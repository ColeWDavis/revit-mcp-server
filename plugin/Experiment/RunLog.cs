// [experiment C2a] Run-scoped event log: RUN_DIR/revit_events.jsonl
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Experiment
{
    public static class RunLog
    {
        private static readonly object Lock = new object();
        private static StreamWriter _writer;

        public static volatile string RunDir;
        public static int CmdSeq;          // last MCP command sequence number in this run
        public static int CsharpN;         // last C# payload number
        public static int SnapshotN;       // last snapshot number
        public static int ChangesInCmd;    // doc_changed events (with element changes) during the current command
        public static volatile bool InSnapshot;
        public static volatile bool SkipPng;

        // Filled by ExperimentTraceListener from [RevitMCP][...] trace lines during a command
        public static volatile string LastCsharpException;
        public static volatile string LastCommitStatus;

        public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

        public static void Begin(string runDir)
        {
            lock (Lock)
            {
                CloseNoLock();
                Directory.CreateDirectory(runDir);
                Directory.CreateDirectory(Path.Combine(runDir, "csharp"));
                Directory.CreateDirectory(Path.Combine(runDir, "snapshots"));
                _writer = new StreamWriter(Path.Combine(runDir, "revit_events.jsonl"), true, new UTF8Encoding(false)) { AutoFlush = true };
                CmdSeq = 0; CsharpN = 0; SnapshotN = 0; ChangesInCmd = 0;
                SkipPng = false; InSnapshot = false;
                RunDir = runDir;
            }
        }

        public static void End()
        {
            lock (Lock) CloseNoLock();
        }

        private static void CloseNoLock()
        {
            RunDir = null;
            try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            _writer = null;
        }

        public static void Write(string kind, JObject payload)
        {
            try
            {
                var rec = new JObject { ["t"] = Now(), ["kind"] = kind };
                if (payload != null)
                    foreach (var p in payload.Properties()) rec[p.Name] = p.Value;
                lock (Lock)
                {
                    if (_writer == null) return;
                    _writer.WriteLine(rec.ToString(Formatting.None));
                }
            }
            catch { /* logging must never break Revit or a command */ }
        }
    }

    /// <summary>Captures "[RevitMCP][tag] message" trace lines from any assembly into the run log.</summary>
    public class ExperimentTraceListener : TraceListener
    {
        private const string Prefix = "[RevitMCP][";

        public override void Write(string message) { }

        public override void WriteLine(string message)
        {
            if (message == null || !message.StartsWith(Prefix)) return;
            int close = message.IndexOf(']', Prefix.Length);
            if (close < 0) return;
            string tag = message.Substring(Prefix.Length, close - Prefix.Length);
            string rest = message.Substring(close + 1).Trim();

            if (tag == "csharp_exception") RunLog.LastCsharpException = rest;
            else if (tag == "csharp_commit") RunLog.LastCommitStatus = rest;

            RunLog.Write("trace", new JObject { ["cmd_seq"] = RunLog.CmdSeq, ["tag"] = tag, ["msg"] = rest });
        }
    }
}
