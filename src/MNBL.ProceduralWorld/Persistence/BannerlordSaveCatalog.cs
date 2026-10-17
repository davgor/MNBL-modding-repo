using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-62: the game's own save list (installed 1.4.8 IL). MBSaveLoad's driver is the AsyncFileSaveDriver that Module.Initialize installs
    // before modules load: it runs FileDriver on a background task, and every read and delete first waits for the task before it, so a read
    // after OnSaveOver sees the finished file. MBSaveLoad.GetSaveFiles -> FileDriver.GetSaveGameFileInfos reads every "Game Saves\*.sav"
    // header without deserializing (a header that cannot be read, or has no ApplicationVersion, gives IsCorrupted). The game's listing hides
    // a folder it cannot read (it returns no files), so the folder is read again with System.IO (SaveFolder), which throws, and both lists
    // must name the same saves. A deletion goes through MBSaveLoad.DeleteSaveGame (FileDriver.Delete reports success even when the file
    // stays, so callers read the list again). Usable at the main menu with no game; vanilla lists saves at the initial state.
    internal sealed class BannerlordSaveCatalog : ISaveCatalog
    {
        public static string Folder { get { return Path.GetDirectoryName(SavePath("x")); } }

        public IList<SaveHeader> List()
        {
            var listed = MBSaveLoad.GetSaveFiles(null);
            SaveFolder.RequireSame(listed.Select(i => i.Name), SaveFolder.Names(Folder));
            return listed.Select(Header).ToList();
        }

        // MMR-48: the one header through the game's own driver (ISaveDriver.LoadMetaData: the reader the save list uses, after the async driver's
        // previous task; MBSaveLoad.GetSaveFileWithName would read every header in the folder after every save). When the game cannot read it
        // (null, a parse error, no ApplicationVersion: what the list calls corrupted) the file is read again sharing read, write and delete
        // (SaveFolder.ReadHeader): torn when the bytes are wrong, unknown when it cannot be opened (another process holds it), readable when
        // only the game's sharing mode was refused. Only a torn header may be treated as a torn write (review finding).
        public SaveHeader Read(string name)
        {
            bool onDisk = SaveFolder.Contains(Folder, name), listed = MBSaveLoad.IsSaveGameFileExists(name);
            if (onDisk != listed) throw new IOException("The save folder and the game disagree on whether " + name + " exists.");
            if (!onDisk) return null;
            MetaData metadata = null;
            try { metadata = Driver.LoadMetaData(name); } catch (Exception) { metadata = null; }
            SaveHeader header;
            if (metadata != null && metadata.Keys.Contains("ApplicationVersion"))
            {
                var entries = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var key in metadata.Keys) entries[key] = metadata[key];
                header = new SaveHeader { Name = name, Metadata = entries };
            }
            else header = SaveFolder.ReadHeader(name, SavePath(name));
            if (header.Readable)
            {
                string problem;
                header.BodyWhole = SaveFileCheck.Check(SavePath(name), out problem);
                header.BodyProblem = problem;
            }
            return header;
        }

        public bool Delete(string name) { return MBSaveLoad.DeleteSaveGame(name); }

        // The driver MBSaveLoad uses now (read each time: Module.Initialize installs the async driver, SetSaveDriver may replace it).
        private static ISaveDriver Driver
        {
            get
            {
                var driver = (ISaveDriver)HarmonyLib.AccessTools.Field(typeof(MBSaveLoad), "_saveDriver").GetValue(null);
                if (driver == null) throw new IOException("The game's save driver is not installed.");
                return driver;
            }
        }

        // MMR-48: the save file's size and last write time (SaveJournal records the file a later save replaces), or null when there is none.
        public static SaveFileStat Stat(string name) { return SaveFolder.Stat(SavePath(name)); }

        // Asks the system to write the save file's cached data to the disk (FlushFileBuffers through FileStream.Flush(true)), so a journal
        // that says published never outlives a save torn by a power loss. Throws when the file cannot be opened.
        public void Flush(string name)
        {
            using (var stream = new FileStream(SavePath(name), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                stream.Flush(true);
        }

        // The full path of a save file, resolved as FileDriver.GetSaveFilePath does (PlatformFileType.User, "Game Saves\<name>.sav"). The
        // integrity check reads it; probes use it to plant a torn save. The binding store never writes save files.
        public static string SavePath(string name)
        {
            return FileHelper.GetFileFullPath(new PlatformFilePath(new PlatformDirectoryPath(PlatformFileType.User, "Game Saves\\"), name + ".sav"));
        }

        private static SaveHeader Header(SaveGameFileInfo info)
        {
            if (info.IsCorrupted || info.MetaData == null) return new SaveHeader { Name = info.Name };
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in info.MetaData.Keys) metadata[key] = info.MetaData[key];
            return new SaveHeader { Name = info.Name, Metadata = metadata };
        }
    }
}
