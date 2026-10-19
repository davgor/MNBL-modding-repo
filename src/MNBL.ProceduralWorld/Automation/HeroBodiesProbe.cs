using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-32: in game, builds the size-129 world's heroes from the verified MMR-29 bundle and the repository policies (the same pure
    // FactionFactory / CharacterTemplateResolver / HeroFactory the offline tests run), then draws every XML hero's explicit body with the
    // engine's face generator (HeroBodies) twice and applies it to the NPCCharacters document. Nothing is registered: the world is not
    // booted here (MMR-45), only its hero bodies are drawn by the engine that will own them.
    internal static class HeroBodiesProbe
    {
        public static Dictionary<string, object> Run(string bundleDir, string repo, List<string> failures)
        {
            var report = new Dictionary<string, object>();
            var started = DateTime.UtcNow;
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
                Func<string, IDictionary<string, object>> parse = text => (IDictionary<string, object>)json.DeserializeObject(text.TrimStart('﻿'));
                Func<string, IDictionary<string, object>> load = path => parse(File.ReadAllText(Path.Combine(repo, path)));
                var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
                var bundle = WorldDataBundle.Open(bundleDir, host);
                var campaign = parse(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/campaign.json")));
                var idMap = parse(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/id-map.json")));
                var rosters = load("worldgen/policies/rosters.json");
                var provenance = (IDictionary<string, object>)rosters["provenance"];
                var templates = Directory.GetFiles(Path.Combine(repo, "worldgen/templates/cultures"), "*.json").Where(f => Path.GetFileName(f) != "contract.json")
                    .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => parse(File.ReadAllText(f)), StringComparer.Ordinal);
                var receipt = load((string)provenance["catalogReceipt"]);
                var catalog = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
                foreach (var d in ((IEnumerable)((IDictionary<string, object>)receipt["outputs"])["documents"]).Cast<IDictionary<string, object>>().Where(d => CharacterTemplateResolver.Documents.Contains((string)d["id"])))
                {
                    var bytes = File.ReadAllBytes((string)d["path"]);
                    if (Sha(bytes) != (string)((IDictionary<string, object>)provenance["catalogDocuments"])[(string)d["id"]]) throw new InvalidOperationException("catalog " + d["id"] + " differs from the roster policy");
                    var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿')); catalog[(string)d["id"]] = doc;
                }
                var run = load((string)provenance["defaultReferences"]);
                var defaults = ((IEnumerable)((IDictionary<string, object>)((IDictionary<string, object>)((IEnumerable)((IDictionary<string, object>)run["outputs"])["worlds"]).Cast<object>().First())["registration"])["verifiedLiveDefaultReferences"]).Cast<object>().Select(x => (string)x).ToList();
                var factions = FactionFactory.Build(campaign, idMap, load("worldgen/policies/factions.json"), load("worldgen/policies/culture-presentations.json"), templates);
                var heroes = HeroFactory.Build(campaign, idMap, factions, rosters, new CharacterTemplateResolver(catalog, defaults, (IDictionary<string, object>)rosters["troops"]));
                var problems = new List<string>();
                var xml = heroes.XmlHeroes.ToList();
                var drawn = HeroBodies.Draw(xml, problems);
                var again = HeroBodies.Draw(xml, new List<string>());
                var characters = heroes.Documents.Single(d => d.Key == "NPCCharacters").Value;
                HeroBodies.Apply(characters, drawn, problems);
                var keys = drawn.Select(b => Key(b.Xml)).ToList();
                bool deterministic = drawn.Count == again.Count && drawn.Zip(again, (a, b) => a.Xml == b.Xml).All(x => x);
                var explicitBodies = characters.DocumentElement.ChildNodes.OfType<XmlElement>().Count(e => e.SelectSingleNode("face/BodyProperties[@key]") != null && e.SelectSingleNode("face/face_key_template") == null);
                var templateKeys = new HashSet<string>(xml.Select(h => h.BodyTemplate).Distinct().Select(t => Key(catalog["BodyProperties"].DocumentElement.SelectSingleNode("BodyProperty[@id='" + t.Substring(13) + "']/BodyPropertiesMin").OuterXml)));
                report["intentsSha256"] = heroes.IntentsSha256;
                report["xmlHeroes"] = xml.Count; report["drawn"] = drawn.Count; report["distinctKeys"] = keys.Distinct().Count();
                report["women"] = drawn.Count(b => b.Female); report["deterministic"] = deterministic; report["explicitBodies"] = explicitBodies;
                report["templateMinimaReused"] = keys.Count(k => templateKeys.Contains(k)); report["bodyTemplates"] = xml.Select(h => h.BodyTemplate).Distinct().Count();
                report["sample"] = drawn.Take(3).Select(b => new Dictionary<string, object> { ["hero"] = b.Hero, ["template"] = b.Template, ["female"] = b.Female, ["seed"] = b.Seed, ["body"] = b.Xml }).ToList();
                report["problems"] = problems;
                foreach (var p in problems) failures.Add("hero bodies: " + p);
                if (drawn.Count != xml.Count) failures.Add("hero bodies: " + drawn.Count + " of " + xml.Count + " XML heroes have a body");
                if (keys.Distinct().Count() != keys.Count) failures.Add("hero bodies: " + (keys.Count - keys.Distinct().Count()) + " bodies repeat");
                if (!deterministic) failures.Add("hero bodies: the same seeds gave different bodies");
                if (explicitBodies != xml.Count) failures.Add("hero bodies: " + explicitBodies + " of " + xml.Count + " NPCCharacters carry an explicit body");
            }
            catch (Exception error) { report["error"] = error.ToString(); failures.Add("hero bodies: " + error.Message); }
            report["milliseconds"] = (DateTime.UtcNow - started).TotalMilliseconds;
            return report;
        }

        private static string Key(string bodyXml)
        {
            var d = new XmlDocument(); d.LoadXml(bodyXml);
            return d.DocumentElement.GetAttribute("key");
        }

        private static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }
}
