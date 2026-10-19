using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MNBL.ProceduralWorld.Diagnostics
{
    public static class GenerationProbe
    {
        public static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static string Run(uint seed, string store, CancellationToken token)
        {
            return RunDiagnostic(seed, store, token, "normal", null, null);
        }

        // Callbacks run synchronously on this caller (normally the background worker), never on a UI dispatcher.
        internal static string RunDiagnostic(uint seed, string store, CancellationToken token, string scenario,
            Action<string> onStage, Action<string> onJobCreated)
        {
            if (scenario != "normal" && scenario != "invalid_settings" && scenario != "producer_terminated")
                throw new ArgumentException("Unknown generation diagnostic scenario: " + scenario);
            string job = Guid.NewGuid().ToString("N");
            string jobRoot = Path.GetFullPath(Path.Combine(store, "jobs", job));
            if (Directory.Exists(jobRoot) || File.Exists(jobRoot)) throw new IOException("Job path already exists: " + jobRoot);
            Directory.CreateDirectory(jobRoot);
            var processes = new List<Dictionary<string, object>>();
            var result = new Dictionary<string, object> {
                { "schema", "mnbl.generation-probe" }, { "version", 2 }, { "job", job }, { "jobRoot", jobRoot },
                { "seed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "scenario", scenario },
                { "recipeVersion", 3 }, { "status", "preflight" }, { "processes", processes },
                { "campaignActivated", false }, { "controlledAbort", false }, { "final", false },
                { "failureDocumentState", "not_read" }, { "failureDocument", null }, { "exitCode", null }, { "producerPid", null }
            };
            var stages = new List<string>();
            result["stages"] = stages;
            Action<string> stage = value => {
                stages.Add(value);
                File.AppendAllText(Path.Combine(jobRoot, "stages.log"), DateTime.UtcNow.ToString("O") + " " + value + Environment.NewLine);
                if (onStage != null) onStage(value);
            };
            var timer = Stopwatch.StartNew();
            string split = Path.Combine(jobRoot, "source");
            try
            {
                if (onJobCreated != null) onJobCreated(jobRoot);
                stage("job_created");
                token.ThrowIfCancellationRequested();
                string bin = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string executable = Path.Combine(bin, "fwg.exe"), bridge = Path.Combine(bin, "worldbridge.exe");
                result["producerHash"] = VerifyPin(executable, Path.Combine(bin, "fwg.sha256"));
                result["bridgeHash"] = VerifyPin(bridge, Path.Combine(bin, "worldbridge.sha256"));
                stage("pins_verified");
                string request = Path.Combine(jobRoot, "request.json");
                // Published size minimum is 3. Size 2 produces a v2 request refusal, unlike size 130's plain config error.
                File.WriteAllText(request, new JavaScriptSerializer().Serialize(new {
                    seed, recipe_version = 3, overrides = new { size = scenario == "invalid_settings" ? 2 : 129, phase = 16 }
                }), new UTF8Encoding(false));
                result["requestHash"] = Hash(request);
                stage("request_written");
                RequireEmptyOutput(split);
                result["sourceOutputInitiallyAbsentOrEmpty"] = true;
                string[] arguments = { "generate", "--request", request, "--split", split, "--quiet" };
                var producer = Execute(executable, (string)result["producerHash"], arguments, jobRoot, "producer", token,
                    scenario == "producer_terminated", stage, processes);
                result["exitCode"] = producer["exitCode"];
                result["producerPid"] = producer["pid"];
                result["peakWorkingSetBytes"] = producer["peakWorkingSetBytes"];
                result["controlledAbort"] = producer["controlledAbort"];
                string documentState;
                var failure = ParseFailureDocument(Path.Combine(jobRoot, "stdout.log"), out documentState);
                result["failureDocumentState"] = documentState;
                result["failureDocument"] = failure;
                if (failure != null) WriteJson(Path.Combine(jobRoot, "producer-failure.json"), failure);
                token.ThrowIfCancellationRequested();
                int exit = (int)producer["exitCode"];
                if ((bool)producer["controlledAbort"])
                {
                    if (exit != 1223) throw new InvalidOperationException("Controlled termination exit was not observed; scenario unconfirmed.");
                    result["status"] = "controlled_abort";
                    stage("controlled_abort_closed");
                    return "Controlled abort of owned producer; not a natural crash. See " + jobRoot;
                }
                if (scenario == "producer_terminated")
                    throw new InvalidOperationException("Producer exited before controlled termination; scenario was not exercised.");
                if (exit == 2 && failure != null)
                {
                    result["status"] = "producer_refused";
                    stage("producer_refused");
                    if (scenario == "invalid_settings" && Convert.ToString(failure["field"]) == "size" && Convert.ToString(failure["code"]) == "INVALID_INPUT" && Convert.ToInt32(failure["received"]) == 2)
                        return "Pinned producer refused the deliberately invalid size request. See " + jobRoot;
                    throw new InvalidDataException("Producer returned a structured refusal; see " + jobRoot);
                }
                if (exit != 0)
                {
                    result["status"] = exit == 2 ? "producer_protocol_error" : "producer_exit_failure";
                    throw new InvalidOperationException("Producer exit " + exit + " (failure document: " + documentState + "); see " + jobRoot);
                }
                if (failure != null || scenario != "normal")
                    throw new InvalidDataException("Producer outcome disagrees with requested scenario/exit contract.");
                if (!File.Exists(Path.Combine(split, "world.json"))) throw new InvalidDataException("No split world.json was produced.");
                result["status"] = "source_generated";
                stage("source_generated");
                string output = Path.Combine(jobRoot, "diagnostic");
                RequireEmptyOutput(output);
                result["readerOutputInitiallyAbsentOrEmpty"] = true;
                var reader = Execute(bridge, (string)result["bridgeHash"], new[] { "--source", split, "--out", output }, jobRoot, "reader", token, false, stage, processes);
                WriteJson(Path.Combine(jobRoot, "reader-result.json"), reader);
                token.ThrowIfCancellationRequested();
                if ((int)reader["exitCode"] != 0) { result["status"] = "reader_failed"; throw new InvalidDataException("Source reader failed; see " + jobRoot); }
                string heightfield = Path.Combine(output, "heightfield.bin"), validation = Path.Combine(output, "source-validation.json");
                if (!File.Exists(heightfield) || new FileInfo(heightfield).Length == 0 || !File.Exists(validation))
                    throw new InvalidDataException("Source reader did not publish a validated heightfield.");
                result["heightfieldHash"] = Hash(heightfield);
                result["sourceValidationHash"] = Hash(validation);
                stage("reader_validated");
                token.ThrowIfCancellationRequested();
                stage("publishing_diagnostic");
                token.ThrowIfCancellationRequested();
                string marker = Path.Combine(Path.GetFullPath(store), "latest-heightfield-" + seed + ".json");
                string pending = marker + "." + job + ".pending";
                WriteJson(pending, new { job, seed, heightfieldHash = result["heightfieldHash"], campaignReady = false });
                token.ThrowIfCancellationRequested();
                if (File.Exists(marker)) File.Replace(pending, marker, null); else File.Move(pending, marker);
                result["status"] = "source_generated";
                stage("diagnostic_published");
                return "Seed " + seed + " source and heightfield generated. Ready for diagnostic rendering; campaign activation is not implemented.";
            }
            catch (OperationCanceledException) { result["status"] = "cancelled"; throw; }
            catch (Exception error)
            {
                if (!new[] { "producer_refused", "producer_protocol_error", "producer_exit_failure", "reader_failed" }.Contains((string)result["status"]))
                    result["status"] = "diagnostic_failed";
                result["error"] = error.ToString();
                throw;
            }
            finally
            {
                timer.Stop();
                result["elapsedMilliseconds"] = timer.ElapsedMilliseconds;
                result["cancelRequested"] = token.IsCancellationRequested;
                result["files"] = Inventory(split);
                result["final"] = true;
                WriteJson(Path.Combine(jobRoot, "result.json"), result);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint Size, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
        internal static Dictionary<string, object> RunSurfaceEncoder(string[] arguments, string jobRoot, CancellationToken token)
        {
            string bin = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string bridge = Path.Combine(bin, "worldbridge.exe");
            string hash = VerifyPin(bridge, Path.Combine(bin, "worldbridge.sha256"));
            var processes = new List<Dictionary<string, object>>();
            try
            {
                return Execute(bridge, hash, arguments, jobRoot, "surface_encoder", token, false,
                    stage => File.AppendAllText(Path.Combine(jobRoot, "encoder-stages.log"), DateTime.UtcNow.ToString("O") + " " + stage + Environment.NewLine), processes);
            }
            finally { WriteJson(Path.Combine(jobRoot, "encoder-processes.json"), processes); }
        }

        // MMR-44: the pinned worldbridge run as an owned child (the generation job's request, source and compile steps). On cancellation
        // requestStop asks the bridge to stop (its cancel file) and the owned job is terminated after stopGraceMs if it has not exited;
        // tick runs about every 500 ms while the bridge runs (progress polling). Logs are <role>-stdout.log and <role>-stderr.log in jobRoot.
        // MMR-49: every process this module started in this process's lifetime, by role (the producer is the generator; the surface encoder
        // rebuilds a scene from a bundle). The loader records the counts before and after a load.
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ProcessStarts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        internal static Dictionary<string, object> RunBridge(string[] arguments, string jobRoot, string role, CancellationToken token, Action requestStop, int stopGraceMs,
            Action tick, List<Dictionary<string, object>> processes)
        {
            string bin = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string bridge = Path.Combine(bin, "worldbridge.exe");
            string hash = VerifyPin(bridge, Path.Combine(bin, "worldbridge.sha256"));
            return Execute(bridge, hash, arguments, jobRoot, role, token, false,
                stage => File.AppendAllText(Path.Combine(jobRoot, "stages.log"), DateTime.UtcNow.ToString("O") + " " + stage + Environment.NewLine), processes, requestStop, stopGraceMs, tick);
        }

        private static string VerifyPin(string binary, string pin)
        {
            string actual = Hash(binary), expected = File.ReadAllText(pin).Trim();
            if (!String.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidDataException("Binary differs from pinned build: " + binary + "; expected " + expected + ", actual " + actual);
            return actual;
        }

        internal static void RequireEmptyOutput(string output)
        {
            if (File.Exists(output) || (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()))
                throw new IOException("Output must be absent or empty: " + output);
        }

        private static Dictionary<string, object> Execute(string executable, string expectedHash, string[] arguments, string jobRoot, string role,
            CancellationToken token, bool controlledAbort, Action<string> stage, List<Dictionary<string, object>> processes,
            Action requestStop = null, int stopGraceMs = 0, Action tick = null)
        {
            string actualHash = Hash(executable);
            if (actualHash != expectedHash) throw new InvalidDataException("Binary changed after preflight: " + executable);
            string commandLine = String.Join(" ", arguments.Select(Quote));
            var receipt = new Dictionary<string, object> {
                { "role", role }, { "executable", executable }, { "executableHash", actualHash },
                { "arguments", arguments }, { "windowsArguments", commandLine }, { "workingDirectory", jobRoot },
                { "started", false }, { "processClosed", false }, { "controlledAbort", false }, { "peakWorkingSetBytes", 0L }
            };
            processes.Add(receipt);
            var start = new ProcessStartInfo(executable, commandLine) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = jobRoot
            };
            var watch = Stopwatch.StartNew();
            using (var process = new Process { StartInfo = start })
            using (var job = new OwnedProcessJob())
            {
                Task stdout = null, stderr = null;
                bool started = false;
                long peak = 0;
                Func<bool> sample = () => {
                    var memory = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf(typeof(ProcessMemoryCounters)) };
                    if (!GetProcessMemoryInfo(process.Handle, ref memory, memory.Size))
                    { receipt["memorySampleWin32Error"] = Marshal.GetLastWin32Error(); return false; }
                    peak = Math.Max(peak, checked((long)memory.PeakWorkingSetSize.ToUInt64()));
                    return true;
                };
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!process.Start()) throw new IOException("Process.Start returned false: " + executable);
                    ProcessStarts.AddOrUpdate(role, 1, (k, n) => n + 1);   // MMR-49: what a load may prove it never started
                    started = true; receipt["started"] = true; receipt["pid"] = process.Id;
                    job.Assign(process); receipt["ownedJobAssigned"] = true;
                    string prefix = role == "producer" ? "" : role + "-";
                    stdout = Drain(process.StandardOutput, Path.Combine(jobRoot, prefix + "stdout.log"));
                    stderr = Drain(process.StandardError, Path.Combine(jobRoot, prefix + "stderr.log"));
                    // Without requestStop a cancellation terminates the owned tree at once; with it the child is asked first.
                    using (token.Register(() => { if (requestStop == null) job.Terminate(); }))
                    {
                        stage(role + "_started");
                        sample();
                        long stopAt = -1, tickAt = 0;
                        while (!process.WaitForExit(25))
                        {
                            sample();
                            if (requestStop != null && token.IsCancellationRequested)
                            {
                                if (stopAt < 0)
                                {
                                    stopAt = watch.ElapsedMilliseconds; receipt["stopRequestedAtMs"] = stopAt;
                                    try { requestStop(); } catch (Exception error) { receipt["stopRequestError"] = error.Message; }
                                    stage(role + "_stop_requested");
                                }
                                else if (watch.ElapsedMilliseconds - stopAt > stopGraceMs && !receipt.ContainsKey("terminatedAfterGraceMs"))
                                {
                                    receipt["terminatedAfterGraceMs"] = watch.ElapsedMilliseconds - stopAt;
                                    job.Terminate();
                                    stage(role + "_terminated");
                                }
                            }
                            if (tick != null && watch.ElapsedMilliseconds - tickAt >= 500) { tickAt = watch.ElapsedMilliseconds; tick(); }
                            if (controlledAbort && watch.ElapsedMilliseconds >= 150 && !token.IsCancellationRequested)
                            {
                                // Only this newly assigned process tree is targeted; never locate a process by name.
                                job.Terminate();
                                receipt["controlledAbort"] = true;
                                stage("controlled_abort_requested");
                                if (!process.WaitForExit(10000)) throw new TimeoutException("Owned producer did not close after controlled abort.");
                                break;
                            }
                        }
                    }
                }
                catch (Exception e) { receipt["error"] = e.ToString(); throw; }
                finally
                {
                    if (started)
                    {
                        if (!process.HasExited)
                        {
                            job.Terminate();
                            if (!process.WaitForExit(2000)) { process.Kill(); if (!process.WaitForExit(10000)) throw new TimeoutException("Owned process closure unconfirmed."); }
                        }
                        receipt["finalMemorySampleSucceeded"] = sample(); // Retained native handle permits the final exited-process sample.
                        receipt["exitCode"] = process.ExitCode;
                        receipt["processClosed"] = process.HasExited;
                        receipt["finalMemorySampleAttempted"] = true;
                    }
                    job.Terminate(); // Close remaining assigned descendants before waiting for pipe EOF.
                    var drains = new[] { stdout, stderr }.Where(t => t != null).ToArray();
                    receipt["peakWorkingSetBytes"] = peak;
                    receipt["elapsedMilliseconds"] = watch.ElapsedMilliseconds;
                    receipt["cancelRequested"] = token.IsCancellationRequested;
                    if (drains.Length > 0 && !Task.WaitAll(drains, 10000)) throw new TimeoutException("Owned process logs did not drain.");
                }
            }
            stage(role + "_closed");
            return receipt;
        }

        internal static Dictionary<string, object> ParseFailureDocument(string stdout, out string state)
        {
            state = "absent";
            string text = File.ReadAllText(stdout).Trim();
            if (text.Length == 0) return null;
            try
            {
                var value = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                object schema, version, code, message, field;
                if (value == null || !value.TryGetValue("schema", out schema) || !Equals(schema, "fantasy-world-generator.failure"))
                { state = "not_failure_document"; return null; }
                if (!value.TryGetValue("schema_version", out version) || !(version is int) || (int)version != 2 ||
                    !value.TryGetValue("code", out code) || !(code is string) || String.IsNullOrEmpty((string)code) ||
                    !value.TryGetValue("message", out message) || !(message is string) ||
                    !value.TryGetValue("field", out field) || !(field is string))
                { state = "unsupported_or_invalid_failure_document"; return null; }
                state = "failure_v2";
                return value;
            }
            catch (ArgumentException) { state = "malformed_json"; return null; }
            catch (InvalidOperationException) { state = "malformed_json"; return null; }
        }

        private static object[] Inventory(string directory)
        {
            if (!Directory.Exists(directory)) return new object[0];
            return Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => (object)new { path = p.Substring(directory.Length + 1).Replace('\\', '/'), length = new FileInfo(p).Length, sha256 = Hash(p) }).ToArray();
        }

        private static void WriteJson(string path, object value)
        {
            File.WriteAllText(path, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(value), new UTF8Encoding(false));
        }

        private static async Task Drain(StreamReader reader, string path)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                var buffer = new char[4096]; int count; long written = 0;
                while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
                {
                    var keep = (int)Math.Min(count, Math.Max(0, 1048576 - written));
                    if (keep > 0) await writer.WriteAsync(buffer, 0, keep).ConfigureAwait(false);
                    written += keep;
                }
            }
        }

        // Windows CRT argv quoting, including embedded quotes and trailing backslashes.
        internal static string Quote(string argument)
        {
            if (argument == null) throw new ArgumentNullException("argument");
            var text = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { text.Append('\\', slashes * 2 + 1); text.Append(c); }
                else { text.Append('\\', slashes); text.Append(c); }
                slashes = 0;
            }
            text.Append('\\', slashes * 2); text.Append('"');
            return text.ToString();
        }
    }
}


