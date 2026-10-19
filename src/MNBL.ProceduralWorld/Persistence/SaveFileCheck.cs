using System;
using System.IO;
using System.IO.Compression;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-62: whether a save file is whole, without deserializing it. The installed 1.4.8 FileDriver.Save (run on a background task by the
    // AsyncFileSaveDriver that Module.Initialize installs) writes the file once with File.WriteAllBytes as: the header (MetaData.Serialize:
    // an Int32 length, then that many bytes of JSON), then a raw deflate stream of GameData.Write: Int32 header length + header bytes; Int32
    // object count, each Int32 length + bytes; Int32 container count, each Int32 length + bytes; Int32 strings length + strings bytes. A
    // write cut short can leave a readable header in front of torn game data, so a header read back is not enough: the game data is inflated
    // and walked to its end, and nothing may follow it. Three outcomes: true (whole), false (torn: the bytes are there and wrong), null (the
    // file could not be read: a sharing violation, a permission error, a cloud placeholder that cannot be fetched). Only a torn file may be
    // treated as a torn write; an unreadable one is unknown. Reads the whole file (an initial save is checked once).
    internal static class SaveFileCheck
    {
        private const int MaxSection = 1 << 30;

        public static bool? Check(string path, out string problem)
        {
            problem = null;
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var header = new BinaryReader(file))
                {
                    if (file.Length < 4) { problem = "shorter than its header length"; return false; }
                    int metadata = header.ReadInt32();
                    if (metadata <= 0 || metadata > file.Length - 4) { problem = "header length " + metadata + " exceeds the file"; return false; }
                    file.Seek(metadata, SeekOrigin.Current);
                    using (var inflated = new DeflateStream(file, CompressionMode.Decompress, true))
                    using (var data = new BinaryReader(inflated))
                    {
                        Skip(data, Length(data, "game data header"));
                        for (int i = 0, count = Length(data, "object count"); i < count; i++) Skip(data, Length(data, "object"));
                        for (int i = 0, count = Length(data, "container count"); i < count; i++) Skip(data, Length(data, "container"));
                        Skip(data, Length(data, "strings"));
                        if (inflated.Read(new byte[1], 0, 1) != 0) { problem = "data follows the game data's end"; return false; }
                    }
                }
                return true;
            }
            catch (EndOfStreamException) { problem = "the game data ends early (a torn write)"; return false; }
            catch (InvalidDataException error) { problem = "the game data does not inflate: " + error.Message; return false; }
            catch (IOException error) { problem = "could not read the file: " + error.GetType().Name + ": " + error.Message; return null; }
            catch (UnauthorizedAccessException error) { problem = "could not read the file: " + error.GetType().Name + ": " + error.Message; return null; }
        }

        private static int Length(BinaryReader reader, string what)
        {
            int value = reader.ReadInt32();
            if (value < 0 || value > MaxSection) throw new InvalidDataException(what + " length " + value + " is not plausible");
            return value;
        }

        private static readonly byte[] buffer = new byte[1 << 16];

        private static void Skip(BinaryReader reader, int count)
        {
            lock (buffer)
                while (count > 0)
                {
                    int read = reader.Read(buffer, 0, Math.Min(count, buffer.Length));
                    if (read <= 0) throw new EndOfStreamException();
                    count -= read;
                }
        }
    }
}
