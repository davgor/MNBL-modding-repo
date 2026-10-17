using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-62: the save folder read so that a failure shows. The game's own listing (PlatformFileHelperPC.GetFiles in 1.4.8) catches an
    // enumeration error and returns no files, so an unreadable "Game Saves" folder looks empty. BannerlordSaveCatalog therefore reads the
    // folder again with System.IO, which throws, and requires the game's list to name the same saves. Only a folder that does not exist (no
    // save made yet) is empty. The pattern is the game's ("*.sav" through DirectoryInfo.GetFiles), so both lists match the same files.
    internal static class SaveFolder
    {
        public static List<string> Names(string directory)
        {
            try { return new DirectoryInfo(directory).GetFiles("*.sav", SearchOption.TopDirectoryOnly).Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToList(); }
            catch (DirectoryNotFoundException) { return new List<string>(); }
        }

        // Whether the folder holds the save; throws when the folder cannot be read.
        public static bool Contains(string directory, string name)
        {
            return Names(directory).Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        // A save file's header metadata (Bannerlord's MetaData: an Int32 length, then that many bytes of JSON {"List": {key: value}}), read
        // without the game (MMR-47's inspector, MMR-48's folder catalog and transfer). Throws InvalidDataException when it cannot be read.
        public static IDictionary<string, string> ReadMetadata(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new BinaryReader(file))
            {
                if (file.Length < 4) throw new InvalidDataException("shorter than its header length");
                int length = reader.ReadInt32();
                if (length <= 0 || length > file.Length - 4 || length > (16 << 20)) throw new InvalidDataException("header length " + length + " does not fit the file");
                var text = new UTF8Encoding(false).GetString(reader.ReadBytes(length));
                IDictionary<string, object> root;
                try { root = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(text) as IDictionary<string, object>; }
                catch (ArgumentException error) { throw new InvalidDataException("the header is not JSON: " + error.Message); }
                catch (InvalidOperationException error) { throw new InvalidDataException("the header is not JSON: " + error.Message); }
                var list = root == null ? null : (root.ContainsKey("List") ? root["List"] : null) as IDictionary<string, object>;
                if (list == null) throw new InvalidDataException("the header is not a metadata list");
                return list.ToDictionary(kv => kv.Key, kv => Convert.ToString(kv.Value, CultureInfo.InvariantCulture), StringComparer.Ordinal);
            }
        }

        // MMR-48: a save's header in three outcomes, as SaveFileCheck gives the game data: readable (Metadata set), torn (the bytes are there and
        // wrong, or the header lacks ApplicationVersion, which the game lists as corrupted: Metadata null) or unknown (the file could not be
        // opened: HeaderUnknown). Opened sharing read, write and delete, so a process holding the file open does not make a whole save look torn.
        public static SaveHeader ReadHeader(string name, string path)
        {
            try
            {
                var metadata = ReadMetadata(path);
                if (!metadata.ContainsKey("ApplicationVersion")) return new SaveHeader { Name = name, HeaderProblem = "the header has no ApplicationVersion (the game lists it as corrupted)" };
                return new SaveHeader { Name = name, Metadata = metadata };
            }
            catch (InvalidDataException error) { return new SaveHeader { Name = name, HeaderProblem = error.Message }; }
            catch (IOException error) { return new SaveHeader { Name = name, HeaderUnknown = true, HeaderProblem = error.GetType().Name + ": " + error.Message }; }
            catch (UnauthorizedAccessException error) { return new SaveHeader { Name = name, HeaderUnknown = true, HeaderProblem = error.GetType().Name + ": " + error.Message }; }
        }

        // A save file's size and last write time, or null when there is none.
        public static SaveFileStat Stat(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return new SaveFileStat { Length = info.Length, WrittenUtcTicks = info.LastWriteTimeUtc.Ticks };
        }

        public static void RequireSame(IEnumerable<string> listedByGame, IEnumerable<string> onDisk)
        {
            var game = new HashSet<string>(listedByGame, StringComparer.OrdinalIgnoreCase);
            var disk = new HashSet<string>(onDisk, StringComparer.OrdinalIgnoreCase);
            var missing = disk.Where(n => !game.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var extra = game.Where(n => !disk.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (missing.Count > 0 || extra.Count > 0)
                throw new IOException("The game's save list and the save folder differ (" + missing.Count + " on disk only" + (missing.Count > 0 ? ": " + String.Join(", ", missing.Take(5)) : "") +
                    "; " + extra.Count + " listed only" + (extra.Count > 0 ? ": " + String.Join(", ", extra.Take(5)) : "") + ").");
        }
    }
}
