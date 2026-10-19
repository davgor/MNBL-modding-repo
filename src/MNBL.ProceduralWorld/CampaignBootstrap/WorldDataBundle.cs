using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-29: the host side of an immutable world data bundle written by worldbridge --bundle (worldgen/contracts/
    // bundle-manifest.schema.json, docs/world-bundle.md). Before any resource is constructed from a bundle the host checks
    // it is sealed (DATA_READY names this manifest), hash-complete (every inventory file present with its size and SHA-256,
    // nothing unlisted, no links, every required file), that its WorldContentId recomputes from the files, and that it is
    // compatible with this host: compiled/compatibility.json (inside the identity) names the installed Bannerlord and War
    // Sails versions and only runtime capabilities the host provides. The rules are worldbridge validate()'s; one set of
    // damaged bundles runs through both, and through tools/summarize_bundle.py (tools/test_bundle_parity.py).
    // A verified bundle is only DATA_READY. Activation is a separate step: U03 writes an activation receipt outside the
    // bundle after W02-W05 and C07, so this class never activates anything and a bundle holding a receipt is refused. Pure
    // managed (no TaleWorlds types), so tests/WorldDataBundleTests.cs runs it without the game; the caller supplies the
    // installed versions and capabilities. Verification never throws for a damaged bundle: it reports problems, and Open /
    // OpenForSave turn them into one RejectedException with what to do.
    internal sealed class WorldDataBundle
    {
        public const string Schema = "mnbl.world-bundle";
        public const string ReadySchema = "mnbl.data-ready";
        public const string CompatibilitySchema = "mnbl.bundle-compatibility";
        public const string ContentIdPrefix = "mnbl.world-content-id/1\n";
        public const string SaveIdentitySchema = "mnbl.world-bundle-binding.v1";
        private const long MaxDocumentBytes = 64L << 20;
        private const int MaxPathBytes = 120;   // worldbridge kMaxBundlePath: the store root plus a bundle path stays below MAX_PATH
        private static readonly string[] Roots = { "source", "compiled", "policies" };
        private static readonly string[] Devices = { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
                                                     "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9" };
        // worldbridge required_files(): the documented layout.
        public static readonly string[] RequiredFiles = {
            "compiled/campaign/campaign.json", "compiled/campaign/id-map.json", "compiled/compatibility.json", "compiled/terrain/height-q16.bin",
            "compiled/terrain/heightfield.bin", "compiled/terrain/masks.bin", "compiled/terrain/projection.json", "compiled/terrain/weights.bin",
            "compiled/travel/cells.bin", "compiled/travel/ports.json", "compiled/travel/travel-graph.json", "compiled/validation.json", "source/world.json" };

        public sealed class HostProfile
        {
            public HostProfile(string gameVersion, string navalVersion, IEnumerable<string> capabilities)
            {
                GameVersion = gameVersion; NavalVersion = navalVersion;
                Capabilities = new HashSet<string>(capabilities ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            }
            public string GameVersion { get; private set; }
            public string NavalVersion { get; private set; }
            public ISet<string> Capabilities { get; private set; }
        }

        public sealed class Entry
        {
            public string Path { get; internal set; }
            public long Bytes { get; internal set; }
            public string Sha256 { get; internal set; }
        }

        // A refused bundle: the message is meant for the player and says what to do; Problems lists every check that failed.
        public sealed class RejectedException : IOException
        {
            public RejectedException(string message, IList<string> problems) : base(message) { Problems = problems; }
            public IList<string> Problems { get; private set; }
        }

        public string Directory { get; private set; }
        public string WorldContentId { get; private set; }
        public string ManifestSha256 { get; private set; }
        public Dictionary<string, object> Manifest { get; private set; }
        private readonly Dictionary<string, Entry> inventory = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public IEnumerable<Entry> Inventory { get { return inventory.Values; } }

        // Opens a published bundle (its directory named by its WorldContentId) or throws RejectedException.
        public static WorldDataBundle Open(string directory, HostProfile host)
        {
            WorldDataBundle bundle;
            var problems = Verify(directory, host, true, out bundle);
            if (problems.Count > 0)
                throw new RejectedException("The world data bundle in " + directory + " cannot be used: " + String.Join("; ", problems) + ". " + Advice(problems), problems);
            return bundle;
        }

        // Every failed check, empty when the bundle is usable by this host. nameMustMatch is false for a candidate under
        // another name (a copy being inspected); a published bundle's directory name is its WorldContentId.
        public static List<string> Problems(string directory, HostProfile host, bool nameMustMatch)
        {
            WorldDataBundle bundle;
            return Verify(directory, host, nameMustMatch, out bundle);
        }

        // The binding a save stores. WorldContentId covers every file, compiled/compatibility.json included, so it names the
        // exact world; the manifest also records provenance (which compiler binary first published it), which is not part
        // of what a save depends on.
        public Dictionary<string, string> SaveIdentity()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal) { ["schema"] = SaveIdentitySchema, ["worldContentId"] = WorldContentId };
        }

        // Resolves the bundle a saved campaign names under the bundle root. A missing, damaged or incompatible bundle is
        // refused with a message saying what to restore or install; a saved world is never regenerated.
        public static WorldDataBundle OpenForSave(string root, IDictionary<string, string> saveIdentity, HostProfile host)
        {
            string schema, id;
            if (saveIdentity == null || !saveIdentity.TryGetValue("schema", out schema) || schema != SaveIdentitySchema ||
                !saveIdentity.TryGetValue("worldContentId", out id) || !IsSha256(id))
                throw new RejectedException("This save does not name a world data bundle (" + SaveIdentitySchema + "), so its world cannot be found. It was not made by this version of MNBL; load it with the version that created it.",
                                            new List<string> { "save identity missing or malformed" });
            string directory = Path.Combine(root, id);
            if (!System.IO.Directory.Exists(directory))
                throw new RejectedException("The world data bundle " + id + " this campaign was saved with is missing from " + root +
                                            ". Restore that bundle directory from a backup; MNBL never regenerates a saved world automatically.",
                                            new List<string> { "missing bundle " + id });
            WorldDataBundle bundle;
            var problems = Verify(directory, host, true, out bundle);
            if (problems.Count > 0)
                throw new RejectedException("The world data bundle " + id + " this campaign was saved with cannot be used: " + String.Join("; ", problems) + ". " + Advice(problems), problems);
            return bundle;
        }

        // A bundle file's bytes, verified again against the inventory at the moment of use.
        public byte[] ReadVerified(string path)
        {
            Entry entry;
            if (!inventory.TryGetValue(path, out entry)) throw new InvalidDataException("Not a file of world data bundle " + WorldContentId + ": " + path);
            var bytes = File.ReadAllBytes(Native(Directory, path));
            if (bytes.LongLength != entry.Bytes || Sha256(bytes) != entry.Sha256) throw new InvalidDataException("World data bundle file changed after verification: " + path);
            return bytes;
        }

        private static string Advice(IList<string> problems)
        {
            if (problems.Any(p => p.StartsWith("built for", StringComparison.Ordinal)))
                return "Install the Bannerlord and War Sails versions it was built for; there is no migration, and bundles are never rewritten at load.";
            if (problems.Any(p => p.StartsWith("requires runtime capability", StringComparison.Ordinal)))
                return "Update MNBL to a version that provides the listed capabilities.";
            return "Restore an intact copy of the bundle; MNBL never repairs or regenerates a bundle at load.";
        }

        private static List<string> Verify(string directory, HostProfile host, bool nameMustMatch, out WorldDataBundle bundle)
        {
            bundle = null;
            var problems = new List<string>();
            try
            {
                directory = Path.GetFullPath(directory);
                if (directory.Length > Path.GetPathRoot(directory).Length) directory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!System.IO.Directory.Exists(directory)) { problems.Add("not a bundle directory: " + directory); return problems; }
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { problems.Add("the bundle directory is a link"); return problems; }
                string manifestFile = Path.Combine(directory, "manifest.json"), readyFile = Path.Combine(directory, "DATA_READY");
                if (!File.Exists(manifestFile)) { problems.Add("manifest.json is missing"); return problems; }
                var manifestBytes = ReadDocument(manifestFile);
                var manifest = Json(manifestBytes);
                var result = new WorldDataBundle { Directory = directory, ManifestSha256 = Sha256(manifestBytes), Manifest = manifest };
                if (Text(manifest, "schema") != Schema || Number(manifest, "version") != 1) { problems.Add("manifest is not " + Schema + " version 1"); return problems; }
                result.WorldContentId = Text(manifest, "worldContentId");
                if (!IsSha256(result.WorldContentId)) problems.Add("manifest worldContentId is not a SHA-256");

                // DATA_READY: written last by the compiler; it names this manifest and this content.
                if (!File.Exists(readyFile)) problems.Add("DATA_READY is missing: the bundle was never sealed");
                else
                {
                    var ready = Json(ReadDocument(readyFile));
                    if (Text(ready, "schema") != ReadySchema || Number(ready, "version") != 1) problems.Add("DATA_READY is not " + ReadySchema + " version 1");
                    if (Text(ready, "manifestSha256") != result.ManifestSha256) problems.Add("DATA_READY names a different manifest");
                    if (Text(ready, "worldContentId") != result.WorldContentId) problems.Add("DATA_READY names a different worldContentId");
                }
                if (nameMustMatch && Path.GetFileName(directory) != result.WorldContentId) problems.Add("the directory name is not the worldContentId");

                // Inventory: well-formed rows with safe paths, strictly sorted by UTF-8 bytes, and totals that add up.
                var rows = Member(manifest, "inventory") as IList;
                if (rows == null) { problems.Add("manifest inventory is missing"); return problems; }
                string previous = null;
                long listedBytes = 0;
                foreach (var item in rows)
                {
                    var row = item as Dictionary<string, object>;
                    string path = row == null ? null : Text(row, "path"), sha = row == null ? null : Text(row, "sha256");
                    long bytes = row == null ? -1 : Number(row, "bytes");
                    if (String.IsNullOrEmpty(path) || !IsSha256(sha) || bytes < 0) { problems.Add("an inventory row is malformed"); continue; }
                    if (!SafePath(path)) { problems.Add("unsafe inventory path " + path); continue; }
                    if (previous != null && Utf8Compare(previous, path) >= 0) problems.Add("inventory is not sorted and unique at " + path);
                    previous = path;
                    result.inventory[path] = new Entry { Path = path, Bytes = bytes, Sha256 = sha };
                    listedBytes += bytes;
                }
                var totals = Member(manifest, "totals") as Dictionary<string, object>;
                if (totals == null || Number(totals, "files") != rows.Count || Number(totals, "bytes") != listedBytes)
                    problems.Add("manifest totals do not match the inventory");

                // The files on disk are exactly the inventory (names compared exactly), with no links anywhere in the bundle.
                var onDisk = new HashSet<string>(StringComparer.Ordinal);
                Walk(directory, directory, onDisk, problems);
                foreach (var entry in result.inventory.Values)
                {
                    if (!onDisk.Contains(entry.Path)) { problems.Add("missing file " + entry.Path); continue; }
                    long length; int links;
                    string actual = HashFile(Native(directory, entry.Path), out length, out links);
                    if (links != 1) problems.Add("hard-linked file " + entry.Path);
                    if (length != entry.Bytes) problems.Add("size differs: " + entry.Path);
                    else if (actual != entry.Sha256) problems.Add("hash differs: " + entry.Path);
                }
                foreach (var path in onDisk.Where(p => p != "manifest.json" && p != "DATA_READY" && !result.inventory.ContainsKey(p)).OrderBy(p => p, StringComparer.Ordinal))
                    problems.Add("unlisted file " + path + (path.IndexOf("receipt", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("ACTIVATED", StringComparison.Ordinal) >= 0
                        ? " (activation receipts never live in a bundle)" : ""));
                foreach (var root in Roots)
                    if (!System.IO.Directory.Exists(Path.Combine(directory, root))) problems.Add("missing root " + root + "/");
                foreach (var file in RequiredFiles)
                    if (!result.inventory.ContainsKey(file)) problems.Add("missing required file " + file);

                // WorldContentId: worldbridge identity::world_content over the verified files.
                if (problems.Count == 0 && ContentId(result.inventory.Values) != result.WorldContentId) problems.Add("worldContentId does not match the files");

                // Hashes the manifest names inside the bundle: every policy and the static validation report.
                var policies = Member(manifest, "policies") as Dictionary<string, object>;
                if (policies == null) problems.Add("manifest policies are missing");
                else foreach (var policy in policies)
                {
                    Entry entry;
                    string path = "policies/" + policy.Key + ".json";
                    if (!SafePath(path) || !result.inventory.TryGetValue(path, out entry) || entry.Sha256 != policy.Value as string)
                        problems.Add("policy " + policy.Key + " does not match its manifest hash");
                }
                var validation = Member(manifest, "validation") as Dictionary<string, object>;
                Entry report;
                if (validation == null || Text(validation, "travelStatus") != "ok") problems.Add("the static validation did not pass");
                else if (Text(validation, "reportPath") == null || !SafePath(Text(validation, "reportPath")) ||
                         !result.inventory.TryGetValue(Text(validation, "reportPath"), out report) || report.Sha256 != Text(validation, "reportSha256"))
                    problems.Add("the validation report does not match its manifest hash");

                // Compatibility: compiled/compatibility.json (covered by the WorldContentId), repeated by the manifest.
                var engine = Member(manifest, "engine") as Dictionary<string, object>;
                var capabilities = Member(manifest, "requiredCapabilities") as IList;
                if (engine == null || String.IsNullOrEmpty(Text(engine, "gameVersion")) || String.IsNullOrEmpty(Text(engine, "navalVersion")) || capabilities == null)
                    problems.Add("manifest engine versions or requiredCapabilities are missing");
                else if (problems.Count == 0)
                {
                    var required = Capabilities(capabilities);
                    if (required == null) problems.Add("a required capability is malformed");
                    var compatibility = Json(result.ReadVerified("compiled/compatibility.json"));
                    var versions = Member(compatibility, "engine") as Dictionary<string, object>;
                    var compatible = Member(compatibility, "requiredCapabilities") as IList;
                    var compatibleCapabilities = compatible == null ? null : Capabilities(compatible);
                    if (Text(compatibility, "schema") != CompatibilitySchema || versions == null || compatibleCapabilities == null || required == null ||
                        Text(versions, "gameVersion") != Text(engine, "gameVersion") || Text(versions, "navalVersion") != Text(engine, "navalVersion") ||
                        !compatibleCapabilities.SequenceEqual(required))
                        problems.Add("manifest engine or requiredCapabilities differ from compiled/compatibility.json");
                    else
                    {
                        if (Text(versions, "gameVersion") != host.GameVersion) problems.Add("built for Bannerlord " + Text(versions, "gameVersion") + ", installed " + host.GameVersion);
                        if (Text(versions, "navalVersion") != host.NavalVersion) problems.Add("built for War Sails " + Text(versions, "navalVersion") + ", installed " + host.NavalVersion);
                        foreach (var capability in compatibleCapabilities)
                            if (!host.Capabilities.Contains(capability.Key))
                                problems.Add("requires runtime capability " + capability.Key + " (" + capability.Value + "), which this host does not provide");
                    }
                }
                if (problems.Count == 0) bundle = result;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                problems.Add(ex.GetType().Name + ": " + ex.Message);
            }
            return problems;
        }

        // [{id: R<2 digits>, name}] as (id, name) pairs in order, or null when malformed.
        private static List<KeyValuePair<string, string>> Capabilities(IList list)
        {
            var result = new List<KeyValuePair<string, string>>();
            foreach (var item in list)
            {
                var capability = item as Dictionary<string, object>;
                string id = capability == null ? null : Text(capability, "id"), name = capability == null ? null : Text(capability, "name");
                if (id == null || id.Length != 3 || id[0] != 'R' || !Char.IsDigit(id[1]) || !Char.IsDigit(id[2]) || id[1] > '9' || id[2] > '9' || String.IsNullOrEmpty(name)) return null;
                result.Add(new KeyValuePair<string, string>(id, name));
            }
            return result;
        }

        // identity::world_content: sha256 of the prefix and the UTF-8-ordinal sorted lines "<root>/<relative>\t<bytes>\t<sha>\n",
        // spelled with '/' exactly as the inventory paths are.
        public static string ContentId(IEnumerable<Entry> entries)
        {
            var lines = entries.Select(e => e.Path + "\t" + e.Bytes.ToString(CultureInfo.InvariantCulture) + "\t" + e.Sha256).ToList();
            lines.Sort(Utf8Compare);
            var text = new StringBuilder(ContentIdPrefix);
            foreach (var line in lines) text.Append(line).Append('\n');
            return Sha256(new UTF8Encoding(false, true).GetBytes(text.ToString()));
        }

        private static void Walk(string top, string directory, HashSet<string> files, List<string> problems)
        {
            foreach (var file in System.IO.Directory.GetFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { problems.Add("linked file " + Relative(top, file)); continue; }
                files.Add(Relative(top, file));
            }
            foreach (var child in System.IO.Directory.GetDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) { problems.Add("linked directory " + Relative(top, child)); continue; }
                Walk(top, child, files, problems);
            }
        }

        private static string Relative(string top, string path) { return path.Substring(top.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/'); }

        // worldbridge safe_bundle_path().
        private static bool SafePath(string path)
        {
            var parts = path.Split('/');
            if (Encoding.UTF8.GetByteCount(path) > MaxPathBytes || parts.Length < 2 || Array.IndexOf(Roots, parts[0]) < 0) return false;
            foreach (var part in parts)
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal)) return false;
                if (part.Any(c => c < 0x20 || "<>:\"|?*\\".IndexOf(c) >= 0)) return false;
                int dot = part.IndexOf('.');
                if (Array.IndexOf(Devices, (dot < 0 ? part : part.Substring(0, dot)).ToLowerInvariant()) >= 0) return false;
            }
            return true;
        }

        private static string Native(string directory, string path)
        {
            string full = Path.GetFullPath(Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Bundle path escapes the bundle: " + path);
            return full;
        }

        private static int Utf8Compare(string a, string b)
        {
            byte[] x = Encoding.UTF8.GetBytes(a), y = Encoding.UTF8.GetBytes(b);
            for (int i = 0; i < Math.Min(x.Length, y.Length); i++) if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
            return x.Length.CompareTo(y.Length);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, [Out] uint[] information);

        // One read of the file: its SHA-256, length and hard-link count (BY_HANDLE_FILE_INFORMATION.nNumberOfLinks).
        private static string HashFile(string path, out long length, out int links)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            using (var sha = new SHA256Cng())
            {
                var information = new uint[13];
                if (!GetFileInformationByHandle(stream.SafeFileHandle, information)) throw new IOException("Cannot read file information: " + path, Marshal.GetHRForLastWin32Error());
                links = (int)information[10];
                length = stream.Length;
                return Hex(sha.ComputeHash(stream));
            }
        }

        private static byte[] ReadDocument(string path)
        {
            if (new FileInfo(path).Length > MaxDocumentBytes) throw new InvalidDataException("Document too large: " + path);
            return File.ReadAllBytes(path);
        }

        internal static string Sha256(byte[] bytes) { using (var sha = new SHA256Cng()) return Hex(sha.ComputeHash(bytes)); }
        private static string Hex(byte[] hash) { return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant(); }
        private static bool IsSha256(string value) { return value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')); }
        private static object Member(Dictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) ? v : null; }
        private static string Text(Dictionary<string, object> o, string key) { return Member(o, key) as string; }

        // A non-negative integer token; JavaScriptSerializer gives int or long for those, decimal or double for anything with a
        // fraction, an exponent or beyond 64 bits (refused, as worldbridge refuses them).
        private static long Number(Dictionary<string, object> o, string key)
        {
            object v = Member(o, key);
            if (v is int) return (int)v >= 0 ? (int)v : -1;
            if (v is long) return (long)v >= 0 ? (long)v : -1;
            return -1;
        }

        // Strict JSON (RFC 8259, as worldbridge's reader): one object, double-quoted keys, no repeated key in an object, no
        // trailing data. JavaScriptSerializer alone keeps the last of repeated keys and accepts some non-JSON.
        private static Dictionary<string, object> Json(byte[] bytes)
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            StrictJson.Check(text);
            var value = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(text) as Dictionary<string, object>;
            if (value == null) throw new InvalidDataException("Expected a JSON object.");
            return value;
        }

        private sealed class StrictJson
        {
            private readonly string s;
            private int i;
            private StrictJson(string text) { s = text; }

            public static void Check(string text)
            {
                var p = new StrictJson(text);
                p.Space(); p.Value(0); p.Space();
                if (p.i != p.s.Length) throw new InvalidDataException("Trailing data after the JSON value.");
            }

            private void Space() { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++; }
            private char Peek() { if (i >= s.Length) throw new InvalidDataException("Unexpected end of JSON."); return s[i]; }
            private void Expect(char c) { if (Peek() != c) throw new InvalidDataException("Expected '" + c + "' at offset " + i + "."); i++; }

            private void Value(int depth)
            {
                if (depth > 128) throw new InvalidDataException("JSON nests too deeply.");
                char c = Peek();
                if (c == '{')
                {
                    i++; Space();
                    if (Peek() == '}') { i++; return; }
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    while (true)
                    {
                        Space();
                        if (Peek() != '"') throw new InvalidDataException("Expected a double-quoted key at offset " + i + ".");
                        string key = Text();
                        if (!keys.Add(key)) throw new InvalidDataException("Repeated key '" + key + "'.");
                        Space(); Expect(':'); Space(); Value(depth + 1); Space();
                        if (Peek() == ',') { i++; continue; }
                        Expect('}'); return;
                    }
                }
                if (c == '[')
                {
                    i++; Space();
                    if (Peek() == ']') { i++; return; }
                    while (true)
                    {
                        Space(); Value(depth + 1); Space();
                        if (Peek() == ',') { i++; continue; }
                        Expect(']'); return;
                    }
                }
                if (c == '"') { Text(); return; }
                foreach (var word in new[] { "true", "false", "null" })
                    if (String.CompareOrdinal(s, i, word, 0, word.Length) == 0) { i += word.Length; return; }
                Number();
            }

            private string Text()
            {
                Expect('"');
                var b = new StringBuilder();
                while (true)
                {
                    char c = Peek(); i++;
                    if (c == '"') return b.ToString();
                    if (c < 0x20) throw new InvalidDataException("Control character in a JSON string.");
                    if (c != '\\') { b.Append(c); continue; }
                    char e = Peek(); i++;
                    int at = "\"\\/bfnrt".IndexOf(e);
                    if (at >= 0) { b.Append("\"\\/\b\f\n\r\t"[at]); continue; }
                    if (e != 'u' || i + 4 > s.Length) throw new InvalidDataException("Invalid escape in a JSON string.");
                    int code;
                    if (!Int32.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)) throw new InvalidDataException("Invalid \\u escape.");
                    b.Append((char)code); i += 4;
                }
            }

            private void Number()
            {
                int start = i;
                if (i < s.Length && s[i] == '-') i++;
                if (i < s.Length && s[i] == '0') i++;
                else if (i < s.Length && s[i] >= '1' && s[i] <= '9') while (i < s.Length && Char.IsDigit(s[i]) && s[i] <= '9') i++;
                else throw new InvalidDataException("Invalid JSON value at offset " + start + ".");
                if (i < s.Length && s[i] == '.') { i++; int d = i; while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++; if (i == d) throw new InvalidDataException("Invalid number."); }
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                {
                    i++; if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                    int d = i; while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
                    if (i == d) throw new InvalidDataException("Invalid number.");
                }
            }
        }
    }
}
