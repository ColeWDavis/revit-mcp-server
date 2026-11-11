// [experiment C2a/C2b] Revit event logging, per-command logging, C# payload capture, snapshots.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Experiment
{
    public static class ExperimentHooks
    {
        public static string RevitVersion = "";
        public static string RevitBuild = "";

        // H9 static scan (patterns from docs/03-build-spec.md C2a)
        private static readonly (string flag, Regex rx)[] RiskPatterns =
        {
            ("System.IO", new Regex(@"System\.IO")),
            ("File.", new Regex(@"\bFile\.")),
            ("Directory.", new Regex(@"\bDirectory\.")),
            ("Process", new Regex(@"\bProcess\b")),
            ("System.Net", new Regex(@"System\.Net")),
            ("HttpClient", new Regex(@"\bHttpClient\b")),
            ("Registry", new Regex(@"\bRegistry\b")),
            ("Environment.", new Regex(@"\bEnvironment\.")),
        };

        public static void Init(UIControlledApplication app)
        {
            RevitVersion = app.ControlledApplication.VersionNumber;
            RevitBuild = app.ControlledApplication.VersionBuild;
            Trace.Listeners.Add(new ExperimentTraceListener());
            ApiRunner.Init();
            app.ControlledApplication.DocumentChanged += OnDocumentChanged;
            app.ControlledApplication.FailuresProcessing += OnFailuresProcessing;
            app.DialogBoxShowing += OnDialogBoxShowing;
            ControlChannel.Start();
        }

        // ---------- Revit events ----------

        private static JArray Ids(ICollection<ElementId> ids) => new JArray(ids.Select(i => i.Value));

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                if (RunLog.RunDir == null || RunLog.InSnapshot) return;
                var doc = e.GetDocument();
                var added = e.GetAddedElementIds();
                var modified = e.GetModifiedElementIds();
                var deleted = e.GetDeletedElementIds();
                if (added.Count + modified.Count + deleted.Count > 0)
                    Interlocked.Increment(ref RunLog.ChangesInCmd);

                var addedInfo = new JArray();
                foreach (var id in added)
                {
                    var el = doc.GetElement(id);
                    if (el == null) continue;
                    ElementType et = null;
                    try { var tid = el.GetTypeId(); if (tid != ElementId.InvalidElementId) et = doc.GetElement(tid) as ElementType; } catch { }
                    addedInfo.Add(new JObject
                    {
                        ["id"] = id.Value,
                        ["class"] = el.GetType().Name,
                        ["category"] = el.Category?.Name,
                        ["family"] = et?.FamilyName,
                        ["type"] = et?.Name,
                        ["name"] = SafeName(el),
                    });
                }

                RunLog.Write("doc_changed", new JObject
                {
                    ["cmd_seq"] = RunLog.CmdSeq,
                    ["doc"] = doc.Title,
                    ["operation"] = e.Operation.ToString(),
                    ["txn_names"] = new JArray(e.GetTransactionNames()),
                    ["added"] = Ids(added),
                    ["modified"] = Ids(modified),
                    ["deleted"] = Ids(deleted),
                    ["added_info"] = addedInfo,
                });
            }
            catch { }
        }

        private static string SafeName(Element el) { try { return el.Name; } catch { return null; } }

        private static void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            try
            {
                if (RunLog.RunDir == null || RunLog.InSnapshot) return;
                var fa = e.GetFailuresAccessor();
                var msgs = fa.GetFailureMessages();
                if (msgs.Count == 0) return;
                string txn = null; bool? committing = null;
                try { txn = fa.GetTransactionName(); } catch { }
                try { committing = fa.IsTransactionBeingCommitted(); } catch { }
                foreach (var m in msgs)
                {
                    var sev = m.GetSeverity();
                    if (sev != FailureSeverity.Warning) RunLog.PendingFailureHasError = true;
                    RunLog.Write("failure", new JObject
                    {
                        ["cmd_seq"] = RunLog.CmdSeq,
                        ["severity"] = m.GetSeverity().ToString().ToLowerInvariant(),
                        ["desc"] = m.GetDescriptionText(),
                        ["element_ids"] = Ids(m.GetFailingElementIds()),
                        ["failure_guid"] = m.GetFailureDefinitionId().Guid.ToString(),
                        ["txn"] = txn,
                        ["txn_being_committed"] = committing,
                        // Logged before Revit's default handling; the outcome shows up as a later
                        // doc_changed (committed) or its absence / csharp commit_status (rolled back).
                        ["resolution"] = "revit_default",
                    });
                }
            }
            catch { }
        }

        private static void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            try
            {
                string message = null;
                if (e is TaskDialogShowingEventArgs td) message = td.Message;
                else if (e is MessageBoxShowingEventArgs mb) message = mb.Message;

                // Decision 2026-11-10 (Cole, pilot evidence): during a run, Revit's modal warning/error
                // dialog blocks the main thread forever. Press the button a person would: OK for warnings
                // (change kept, warnings stay in the model), Cancel when an error was reported (Revit rolls
                // the change back). Outside a run, dialogs behave normally. Other dialogs are only logged.
                string action = "none (not auto-dismissed)";
                if (RunLog.RunDir != null && e.DialogId == "Dialog_Revit_DocWarnDialog")
                {
                    bool hadError = RunLog.PendingFailureHasError;
                    e.OverrideResult(hadError ? 2 /*IDCANCEL*/ : 1 /*IDOK*/);
                    action = hadError ? "auto_cancel (error reported)" : "auto_ok (warnings only)";
                }
                RunLog.PendingFailureHasError = false;

                RunLog.Write("dialog", new JObject
                {
                    ["cmd_seq"] = RunLog.CmdSeq,
                    ["dialog_id"] = e.DialogId,
                    ["dialog_type"] = e.GetType().Name,
                    ["message"] = message,
                    ["action"] = action,
                });
            }
            catch { }
        }

        // ---------- MCP commands (called from SocketService on the socket thread) ----------

        public class CmdRecord
        {
            public int Seq; public string Method; public string Id; public JToken Params;
            public string Start; public Stopwatch Sw; public int CsharpN;
        }

        public static CmdRecord BeforeCommand(string method, string id, object parameters)
        {
            if (RunLog.RunDir == null) return null;
            try
            {
                var rec = new CmdRecord
                {
                    Seq = Interlocked.Increment(ref RunLog.CmdSeq),
                    Method = method,
                    Id = id,
                    Params = parameters == null ? null : (parameters as JToken ?? JToken.FromObject(parameters)),
                    Start = RunLog.Now(),
                    Sw = Stopwatch.StartNew(),
                };
                Interlocked.Exchange(ref RunLog.ChangesInCmd, 0);
                RunLog.LastCsharpException = null;
                RunLog.LastCommitStatus = null;

                if (method == "send_code_to_revit")
                {
                    rec.CsharpN = Interlocked.Increment(ref RunLog.CsharpN);
                    string code = rec.Params?["code"]?.ToString() ?? "";
                    File.WriteAllText(Path.Combine(RunLog.RunDir, "csharp", $"{rec.CsharpN:D4}.cs"), code);
                }
                return rec;
            }
            catch { return null; }
        }

        public static void AfterCommand(CmdRecord rec, bool ok, string error, object result)
        {
            if (rec == null || RunLog.RunDir == null) return;
            try
            {
                rec.Sw.Stop();
                JToken resultTok = null;
                try { resultTok = result == null ? null : (result as JToken ?? JToken.FromObject(result)); } catch { }

                RunLog.Write("mcp_cmd", new JObject
                {
                    ["seq"] = rec.Seq,
                    ["cmd"] = rec.Method,
                    ["rpc_id"] = rec.Id,
                    ["params"] = rec.Params,
                    ["start"] = rec.Start,
                    ["end"] = RunLog.Now(),
                    ["duration_ms"] = rec.Sw.ElapsedMilliseconds,
                    ["ok"] = ok,
                    ["error"] = error,
                    ["changes"] = RunLog.ChangesInCmd,
                });

                if (rec.Method == "send_code_to_revit") LogCsharp(rec, ok, error, resultTok);

                if (RunLog.ChangesInCmd > 0) Snapshot(rec.Seq);
            }
            catch { }
        }

        private static void LogCsharp(CmdRecord rec, bool ok, string error, JToken result)
        {
            string code = rec.Params?["code"]?.ToString() ?? "";
            bool success = ok && (result?["success"]?.Value<bool>() ?? false);
            string errMsg = result?["errorMessage"]?.ToString();
            if (!ok) errMsg = error;
            bool compileFailed = errMsg != null && errMsg.Contains("Code compilation error");
            var compileErrors = new JArray();
            if (compileFailed)
                foreach (var line in errMsg.Split('\n').Skip(1))
                    if (!string.IsNullOrWhiteSpace(line)) compileErrors.Add(line.Trim());

            RunLog.Write("csharp", new JObject
            {
                ["n"] = rec.CsharpN,
                ["cmd_seq"] = rec.Seq,
                ["file"] = $"csharp/{rec.CsharpN:D4}.cs",
                ["transaction_mode"] = rec.Params?["transactionMode"]?.ToString() ?? "auto",
                ["compile_ok"] = !compileFailed && ok,
                ["compile_errors"] = compileErrors,
                ["runtime_ok"] = success,
                ["exception_shown_to_agent"] = success ? null : errMsg,
                ["exception_inner"] = success ? null : RunLog.LastCsharpException,
                ["commit_status"] = RunLog.LastCommitStatus,
                ["return_value"] = result?["result"],
                ["duration_ms"] = rec.Sw.ElapsedMilliseconds,
                ["risk_flags"] = new JArray(RiskPatterns.Where(p => p.rx.IsMatch(code)).Select(p => p.flag)),
            });
        }

        // ---------- Snapshots (C2b) ----------

        private static void Snapshot(int afterCmdSeq)
        {
            int n = Interlocked.Increment(ref RunLog.SnapshotN);
            string folder = Path.Combine(RunLog.RunDir, "snapshots");
            JObject res;
            try
            {
                res = (JObject)ApiRunner.Run(app => Snapshotter.Export(app.ActiveUIDocument.Document, folder, $"{n:D4}", !RunLog.SkipPng), 180000);
            }
            catch (Exception ex)
            {
                res = new JObject { ["error"] = ex.Message };
            }
            res["n"] = n;
            res["after_cmd_seq"] = afterCmdSeq;
            if ((res["elapsed_ms"]?.Value<long>() ?? 0) > 30000 && !RunLog.SkipPng)
            {
                RunLog.SkipPng = true;
                res["note"] = "export took >30 s; PNGs skipped from now on";
            }
            RunLog.Write("snapshot", res);
        }
    }

    public static class Snapshotter
    {
        public const string IfcOption = "IFC4RV (IFC4 Reference View), default IFCExportOptions otherwise";

        /// <summary>Runs on Revit's main thread. Exports {name}.ifc and optionally {name}.png into folder.</summary>
        public static JObject Export(Document doc, string folder, string name, bool png)
        {
            var res = new JObject();
            var sw = Stopwatch.StartNew();
            RunLog.InSnapshot = true;
            try
            {
                Directory.CreateDirectory(folder);
                try
                {
                    res["ifc_ok"] = ExportIfc(doc, folder, name + ".ifc");
                }
                catch (Exception ex) { res["ifc_ok"] = false; res["ifc_error"] = ex.Message; }
                res["ifc_ms"] = sw.ElapsedMilliseconds;

                if (png)
                {
                    try
                    {
                        var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                            .Where(v => !v.IsTemplate)
                            .OrderBy(v => v.Name == "3D Snapshot" ? 0 : v.Name == "{3D}" ? 1 : 2).FirstOrDefault();
                        if (view == null) res["png_error"] = "no 3D view in document";
                        else
                        {
                            var opts = new ImageExportOptions
                            {
                                ExportRange = ExportRange.SetOfViews,
                                FilePath = Path.Combine(folder, name),
                                ZoomType = ZoomFitType.FitToPage,
                                PixelSize = 1600,
                                FitDirection = FitDirectionType.Horizontal,
                                HLRandWFViewsFileType = ImageFileType.PNG,
                                ShadowViewsFileType = ImageFileType.PNG,
                                ImageResolution = ImageResolution.DPI_150,
                            };
                            opts.SetViewsAndSheets(new List<ElementId> { view.Id });
                            doc.ExportImage(opts);
                            // Revit appends " - 3D View - {name}" to the file name; normalise it.
                            var target = Path.Combine(folder, name + ".png");
                            var produced = new DirectoryInfo(folder).GetFiles(name + "*.png")
                                .Where(f => !string.Equals(f.FullName, target, StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                            if (produced != null) { if (File.Exists(target)) File.Delete(target); produced.MoveTo(target); }
                            res["png_ok"] = File.Exists(target);
                            res["png_view"] = view.Name;
                        }
                    }
                    catch (Exception ex) { res["png_ok"] = false; res["png_error"] = ex.Message; }
                }
            }
            finally
            {
                RunLog.InSnapshot = false;
            }
            res["elapsed_ms"] = sw.ElapsedMilliseconds;
            return res;
        }

        private static bool ExportIfc(Document doc, string folder, string fileName)
        {
            var opts = new IFCExportOptions { FileVersion = IFCVersion.IFC4RV };
            // IFC export needs an open transaction in recent Revit versions; roll it back afterwards
            // so the export leaves no trace in the model.
            if (doc.IsModifiable) return doc.Export(folder, fileName, opts);
            using (var t = new Transaction(doc, "Experiment IFC snapshot"))
            {
                t.Start();
                bool ok = doc.Export(folder, fileName, opts);
                t.RollBack();
                return ok;
            }
        }
    }
}
