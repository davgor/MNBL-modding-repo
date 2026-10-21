using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-41: the identity of a world's navigation caches (the installed SandBoxNavigationCache Default, Naval and All structures). A cache
    // is reused only under the same key, and the key holds every input the cached distances depend on:
    //   game        the game and War Sails versions, the distance and party navigation models (type and assembly version), the campaign's
    //               path-cost limit, and per navigation type the cache's own parameters the models set (excluded faces, region switch
    //               costs, agent radius, neighbour cost multiplier)
    //   world       the world id, the scene name, its navmesh.bin hash and the engine's native scene XML and navigation mesh CRCs
    //   positions   every registered settlement's id, kind, position, gate and port as the campaign holds them (MMR-40's resolved placement)
    //   adapter     this adapter's version and the periodic distance identity (MMR-64: the caches are built through the periodic map scene)
    //   schema      the key and cache schema
    // Pure managed: the key text is canonical (one name=value line per input, then one line per settlement in id order; floats in
    // round-trip form), its SHA-256 names the cache, and Differences names what changed between two keys.
    internal sealed class NavigationCacheKey
    {
        public const string Schema = "mnbl.navigation-cache-key";
        public const int Version = 1;
        // MMR-254 (a3): every keyed input and what it is read from. Each is fixed for a world for as long as the game, War Sails and this module
        // stay the same: the installed models' types and the caches' own parameters, the scene's files (its navmesh.bin hash and the engine's
        // CRCs of them), the world's identity and coordinate frame, and the settlements' placed positions. None is a campaign's mutable state:
        // not the party speed model, roads.json, the cells.bin road bits, the live face terrain overrides of a mutation (MMR-260/261: they
        // retype faces through GeneratedMapScene, never the navmesh file), the weather, the season or the campaign time. Set refuses a name
        // this table does not list, so a new input is classified before it can be keyed (PlayabilityTests checks the table against BuildKey).
        public static readonly IDictionary<string, string> Sources = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["game"] = "installed game and War Sails versions",
            ["distanceModel"] = "installed model type and assembly version",
            ["partyNavigationModel"] = "installed model type and assembly version",
            ["pathFindingMaxCostLimit"] = "installed campaign constant",
            ["excludedFaceIds"] = "the cache's parameter, set by the installed navigation model",
            ["regionSwitchCosts"] = "the cache's parameter, set by the installed navigation model",
            ["agentRadius"] = "the cache's parameter, set by the installed navigation model",
            ["neighbourCostMultiplier"] = "the cache's parameter, set by the installed navigation model",
            ["worldId"] = "the world's identity",
            ["scene"] = "the world's content-addressed scene name",
            ["navmeshSha256"] = "the scene's navmesh.bin, written once by the encoder",
            ["sceneXmlCrc"] = "the engine's CRC of the scene's files",
            ["navigationMeshCrc"] = "the engine's CRC of the scene's files",
            ["periodic"] = "the world's coordinate frame",
            ["distanceVersion"] = "this module's code",
            ["adapter"] = "this module's code",
            ["moduleSha256"] = "this module's code",
            ["schema"] = "this module's code",
        };
        // The per-navigation-type inputs are keyed as "<type>.<name>".
        public static string SourceName(string input) { int dot = input.IndexOf('.'); return dot < 0 ? input : input.Substring(dot + 1); }
        public readonly SortedDictionary<string, string> Inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, string> settlements = new SortedDictionary<string, string>(StringComparer.Ordinal);

        public NavigationCacheKey Set(string name, string value)
        {
            if (String.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '=', '\n', '\r' }) >= 0) throw new ArgumentException("Bad key input name " + name);
            if (value == null || value.IndexOfAny(new[] { '\n', '\r' }) >= 0) throw new ArgumentException("Bad key input value for " + name);
            if (!Sources.ContainsKey(SourceName(name))) throw new ArgumentException("Key input " + name + " has no classified source (NavigationCacheKey.Sources).");
            Inputs[name] = value;
            return this;
        }

        public NavigationCacheKey Settlement(string id, string kind, float[] position, float[] gate, float[] port)
        {
            if (String.IsNullOrEmpty(id) || id.IndexOfAny(new[] { '|', '\n', '\r' }) >= 0) throw new ArgumentException("Bad settlement id " + id);
            if (settlements.ContainsKey(id)) throw new ArgumentException("Settlement " + id + " is keyed twice.");
            settlements[id] = id + "|" + kind + "|" + P(position) + "|" + P(gate) + "|" + P(port);
            return this;
        }

        public IEnumerable<string> SettlementIds { get { return settlements.Keys; } }

        public string Text
        {
            get
            {
                var text = new StringBuilder();
                text.Append(Schema).Append(' ').Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
                foreach (var kv in Inputs) text.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
                foreach (var line in settlements.Values) text.Append("settlement=").Append(line).Append('\n');
                return text.ToString();
            }
        }

        public string Sha256 { get { return Sha(new UTF8Encoding(false).GetBytes(Text)); } }

        // The inputs that differ between two key texts: input names, and settlement ids (added, removed or moved), in order.
        public static List<string> Differences(string a, string b)
        {
            Func<string, Dictionary<string, string>> parse = text => {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in (text ?? "").Split('\n').Where(l => l.Length > 0))
                {
                    int eq = line.IndexOf('=');
                    string name = eq < 0 ? "(header)" : line.Substring(0, eq), value = eq < 0 ? line : line.Substring(eq + 1);
                    if (name == "settlement") { int bar = value.IndexOf('|'); name = "settlement " + (bar < 0 ? value : value.Substring(0, bar)); }
                    map[name] = value;
                }
                return map;
            };
            var x = parse(a); var y = parse(b);
            return x.Keys.Union(y.Keys).Where(k => { string u, v; return !x.TryGetValue(k, out u) || !y.TryGetValue(k, out v) || u != v; })
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
        }

        private static string P(float[] p) { return p == null ? "-" : String.Join(",", p.Select(v => v.ToString("R", CultureInfo.InvariantCulture))); }
        public static string Sha(byte[] b) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }
    }

    // The installed cache file (NavigationCache<T>.Serialize, 1.4.8 IL), read without the engine: the scene XML and navigation mesh CRCs
    // (written, never checked by the installed Deserialize), then per settlement entrance (string id, bool port) its distances to the
    // entrances after it (string id, bool port, float distance, and for the All type the land ratio), the fortification neighbour pairs
    // (string, string), and the closest entrance of every navmesh face (int face, string id, bool port). BinaryWriter strings are UTF-8 with
    // a 7-bit encoded length.
    internal sealed class NavigationCacheFile
    {
        public const int DefaultType = 1, NavalType = 2, AllType = 3;   // MobileParty.NavigationType values
        public uint SceneXmlCrc, NavigationMeshCrc;
        public int Entrances, Pairs, NeighbourPairs, Faces, MaxFace = -1;
        public readonly HashSet<string> Ids = new HashSet<string>(StringComparer.Ordinal);
        // Content, canonical (an entrance pair in ordinal order, the order NavigationCacheElement.Sort may change on a round trip), so a
        // generated cache and its serialized and deserialized copy compare equal: "a:p|b:q" -> distance ("...|ratio" -> land ratio for All),
        // the neighbour pairs "a|b", and each face's closest entrance "id:p".
        public readonly SortedDictionary<string, float> Distances = new SortedDictionary<string, float>(StringComparer.Ordinal);
        public readonly SortedSet<string> Neighbours = new SortedSet<string>(StringComparer.Ordinal);
        public readonly SortedDictionary<int, string> FaceMap = new SortedDictionary<int, string>();

        public static NavigationCacheFile Read(byte[] data, int navigationType)
        {
            var f = new NavigationCacheFile();
            using (var reader = new BinaryReader(new MemoryStream(data), new UTF8Encoding(false)))
            {
                f.SceneXmlCrc = reader.ReadUInt32(); f.NavigationMeshCrc = reader.ReadUInt32();
                f.Entrances = Count(reader, "entrances");
                for (int i = 0; i < f.Entrances; i++)
                {
                    string a = reader.ReadString(); bool pa = reader.ReadBoolean(); f.Ids.Add(a);
                    int n = Count(reader, "pairs of " + a);
                    for (int j = 0; j < n; j++)
                    {
                        string b = reader.ReadString(); bool pb = reader.ReadBoolean(); float d = reader.ReadSingle(); f.Ids.Add(b);
                        float ratio = navigationType == AllType ? reader.ReadSingle() : float.NaN;
                        string x = a + ":" + (pa ? 1 : 0), y = b + ":" + (pb ? 1 : 0), pair = String.CompareOrdinal(x, y) <= 0 ? x + "|" + y : y + "|" + x;
                        f.Distances[pair] = d;
                        if (navigationType == AllType) f.Distances[pair + "|ratio"] = ratio;
                        f.Pairs++;
                    }
                }
                f.NeighbourPairs = Count(reader, "neighbour pairs");
                for (int i = 0; i < f.NeighbourPairs; i++) { string a = reader.ReadString(), b = reader.ReadString(); f.Ids.Add(a); f.Ids.Add(b); f.Neighbours.Add(a + "|" + b); }
                f.Faces = Count(reader, "faces");
                for (int i = 0; i < f.Faces; i++)
                {
                    int face = reader.ReadInt32(); string id = reader.ReadString(); bool port = reader.ReadBoolean();
                    if (face < 0) throw new InvalidDataException("A negative face index.");
                    f.Ids.Add(id); f.FaceMap[face] = id + ":" + (port ? 1 : 0); f.MaxFace = Math.Max(f.MaxFace, face);
                }
                if (reader.BaseStream.Position != data.Length) throw new InvalidDataException((data.Length - reader.BaseStream.Position) + " bytes follow the cache's last record.");
            }
            return f;
        }

        // The differences in content between two caches of one type: distances (exact), neighbour pairs, closest entrances per face.
        public static List<string> Compare(NavigationCacheFile a, NavigationCacheFile b)
        {
            var d = new List<string>();
            foreach (var k in a.Distances.Keys.Union(b.Distances.Keys))
            {
                float u, v; bool hu = a.Distances.TryGetValue(k, out u), hv = b.Distances.TryGetValue(k, out v);
                if (!hu || !hv || !(u.Equals(v))) d.Add("distance " + k + ": " + (hu ? u.ToString("R", CultureInfo.InvariantCulture) : "-") + " / " + (hv ? v.ToString("R", CultureInfo.InvariantCulture) : "-"));
            }
            foreach (var n in a.Neighbours.Except(b.Neighbours)) d.Add("neighbour " + n + " only in the first");
            foreach (var n in b.Neighbours.Except(a.Neighbours)) d.Add("neighbour " + n + " only in the second");
            foreach (var k in a.FaceMap.Keys.Union(b.FaceMap.Keys))
            {
                string u, v; bool hu = a.FaceMap.TryGetValue(k, out u), hv = b.FaceMap.TryGetValue(k, out v);
                if (!hu || !hv || u != v) d.Add("face " + k + ": " + (u ?? "-") + " / " + (v ?? "-"));
            }
            return d;
        }

        private static int Count(BinaryReader reader, string what)
        {
            int n = reader.ReadInt32();
            if (n < 0 || n > 50000000) throw new InvalidDataException("The cache's " + what + " count " + n + " is not plausible.");
            return n;
        }
    }

    // A published cache: <store>/navigation-caches/<key sha>/ holding Default.bin, Naval.bin, All.bin and manifest.json. The manifest names
    // the key (text and hash), the scene, the native CRCs, and each file's hash, size and record counts; it is written last, into a staging
    // folder that is then moved into place whole. Check lists every reason a published cache may not be reused for a key and scene.
    internal static class NavigationCacheStore
    {
        public const string Schema = "mnbl.navigation-cache";
        public const int Version = 1;
        public static readonly string[] TypeNames = { "Default", "Naval", "All" };
        public static readonly int[] TypeValues = { NavigationCacheFile.DefaultType, NavigationCacheFile.NavalType, NavigationCacheFile.AllType };

        public static List<string> Check(IDictionary<string, object> manifest, NavigationCacheKey key, Func<string, byte[]> file, uint sceneXmlCrc, uint navigationMeshCrc, int faceCount)
        {
            var reasons = new List<string>();
            if (manifest == null) { reasons.Add("no manifest"); return reasons; }
            object schema, version; manifest.TryGetValue("schema", out schema); manifest.TryGetValue("version", out version);
            if ((schema as string) != Schema || Convert.ToInt32(version ?? 0, CultureInfo.InvariantCulture) != Version) reasons.Add("the manifest is not " + Schema + " version " + Version);
            object keySha, keyText; manifest.TryGetValue("keySha256", out keySha); manifest.TryGetValue("key", out keyText);
            if ((keySha as string) != key.Sha256 || (keyText as string) != key.Text)
                reasons.Add("the key differs: " + String.Join(", ", NavigationCacheKey.Differences(keyText as string, key.Text).Take(8)));
            object files; manifest.TryGetValue("files", out files);
            var byType = files as IDictionary<string, object>;
            for (int i = 0; i < TypeNames.Length; i++)
            {
                string type = TypeNames[i];
                object entry = null; if (byType != null) byType.TryGetValue(type, out entry);
                var e = entry as IDictionary<string, object>;
                if (e == null) { reasons.Add(type + ": not in the manifest"); continue; }
                byte[] data;
                try { data = file(type + ".bin"); }
                catch (Exception error) { reasons.Add(type + ": " + error.GetType().Name + " reading the file"); continue; }
                if (data == null) { reasons.Add(type + ": no file"); continue; }
                object sha; e.TryGetValue("sha256", out sha);
                if ((sha as string) != NavigationCacheKey.Sha(data)) { reasons.Add(type + ": the file's hash differs from the manifest's"); continue; }
                NavigationCacheFile parsed;
                try { parsed = NavigationCacheFile.Read(data, TypeValues[i]); }
                catch (Exception error) { reasons.Add(type + ": unreadable (" + error.Message + ")"); continue; }
                if (parsed.SceneXmlCrc != sceneXmlCrc || parsed.NavigationMeshCrc != navigationMeshCrc)
                    reasons.Add(type + ": written for scene CRCs " + parsed.SceneXmlCrc + "/" + parsed.NavigationMeshCrc + ", the scene's are " + sceneXmlCrc + "/" + navigationMeshCrc);
                var unknown = parsed.Ids.Where(id => !key.SettlementIds.Contains(id)).ToList();
                if (unknown.Count > 0) reasons.Add(type + ": names settlements the key does not (" + String.Join(", ", unknown.Take(4)) + ")");
                if (parsed.MaxFace >= faceCount) reasons.Add(type + ": names face " + parsed.MaxFace + " of a " + faceCount + "-face navmesh");
            }
            return reasons;
        }

        public static Dictionary<string, object> Manifest(NavigationCacheKey key, string scene, string worldId, uint sceneXmlCrc, uint navigationMeshCrc, Func<string, byte[]> file,
                                                          IDictionary<string, object> durations, string moduleSha256)
        {
            var files = new Dictionary<string, object>();
            for (int i = 0; i < TypeNames.Length; i++)
            {
                var data = file(TypeNames[i] + ".bin");
                var parsed = NavigationCacheFile.Read(data, TypeValues[i]);
                files[TypeNames[i]] = new Dictionary<string, object> { ["file"] = TypeNames[i] + ".bin", ["sha256"] = NavigationCacheKey.Sha(data), ["bytes"] = data.Length,
                    ["entrances"] = parsed.Entrances, ["pairs"] = parsed.Pairs, ["neighbourPairs"] = parsed.NeighbourPairs, ["faces"] = parsed.Faces };
            }
            return new Dictionary<string, object> { ["schema"] = Schema, ["version"] = Version, ["keySha256"] = key.Sha256, ["key"] = key.Text, ["scene"] = scene, ["worldId"] = worldId,
                ["crc"] = new Dictionary<string, object> { ["sceneXml"] = sceneXmlCrc, ["navigationMesh"] = navigationMeshCrc }, ["files"] = files, ["durations"] = durations,
                ["moduleSha256"] = moduleSha256, ["createdUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
        }

        public static IDictionary<string, object> ParseManifest(string json)
        {
            return (IDictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(json);
        }
    }
}
