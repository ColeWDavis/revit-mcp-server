// [experiment C2] Runs a function on Revit's main thread (valid API context) and waits for it.
using System;
using System.Collections.Concurrent;
using System.Threading;
using Autodesk.Revit.UI;

namespace revit_mcp_plugin.Experiment
{
    public class ApiRunner : IExternalEventHandler
    {
        private class Job
        {
            public Func<UIApplication, object> Fn;
            public object Result;
            public Exception Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static ApiRunner _instance;
        private static ExternalEvent _event;
        private readonly ConcurrentQueue<Job> _queue = new ConcurrentQueue<Job>();

        /// <summary>Must be called from a valid API context (OnStartup).</summary>
        public static void Init()
        {
            _instance = new ApiRunner();
            _event = ExternalEvent.Create(_instance);
        }

        public static object Run(Func<UIApplication, object> fn, int timeoutMs)
        {
            var job = new Job { Fn = fn };
            _instance._queue.Enqueue(job);
            _event.Raise();
            if (!job.Done.Wait(timeoutMs))
                throw new TimeoutException($"Revit main thread did not run the job within {timeoutMs} ms");
            if (job.Error != null) throw job.Error;
            return job.Result;
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var job))
            {
                try { job.Result = job.Fn(app); }
                catch (Exception ex) { job.Error = ex; }
                finally { job.Done.Set(); }
            }
        }

        public string GetName() => "Experiment API runner";
    }
}
