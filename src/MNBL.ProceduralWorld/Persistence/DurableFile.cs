using System;
using System.IO;
using System.Text;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-62: a small file replaced whole or not at all. The bytes go to <path>.tmp-<guid> on the same volume, written through and
    // flushed to the disk, then the temporary file is renamed over the target (File.Replace when it exists, File.Move otherwise: one
    // same-volume rename, atomic on NTFS). A crash leaves either the old file or the new one, plus at most a temporary file, which is
    // staging (StagingCleanup removes it). The directory entry itself is not flushed: after a power loss the rename may be undone, which
    // leaves the old file, never a torn one.
    internal static class DurableFile
    {
        public const string TemporaryMarker = ".tmp-";

        public static void WriteAllText(string path, string text) { WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text)); }

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temporary = path + TemporaryMarker + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null, true);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        public static bool IsTemporary(string path) { return Path.GetFileName(path).IndexOf(TemporaryMarker, StringComparison.Ordinal) > 0; }
    }
}
