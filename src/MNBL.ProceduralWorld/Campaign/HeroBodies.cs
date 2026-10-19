using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-32: explicit bodies for the XML heroes (docs/heroes.md). Hero.Deserialize gives a hero its character's body minimum
    // (StaticBodyProperties = GetBodyPropertiesMin(), installed IL) and nothing re-randomizes an XML hero at new game, so every hero made
    // from one template would share one face. Before the NPCCharacters document is registered, each hero's face key template is
    // replaced by an explicit body drawn with the engine's own face generator (BodyProperties.GetRandomBodyProperties, native) from the
    // template's installed range and hair/beard/tattoo tags, for the hero's sex, with the hero's seed from the HeroFactory bodies stream.
    // The same seed gives the same body; the official heroes carry explicit bodies the same way.
    internal static class HeroBodies
    {
        public sealed class Body { public string Hero, Template, Xml; public bool Female; public int Seed; }

        // Draws every XML hero's body; problems (a missing range, a generator failure) are returned, never repaired.
        public static List<Body> Draw(IEnumerable<HeroFactory.HeroIntent> heroes, List<string> problems)
        {
            var bodies = new List<Body>();
            foreach (var h in heroes.Where(x => x.Xml).OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                string id = h.BodyTemplate == null ? null : (h.BodyTemplate.StartsWith("BodyProperty.", StringComparison.Ordinal) ? h.BodyTemplate.Substring(13) : h.BodyTemplate);
                var range = id == null ? null : MBObjectManager.Instance.GetObject<MBBodyProperty>(id);
                if (range == null) { problems.Add("hero " + h.Id + " body range " + h.BodyTemplate + " is not registered"); continue; }
                try
                {
                    var body = BodyProperties.GetRandomBodyProperties(0, h.Female, range.BodyPropertyMin, range.BodyPropertyMax, 0, h.BodySeed,
                                                                      range.HairTags, range.BeardTags, range.TattooTags);
                    bodies.Add(new Body { Hero = h.Id, Template = h.BodyTemplate, Female = h.Female, Seed = h.BodySeed, Xml = body.ToString() });
                }
                catch (Exception error) { problems.Add("hero " + h.Id + " body: " + error.GetType().Name + ": " + error.Message); }
            }
            return bodies;
        }

        // Replaces each drawn hero's face in the NPCCharacters document with its explicit body.
        public static void Apply(XmlDocument characters, IEnumerable<Body> bodies, List<string> problems)
        {
            foreach (var b in bodies)
            {
                var e = characters.DocumentElement.SelectSingleNode("NPCCharacter[@id='" + b.Hero + "']") as XmlElement;
                if (e == null) { problems.Add("hero " + b.Hero + " has no NPCCharacter"); continue; }
                var face = e.SelectSingleNode("face") as XmlElement;
                if (face == null) { face = characters.CreateElement("face"); e.PrependChild(face); }
                face.RemoveAll();
                var fragment = new XmlDocument(); fragment.LoadXml(b.Xml);
                face.AppendChild(characters.ImportNode(fragment.DocumentElement, true));
            }
        }
    }
}
