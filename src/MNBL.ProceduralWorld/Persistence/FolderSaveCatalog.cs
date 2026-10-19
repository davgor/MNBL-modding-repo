using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-48: a save folder read without the game, as the transfer tool and the offline tests see it: every "<name>.sav" with its header
    // (SaveFolder.ReadMetadata; an unreadable header gives Metadata null), Read with the integrity check (SaveFileCheck), deletion and flush
    // through System.IO. In game the catalog is BannerlordSaveCatalog (the game's own list, cross-checked with this folder).
    internal sealed class FolderSaveCatalog : ISaveCatalog
    {
        private readonly string folder;
        public FolderSaveCatalog(string folder) { this.folder = Path.GetFullPath(folder); }
        public string Folder { get { return folder; } }

        public string PathOf(string name)
        {
            if (!SaveJournal.ValidName(name)) throw new ArgumentException("Not a valid save name: '" + name + "'.");
            return Path.Combine(folder, name + ".sav");
        }

        public IList<SaveHeader> List() { return SaveFolder.Names(folder).OrderBy(n => n, StringComparer.Ordinal).Select(Header).ToList(); }

        public SaveHeader Read(string name)
        {
            if (!SaveFolder.Contains(folder, name)) return null;
            var header = Header(name);
            if (header.Readable)
            {
                string problem;
                header.BodyWhole = SaveFileCheck.Check(PathOf(name), out problem);
                header.BodyProblem = problem;
            }
            return header;
        }

        public bool Delete(string name)
        {
            string path = PathOf(name);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return !File.Exists(path);
        }

        public void Flush(string name)
        {
            using (var stream = new FileStream(PathOf(name), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                stream.Flush(true);
        }

        public SaveFileStat Stat(string name) { return SaveFolder.Stat(PathOf(name)); }

        // Readable, torn or unknown (SaveFolder.ReadHeader): a file another process holds is never taken for a torn one.
        private SaveHeader Header(string name) { return SaveFolder.ReadHeader(name, PathOf(name)); }
    }
}
