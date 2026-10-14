using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace MNBL.ProceduralWorld.Automation
{
    // Runs several scene_query steps in one game process. A step may first replace the files of an owned scene
    // slot (copyFrom -> copyTo, both relative to this module's folder), which tests same-process slot rewrites.
    // Args: steps [{ sceneName, copyFrom?, copyTo?, ...scene_query args }], plus defaults shared by every step.
    internal static class SceneSequenceProbe
    {
        public static IEnumerator Run(ProbeContext context)
        {
            var steps = context.Args.ContainsKey("steps") ? context.Args["steps"] as IEnumerable : null;
            if (steps == null) throw new ArgumentException("scene_sequence requires args.steps.");
            var module = new FileInfo(typeof(SceneSequenceProbe).Assembly.Location).Directory.Parent.Parent.FullName;
            var results = new List<object>();
            context.Outputs["moduleRoot"] = module;
            context.Outputs["steps"] = results;
            int index = 0;
            foreach (var raw in steps)
            {
                var stepArgs = new Dictionary<string, object>(context.Args);
                stepArgs.Remove("steps");
                foreach (var pair in (Dictionary<string, object>)raw) stepArgs[pair.Key] = pair.Value;
                int number = index;
                var step = new ProbeContext(stepArgs, context.Directory, context.ProcessId, s => context.Stage("step" + number + " " + s), context.Checkpoint);
                results.Add(step.Outputs);
                if (step.Has("copyFrom"))
                {
                    context.Stage("step" + number + " copy");
                    step.Outputs["copied"] = CopySlot(module, step.Str("copyFrom", null), step.Str("copyTo", null));
                    context.Checkpoint();
                }
                var inner = SceneQueryProbe.Run(step);
                while (inner.MoveNext()) yield return inner.Current;
                context.Checkpoint();
                index++;
            }
        }

        private static List<object> CopySlot(string module, string from, string to)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) throw new ArgumentException("copyFrom and copyTo are both required.");
            var source = Contained(module, from);
            var target = Contained(module, to);
            if (!target.StartsWith(Path.Combine(module, "SceneObj", "mnbl_"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("copyTo must be an owned SceneObj/mnbl_* slot.");
            if (!Directory.Exists(source) || !Directory.Exists(target)) throw new DirectoryNotFoundException("Slot source or target is missing.");
            var copied = new List<object>();
            foreach (var file in Directory.GetFiles(source))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected slot file: " + file);
                var destination = Path.Combine(target, Path.GetFileName(file));
                File.Copy(file, destination, true);
                string digest;
                using (var stream = File.OpenRead(destination))
                using (var sha = SHA256.Create())
                    digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                copied.Add(new Dictionary<string, object> { { "file", Path.GetFileName(file) }, { "sha256", digest } });
            }
            return copied;
        }

        private static string Contained(string module, string relative)
        {
            var full = Path.GetFullPath(Path.Combine(module, relative));
            if (!full.StartsWith(module + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Path escapes the module folder: " + relative);
            return full;
        }
    }
}
