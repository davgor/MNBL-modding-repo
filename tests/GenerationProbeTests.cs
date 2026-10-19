using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using MNBL.ProceduralWorld.Diagnostics;

internal static class GenerationProbeTests
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Dictionary<string, object> Read(string path) { return Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path)); }
    private static void Closed(Dictionary<string, object> result)
    {
        foreach (Dictionary<string, object> process in (System.Collections.IEnumerable)result["processes"])
        {
            if (!(bool)process["started"]) continue;
            Check((bool)process["processClosed"], "Process closure was not recorded.");
            Check((bool)process["finalMemorySampleAttempted"], "Final memory sample absent.");
            Check((bool)process["finalMemorySampleSucceeded"], "Final native memory sample failed.");
            int pid = Convert.ToInt32(process["pid"]);
            try { using (var p = Process.GetProcessById(pid)) Check(p.HasExited, "Owned process remains alive: " + pid); }
            catch (ArgumentException) { }
            Check(((System.Collections.IEnumerable)process["arguments"]).Cast<object>().Any(), "Missing argument array.");
        }
    }
    private static Dictionary<string, object> Run(string root, uint seed, string scenario, CancellationToken token,
        Action<string, string> action = null, Type expected = null)
    {
        string job = null; int created = 0, caller = Thread.CurrentThread.ManagedThreadId;
        Exception caught = null;
        try
        {
            Console.WriteLine(GenerationProbe.RunDiagnostic(seed, root, token, scenario, stage => {
                Check(Thread.CurrentThread.ManagedThreadId == caller, "Callback unexpectedly dispatched threads.");
                if (action != null) action(stage, job);
            }, path => {
                created++; job = path;
                Check(Path.IsPathRooted(path) && Directory.Exists(path), "Job callback not absolute/created.");
            }));
        }
        catch (Exception error) { caught = error; }
        Check(created == 1, "Job callback count != 1");
        if (expected == null && caught != null) throw caught;
        Check(expected == null || (caught != null && expected.IsInstanceOfType(caught)), "Wrong/missing failure: " + caught);
        var result = Read(Path.Combine(job, "result.json"));
        Check((bool)result["final"], "Missing final receipt"); Closed(result);
        Console.WriteLine("CHECK " + scenario + ": " + result["status"] + " " + job);
        return result;
    }
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--echo") { Console.OutputEncoding = new System.Text.UTF8Encoding(false); Console.Write(Json.Serialize(args.Skip(1).ToArray())); return 0; }
        try
        {
            string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            // Exercise actual Windows ProcessStartInfo/CRT argv parsing, not a mirror of the quoting implementation.
            string[] values = { "", "plain", "with space", "trailing\\", "a\\\"b", "quote\"", "spaces and trailing\\", "雪" };
            using (var echo = Process.Start(new ProcessStartInfo(typeof(GenerationProbeTests).Assembly.Location,
                String.Join(" ", new[] { "--echo" }.Concat(values).Select(GenerationProbe.Quote))) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 }))
            {
                string output = echo.StandardOutput.ReadToEnd(); echo.WaitForExit();
                var actual = Json.Deserialize<string[]>(output);
                Check(actual.SequenceEqual(values), "Windows argv roundtrip mismatch");
            }
            string[] hashes = new string[2];
            for (int i = 0; i < 2; i++)
            {
                var result = Run(root, (uint)(42 + i), "normal", CancellationToken.None);
                Check((string)result["status"] == "source_generated", "Normal generation did not complete.");
                var marker = Read(Path.Combine(root, "latest-heightfield-" + (42 + i) + ".json"));
                var height = Path.Combine(root, "jobs", (string)marker["job"], "diagnostic", "heightfield.bin");
                hashes[i] = GenerationProbe.Hash(height);
                Check(hashes[i] == (string)marker["heightfieldHash"] && new FileInfo(height).Length == 133140, "Heightfield marker/content mismatch");
            }
            Check(hashes[0] != hashes[1], "Distinct seeds produced identical heightfield");
            string original = File.ReadAllText(Path.Combine(root, "latest-heightfield-42.json"));
            var refused = Run(root, 42, "invalid_settings", CancellationToken.None);
            Check((string)refused["status"] == "producer_refused" && Convert.ToInt32(refused["exitCode"]) == 2, "Expected structured exit-2 refusal");
            var failure = (Dictionary<string, object>)refused["failureDocument"];
            Check((string)failure["field"] == "size" && Convert.ToInt32(failure["schema_version"]) == 2, "Failure schema mismatch");
            var aborted = Run(root, 42, "producer_terminated", CancellationToken.None);
            Check((string)aborted["status"] == "controlled_abort" && (bool)aborted["controlledAbort"], "Controlled termination not exercised");
            foreach (string cancelStage in new[] { "producer_started", "reader_started" })
                using (var cancel = new CancellationTokenSource())
                {
                    var cancelled = Run(root, 42, "normal", cancel.Token,
                        (stage, job) => { if (stage == cancelStage) cancel.Cancel(); }, typeof(OperationCanceledException));
                    Check((string)cancelled["status"] == "cancelled", "Cancellation receipt mismatch");
                }
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                Run(root, 42, "normal", cancel.Token, null, typeof(OperationCanceledException));
            }
            Run(root, 42, "normal", CancellationToken.None, (stage, job) => {
                if (stage == "request_written") { Directory.CreateDirectory(Path.Combine(job, "source")); File.WriteAllText(Path.Combine(job, "source", "existing"), "preserve"); }
            }, typeof(IOException));
            // Mutate only harness-local binary copies; accepted repository/deployment pins and official files are untouched.
            foreach (string name in new[] { "fwg.exe", "worldbridge.exe" })
            {
                string binary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
                long length = new FileInfo(binary).Length;
                try
                {
                    using (var file = new FileStream(binary, FileMode.Append)) file.WriteByte(0);
                    Run(root, 42, "normal", CancellationToken.None, null, typeof(InvalidDataException));
                }
                finally { using (var file = new FileStream(binary, FileMode.Open, FileAccess.Write)) file.SetLength(length); }
            }
            string doc = Path.Combine(root, "failure-fixture.json"), state;
            File.WriteAllText(doc, "{broken"); Check(GenerationProbe.ParseFailureDocument(doc, out state) == null && state == "malformed_json", "Malformed JSON accepted");
            File.WriteAllText(doc, "{\"schema\":\"fantasy-world-generator.failure\",\"schema_version\":1,\"code\":\"x\",\"message\":\"x\"}");
            Check(GenerationProbe.ParseFailureDocument(doc, out state) == null && state == "unsupported_or_invalid_failure_document", "Unknown failure version accepted");
            Check(File.ReadAllText(Path.Combine(root, "latest-heightfield-42.json")) == original, "Failure/cancellation replaced successful marker");
            Console.WriteLine("PASS: two seeds, structured refusal, controlled abort, producer/reader/pre-cancellation, process closure, nonempty output, copied-binary mismatch, malformed failure, Windows argv roundtrip, preserved marker.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}


