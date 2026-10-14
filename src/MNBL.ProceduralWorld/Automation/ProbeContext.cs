using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace MNBL.ProceduralWorld.Automation
{
    // Per-run state handed to an autorun probe: request arguments, stage tracing and JSON outputs.
    internal sealed class ProbeContext
    {
        private readonly Action<string> trace;
        private readonly Action checkpoint;
        private readonly List<Tuple<Action, Func<bool>>> ownedWork = new List<Tuple<Action, Func<bool>>>();

        public ProbeContext(Dictionary<string, object> args, string directory, int processId, Action<string> trace, Action checkpoint)
        {
            Args = args ?? new Dictionary<string, object>();
            Directory = directory;
            ProcessId = processId;
            this.trace = trace;
            this.checkpoint = checkpoint;
        }

        public Dictionary<string, object> Args { get; private set; }
        public Dictionary<string, object> Outputs { get; } = new Dictionary<string, object>();
        public string Directory { get; private set; }
        public int ProcessId { get; private set; }
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public double Seconds { get { return Clock.Elapsed.TotalSeconds; } }

        // Flushed to the progress log before the caller continues, so a native crash leaves the last stage on disk.
        public void Stage(string name) { trace(name); }

        // Rewrites the (non-final) result file with the outputs gathered so far.
        public void Checkpoint() { checkpoint(); }

        // A task is finished only after its child-job finally/disposal path has completed.
        internal void TrackOwnedWork(Action cancel, Func<bool> hasFinished)
        {
            ownedWork.Add(Tuple.Create(cancel, hasFinished));
        }

        internal void CancelOwnedWork()
        {
            foreach (var work in ownedWork) if (!work.Item2()) work.Item1();
        }

        internal bool OwnedWorkFinished
        {
            get
            {
                foreach (var work in ownedWork) if (!work.Item2()) return false;
                return true;
            }
        }

        public bool Has(string key) { return Args.ContainsKey(key) && Args[key] != null; }

        public string Str(string key, string fallback)
        {
            object value;
            return Args.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : fallback;
        }

        public bool Bool(string key, bool fallback)
        {
            object value;
            if (!Args.TryGetValue(key, out value) || value == null) return fallback;
            if (value is bool) return (bool)value;
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        public double Num(string key, double fallback)
        {
            object value;
            return Args.TryGetValue(key, out value) && value != null ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : fallback;
        }

        public int Int(string key, int fallback)
        {
            object value;
            return Args.TryGetValue(key, out value) && value != null ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : fallback;
        }

        // A JSON list of numeric pairs, e.g. [[50,80],[250,400]].
        public List<double[]> Pairs(string key)
        {
            var result = new List<double[]>();
            object value;
            if (!Args.TryGetValue(key, out value) || value == null) return result;
            var list = value as IEnumerable;
            if (list == null || value is string) throw new ArgumentException("Argument '" + key + "' must be a list of [x,y] pairs.");
            foreach (var item in list)
            {
                var pair = new List<double>();
                var inner = item as IEnumerable;
                if (inner == null || item is string) throw new ArgumentException("Argument '" + key + "' contains a non-list entry.");
                foreach (var number in inner) pair.Add(Convert.ToDouble(number, CultureInfo.InvariantCulture));
                if (pair.Count != 2) throw new ArgumentException("Argument '" + key + "' entries must have exactly two numbers.");
                if (double.IsNaN(pair[0]) || double.IsNaN(pair[1]) || double.IsInfinity(pair[0]) || double.IsInfinity(pair[1]))
                    throw new ArgumentException("Argument '" + key + "' contains a non-finite number.");
                result.Add(pair.ToArray());
            }
            return result;
        }

        // JavaScriptSerializer would emit bare NaN/Infinity tokens (invalid JSON); keep them as strings instead.
        public static object F(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return value.ToString(CultureInfo.InvariantCulture);
            return value;
        }
    }
}
