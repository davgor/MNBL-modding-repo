using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using TaleWorlds.ModuleManager;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-19: the immutable outputs a generated world needs to be loaded again without running the generator: the placement,
    // its climate files, the generated XML documents (exact bytes the new game loaded) and world.json, which pins every hash
    // including the five installed scene files. Written once per world after creation; a cold load reads only this package
    // and the installed scene folder, verified by hash, and fails closed on any mismatch.
    internal sealed class GeneratedWorldPackage
    {
        public const string Schema = "mnbl.generated-world-package.v1";
        // The documents a saved game re-applies, in the new-game order. Heroes, Kingdoms and Factions are temporary types whose
        // state lives in the save (vanilla InitializeSandboxXMLs(true) skips them too).
        public static readonly string[] SavedGameDocuments = { "SPCultures", "NPCCharacters", "WorkshopTypes", "LocationComplexTemplates", "Settlements" };
        private static readonly string[] SceneFiles = { "scene.xscene", "terrain.bin", "navmesh.bin", "flora.bin", "atmosphere.xml" };

        public string Directory { get; private set; }
        public string ManifestSha256 { get; private set; }
        public string WorldId { get; private set; }
        public string SceneName { get; private set; }
        public uint Seed { get; private set; }
        public string PlacementPath { get; private set; }
        public Dictionary<string, object> Placement { get; private set; }
        public Dictionary<string, object> Manifest { get; private set; }
        private readonly Dictionary<string, string> documentFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> documentHashes = new Dictionary<string, string>(StringComparer.Ordinal);

        public static string Root { get { return Path.Combine(SubModule.Store, "worlds"); } }

        public static GeneratedWorldPackage Write(string worldId, string sceneName, uint seed, string placementPath,
            IEnumerable<GeneratedCampaignDataset.Document> documents, string adapterSha256, Dictionary<string, object> datasetFingerprint)
        {
            var docs = documents.ToList();
            var placementBytes = File.ReadAllBytes(placementPath);
            var placement = Json(placementBytes);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["placement.json"] = placementBytes };
            object climate;
            if (placement.TryGetValue("climate", out climate) && climate != null)
                foreach (var key in new[] { "wind", "precipitation" })
                {
                    string name = Convert.ToString(((Dictionary<string, object>)((Dictionary<string, object>)climate)[key])["file"], CultureInfo.InvariantCulture);
                    files[name] = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(placementPath)), name));
                }
            // Generator fingerprint: the offline build receipt next to the placement (builder, encoder and source hashes, settings).
            var receiptPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(placementPath)), "build-receipt.json");
            Dictionary<string, object> receipt = null;
            if (File.Exists(receiptPath)) { files["build-receipt.json"] = File.ReadAllBytes(receiptPath); receipt = Json(files["build-receipt.json"]); }
            var documentRows = new List<object>();
            foreach (var document in docs)
            {
                var bytes = new UTF8Encoding(false).GetBytes(document.Xml);
                if (Sha256(bytes) != document.Sha256) throw new InvalidDataException("Generated document bytes differ from their recorded hash: " + document.Id);
                files["documents/" + document.Id + ".xml"] = bytes;
                documentRows.Add(new Dictionary<string, object> { ["id"] = document.Id, ["file"] = "documents/" + document.Id + ".xml", ["sha256"] = document.Sha256 });
            }
            var sceneHashes = (Dictionary<string, object>)placement["sceneFiles"];
            var manifest = new Dictionary<string, object> {
                ["schema"] = Schema, ["worldId"] = worldId, ["sceneName"] = sceneName, ["seed"] = seed.ToString(CultureInfo.InvariantCulture),
                ["files"] = files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToDictionary(k => k, k => (object)Sha256(files[k])),
                ["documents"] = documentRows,
                ["sceneFiles"] = SceneFiles.ToDictionary(f => f, f => (object)Convert.ToString(sceneHashes[f], CultureInfo.InvariantCulture)),
                ["generator"] = new Dictionary<string, object> {
                    ["placementSchema"] = placement["schema"], ["sourceSha256"] = placement.ContainsKey("sourceSha256") ? placement["sourceSha256"] : null,
                    ["terrainSha256"] = placement.ContainsKey("terrainSha256") ? placement["terrainSha256"] : null,
                    ["buildReceiptSha256"] = receipt == null ? null : Sha256(files["build-receipt.json"]),
                    ["buildInputs"] = receipt != null && receipt.ContainsKey("inputs") ? receipt["inputs"] : null,
                    ["buildCommand"] = receipt != null && receipt.ContainsKey("command") ? receipt["command"] : null,
                    ["derivedFrom"] = receipt != null && receipt.ContainsKey("derivedFrom") ? receipt["derivedFrom"] : null },
                ["adapter"] = new Dictionary<string, object> { ["moduleAssemblySha256"] = adapterSha256, ["dataset"] = datasetFingerprint },
                ["scope"] = "MMR-19 diagnostic world package; loading reads only this package and the installed scene folder"
            };
            var manifestBytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(manifest));
            string manifestSha = Sha256(manifestBytes);
            var directory = Path.Combine(Root, worldId + "-" + manifestSha.Substring(0, 12));
            if (System.IO.Directory.Exists(directory))
            {
                // Immutable: an existing package must be byte-identical.
                if (Sha256(File.ReadAllBytes(Path.Combine(directory, "world.json"))) != manifestSha)
                    throw new InvalidDataException("An existing world package differs: " + directory);
            }
            else
            {
                var temporary = directory + ".tmp-" + Guid.NewGuid().ToString("N");
                foreach (var file in files)
                {
                    var target = Path.Combine(temporary, file.Key.Replace('/', Path.DirectorySeparatorChar));
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.WriteAllBytes(target, file.Value);
                }
                File.WriteAllBytes(Path.Combine(temporary, "world.json"), manifestBytes);
                System.IO.Directory.Move(temporary, directory);
            }
            return Load(directory, manifestSha);
        }

        public static GeneratedWorldPackage Load(string directory, string expectedManifestSha256)
        {
            directory = Path.GetFullPath(directory);
            if (!directory.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("World package outside the package root: " + directory);
            var manifestBytes = File.ReadAllBytes(Path.Combine(directory, "world.json"));
            string manifestSha = Sha256(manifestBytes);
            if (manifestSha != expectedManifestSha256) throw new InvalidDataException("World package manifest hash mismatch.");
            var manifest = Json(manifestBytes);
            if (Convert.ToString(manifest["schema"], CultureInfo.InvariantCulture) != Schema) throw new InvalidDataException("Wrong world package schema.");
            foreach (var file in (Dictionary<string, object>)manifest["files"])
            {
                var path = Path.GetFullPath(Path.Combine(directory, file.Key.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package file escapes the package.");
                if (Sha256(File.ReadAllBytes(path)) != Convert.ToString(file.Value, CultureInfo.InvariantCulture)) throw new InvalidDataException("World package file hash mismatch: " + file.Key);
            }
            string sceneName = Convert.ToString(manifest["sceneName"], CultureInfo.InvariantCulture);
            DiagnosticMapSceneCreator.ValidateFixture(sceneName);
            var sceneFolder = Path.Combine(ModuleHelper.GetModuleFullPath(DiagnosticMapSceneCreator.ModuleId), "SceneObj", sceneName);
            foreach (var file in (Dictionary<string, object>)manifest["sceneFiles"])
                if (Sha256(File.ReadAllBytes(Path.Combine(sceneFolder, file.Key))) != Convert.ToString(file.Value, CultureInfo.InvariantCulture))
                    throw new InvalidDataException("Installed scene file differs from the world package: " + file.Key);
            var package = new GeneratedWorldPackage {
                Directory = directory, ManifestSha256 = manifestSha, Manifest = manifest,
                WorldId = Convert.ToString(manifest["worldId"], CultureInfo.InvariantCulture), SceneName = sceneName,
                Seed = UInt32.Parse(Convert.ToString(manifest["seed"], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                PlacementPath = Path.Combine(directory, "placement.json")
            };
            package.Placement = Json(File.ReadAllBytes(package.PlacementPath));
            foreach (Dictionary<string, object> row in (IEnumerable)manifest["documents"])
            {
                string id = Convert.ToString(row["id"], CultureInfo.InvariantCulture);
                package.documentFiles[id] = Path.Combine(directory, Convert.ToString(row["file"], CultureInfo.InvariantCulture).Replace('/', Path.DirectorySeparatorChar));
                package.documentHashes[id] = Convert.ToString(row["sha256"], CultureInfo.InvariantCulture);
            }
            if (Convert.ToString(package.Placement["worldId"], CultureInfo.InvariantCulture) != package.WorldId) throw new InvalidDataException("Package placement identity differs.");
            return package;
        }

        public IEnumerable<string> DocumentIds { get { return documentFiles.Keys; } }
        public string DocumentSha256(string id) { return documentHashes[id]; }

        // The document as an XmlDocument, re-verified against the manifest at the moment of use.
        public XmlDocument Document(string id)
        {
            var bytes = File.ReadAllBytes(documentFiles[id]);
            if (Sha256(bytes) != documentHashes[id]) throw new InvalidDataException("World package document changed: " + id);
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                document.Load(reader);
            return document;
        }

        internal static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static Dictionary<string, object> Json(byte[] bytes)
        {
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<Dictionary<string, object>>(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿'));
        }
    }
}
