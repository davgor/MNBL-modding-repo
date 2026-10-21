using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace MNBL.ProceduralWorld.Generation
{
    // MMR-283: which active module provides or patches an installed object, so a conflict names the mod to disable. Pure managed over the
    // active modules in load order (the game's ModuleHelper in game, a folder list offline). A module's SubModule.xml lists its campaign XML
    // as Xmls/XmlNode/XmlName (id: the merged document, path: ModuleData/<path>.xml, patched by ModuleData/<path>.xslt when present), as the
    // object manager merges them. The provider of an object is the last module whose XML for the object's document defines a record with its
    // id, or whose XSLT for it names the id; a scene's is the last module with SceneObj/<id>/scene.xscene.
    internal sealed class ModuleSources
    {
        public sealed class Module
        {
            public string Id, Folder; public bool Official;
            public readonly List<KeyValuePair<string, string>> Xmls = new List<KeyValuePair<string, string>>();   // document id, path
        }

        private readonly List<Module> modules;
        private readonly Dictionary<string, HashSet<string>> defined = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> patches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public ModuleSources(IEnumerable<Module> inLoadOrder) { modules = inLoadOrder.ToList(); }
        public IList<Module> Modules { get { return modules; } }

        // A module from its folder: its SubModule.xml's campaign XML declarations (none when it has no readable SubModule.xml).
        public static Module Read(string id, string folder, bool official)
        {
            var m = new Module { Id = id, Folder = folder, Official = official };
            string path = Path.Combine(folder ?? "", "SubModule.xml");
            if (folder == null || !File.Exists(path)) return m;
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
                foreach (var name in doc.SelectNodes("//Xmls/XmlNode/XmlName").OfType<XmlElement>())
                    if (name.GetAttribute("id").Length > 0 && name.GetAttribute("path").Length > 0) m.Xmls.Add(new KeyValuePair<string, string>(name.GetAttribute("id"), name.GetAttribute("path")));
            }
            catch (Exception error) when (error is XmlException || error is IOException || error is UnauthorizedAccessException) { }
            return m;
        }

        public Module Provider(string document, string id)
        {
            Module found = null;
            foreach (var m in modules)
                foreach (var x in m.Xmls.Where(x => String.Equals(x.Key, document, StringComparison.OrdinalIgnoreCase)))
                {
                    string xml = Path.Combine(m.Folder, "ModuleData", x.Value + ".xml"), xslt = Path.Combine(m.Folder, "ModuleData", x.Value + ".xslt");
                    if (Defines(xml).Contains(id) || Patches(xslt, id)) found = m;
                }
            return found;
        }

        public Module SceneProvider(string scene)
        {
            Module found = null;
            foreach (var m in modules) if (m.Folder != null && File.Exists(Path.Combine(m.Folder, "SceneObj", scene ?? "", "scene.xscene"))) found = m;
            return found;
        }

        // The ids of the records a module XML defines (its root's children with an id), read once.
        private HashSet<string> Defines(string file)
        {
            HashSet<string> ids;
            if (defined.TryGetValue(file, out ids)) return ids;
            ids = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists(file))
                try
                {
                    using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true }))
                        while (reader.Read())
                            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1) { string id = reader.GetAttribute("id"); if (id != null) ids.Add(id); }
                }
                catch (Exception error) when (error is XmlException || error is IOException || error is UnauthorizedAccessException) { }
            defined[file] = ids;
            return ids;
        }

        private bool Patches(string file, string id)
        {
            string text;
            if (!patches.TryGetValue(file, out text))
            {
                try { text = File.Exists(file) ? File.ReadAllText(file) : ""; } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { text = ""; }
                patches[file] = text;
            }
            return text.Length > 0 && (text.Contains("'" + id + "'") || text.Contains("\"" + id + "\""));
        }
    }
}
