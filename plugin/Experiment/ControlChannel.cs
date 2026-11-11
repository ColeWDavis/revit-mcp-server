// [experiment C2c] Harness-only control channel. NOT an MCP tool.
// Localhost TCP, newline-delimited JSON. Port: env REVIT_EXPERIMENT_CONTROL_PORT, default 8765.
//   {"op":"ping"}
//   {"op":"begin_run","run_dir":"...","template":"...rvt"}
//   {"op":"end_run"}
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Experiment
{
    public static class ControlChannel
    {
        private static TcpListener _listener;
        private static string _lastTemplate;
        public static int Port { get; private set; } = 8765;

        public static void Start()
        {
            var env = Environment.GetEnvironmentVariable("REVIT_EXPERIMENT_CONTROL_PORT");
            if (int.TryParse(env, out var p) && p > 0) Port = p;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                new Thread(AcceptLoop) { IsBackground = true, Name = "ExperimentControl" }.Start();
                Helpers.McpLogger.Info("Experiment", $"Control channel listening on 127.0.0.1:{Port}");
            }
            catch (Exception ex)
            {
                Helpers.McpLogger.Error("Experiment", "Control channel failed to start", ex);
            }
        }

        private static void AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); } catch { return; }
                new Thread(() => Serve(client)) { IsBackground = true }.Start();
            }
        }

        private static void Serve(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
            {
                string line;
                try
                {
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (line.StartsWith("GET ") || line.StartsWith("POST ") || line.StartsWith("OPTIONS ")) return; // no HTTP
                        JObject reply;
                        try { reply = Handle(JObject.Parse(line)); }
                        catch (Exception ex) { reply = new JObject { ["ok"] = false, ["error"] = ex.Message }; }
                        writer.WriteLine(reply.ToString(Formatting.None));
                    }
                }
                catch (IOException) { }
            }
        }

        private static JObject Handle(JObject req)
        {
            switch ((string)req["op"])
            {
                case "ping": return Ping();
                case "begin_run": return BeginRun((string)req["run_dir"], (string)req["template"]);
                case "end_run": return EndRun();
                default: return new JObject { ["ok"] = false, ["error"] = "unknown op" };
            }
        }

        private static JObject Ping()
        {
            // Answers even while Revit's main thread is busy; api_responsive says whether it is.
            string doc = null; bool responsive;
            try { doc = (string)ApiRunner.Run(app => app.ActiveUIDocument?.Document?.Title, 3000); responsive = true; }
            catch (TimeoutException) { responsive = false; }
            return new JObject
            {
                ["ok"] = true,
                ["revit_version"] = ExperimentHooks.RevitVersion,
                ["revit_build"] = ExperimentHooks.RevitBuild,
                ["doc"] = doc,
                ["api_responsive"] = responsive,
                ["run_dir"] = RunLog.RunDir,
                ["mcp_listener_running"] = Core.SocketService.Instance.IsRunning,
                ["mcp_port"] = Core.SocketService.Instance.Port,
                ["ifc_option"] = Snapshotter.IfcOption,
            };
        }

        private static JObject BeginRun(string runDir, string template)
        {
            if (string.IsNullOrEmpty(runDir) || string.IsNullOrEmpty(template)) throw new ArgumentException("run_dir and template are required");
            if (!File.Exists(template)) throw new FileNotFoundException("template not found", template);
            RunLog.End();
            Directory.CreateDirectory(runDir);
            string model = Path.Combine(runDir, "model.rvt");
            File.Copy(template, model, false);

            var title = (string)ApiRunner.Run(app =>
            {
                var uidoc = app.OpenAndActivateDocument(model);
                var keep = uidoc.Document.PathName;
                // Close every other open document without saving (the active one can't be closed via the API).
                var others = app.Application.Documents.Cast<Document>()
                    .Where(d => !d.IsLinked && !string.Equals(d.PathName, keep, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var d in others) { try { d.Close(false); } catch { } }
                return uidoc.Document.Title;
            }, 180000);

            _lastTemplate = template;
            RunLog.Begin(runDir);
            RunLog.Write("begin_run", new JObject { ["template"] = template, ["model"] = model, ["doc"] = title, ["revit_build"] = ExperimentHooks.RevitBuild });
            return new JObject { ["ok"] = true, ["doc"] = title, ["model"] = model };
        }

        private static JObject EndRun()
        {
            string runDir = RunLog.RunDir;
            if (runDir == null) return new JObject { ["ok"] = false, ["error"] = "no run in progress" };
            var res = (JObject)ApiRunner.Run(app =>
            {
                var doc = app.ActiveUIDocument.Document;
                var r = new JObject { ["doc"] = doc.Title };
                try { doc.Save(); r["saved"] = true; } catch (Exception ex) { r["saved"] = false; r["save_error"] = ex.Message; }
                var exp = Snapshotter.Export(doc, runDir, "final", true);
                r["final_export"] = exp;
                return r;
            }, 600000);
            RunLog.Write("end_run", res);
            RunLog.End();

            // Park Revit on a blank copy of the template and close the run's model, so the harness can
            // move the run's files away (the active document cannot be closed via the API).
            try
            {
                string idleDir = Path.Combine(Path.GetDirectoryName(runDir) ?? runDir, "_idle");
                Directory.CreateDirectory(idleDir);
                string idle = Path.Combine(idleDir, "idle.rvt");
                if (!File.Exists(idle) && _lastTemplate != null) File.Copy(_lastTemplate, idle);
                if (File.Exists(idle))
                {
                    res["parked"] = (bool)ApiRunner.Run(app =>
                    {
                        var uidoc = app.OpenAndActivateDocument(idle);
                        var keep = uidoc.Document.PathName;
                        foreach (var d in app.Application.Documents.Cast<Document>()
                                     .Where(d => !d.IsLinked && !string.Equals(d.PathName, keep, StringComparison.OrdinalIgnoreCase)).ToList())
                        { try { d.Close(false); } catch { } }
                        return true;
                    }, 180000);
                }
            }
            catch (Exception ex) { res["parked"] = false; res["park_error"] = ex.Message; }

            res["ok"] = true;
            return res;
        }
    }
}
