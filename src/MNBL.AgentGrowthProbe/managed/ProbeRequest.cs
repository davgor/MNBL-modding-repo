using System;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;

namespace MNBL.AgentGrowthProbe
{
    public sealed class ProbeRequest
    {
        public const string EnvironmentKey = "MNBL_PRIVATE_ELEMENT_PROBE_REQUEST";
        public int schemaVersion { get; set; }
        public string runId { get; set; }
        public string createdUtc { get; set; }
        public string gameExecutable { get; set; }
        public string nativeDll { get; set; }
        public string nativeSha256 { get; set; }
        public string hostSha256 { get; set; }
        public string resultPath { get; set; }
        public string ownerPath { get; set; }
        public string claimPath { get; set; }
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 65536 };
        public static ProbeRequest ReadAndValidate(string path, string hostPath, int pid, long startTicks, string executable, DateTime now)
        {
            if (String.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return null;
            path = Path.GetFullPath(path);
            var r = Json.Deserialize<ProbeRequest>(File.ReadAllText(path));
            Guid id; DateTime created;
            if (r == null || r.schemaVersion != 1 || !Guid.TryParseExact(r.runId, "D", out id) || id == Guid.Empty ||
                !DateTime.TryParse(r.createdUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out created) ||
                created.Kind != DateTimeKind.Utc || now < created || now - created > TimeSpan.FromMinutes(10))
                throw new InvalidOperationException("Stale or invalid private probe request.");
            string directory = Path.GetDirectoryName(path);
            if (Path.GetFileName(directory) != r.runId || Path.GetFileName(path) != "request-" + r.runId + ".json")
                throw new InvalidOperationException("Request GUID path differs.");
            RequirePath(r.ownerPath, Path.Combine(directory, "owner.json"));
            RequirePath(r.claimPath, Path.Combine(directory, "claimed.json"));
            RequirePath(r.resultPath, Path.Combine(directory, "result.json"));
            RequirePath(r.nativeDll, Path.Combine(Path.GetDirectoryName(hostPath), "MNBL.NativeElementProbe.dll"));
            RequirePath(r.gameExecutable, executable);
            if (!ProbeInterop.ValidHash(r.nativeSha256) || !ProbeInterop.ValidHash(r.hostSha256) || ProbeInterop.Hash(hostPath) != r.hostSha256)
                throw new InvalidOperationException("Host/request hash differs.");
            var owner = Json.Deserialize<Owner>(File.ReadAllText(r.ownerPath));
            if (owner == null || owner.runId != r.runId || owner.pid != pid || owner.startTicks != startTicks)
                throw new InvalidOperationException("Request is not owned by this exact process.");
            return r;
        }
        private static void RequirePath(string actual, string expected)
        {
            if (String.IsNullOrEmpty(actual) || !Path.IsPathRooted(actual) ||
                !String.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Request path differs.");
        }
        public sealed class Owner
        {
            public string runId { get; set; }
            public int pid { get; set; }
            public long startTicks { get; set; }
        }
        public void Claim(int pid, long startTicks)
        {
            using (var file = new FileStream(claimPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var text = new StreamWriter(file)) text.Write(Json.Serialize(new Owner { runId = runId, pid = pid, startTicks = startTicks }));
        }
        public void Report(object value)
        {
            string temporary = resultPath + ".tmp";
            File.WriteAllText(temporary, Json.Serialize(value));
            if (File.Exists(resultPath)) File.Replace(temporary, resultPath, null); else File.Move(temporary, resultPath);
        }
    }
}
