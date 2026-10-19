using System;
using System.IO;
using System.Text;

namespace MNBL.ProceduralWorld.Diagnostics
{
    internal static class NavigationFixture
    {
        // Experimental NMG9 encoder derived from the installed prefab layout.
        // Native import/path tests must validate it before any production use.
        internal static string Write(int seed, string existingName = null)
        {
            var module = new FileInfo(typeof(NavigationFixture).Assembly.Location).Directory.Parent.Parent;
            var directory = Path.Combine(module.FullName, "NavMeshPrefabs");
            Directory.CreateDirectory(directory);
            if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Navigation fixture directory must not be redirected.");
            var name = existingName ?? "mnbl_probe_" + seed + "_" + Guid.NewGuid().ToString("N");
            if (!name.StartsWith("mnbl_probe_", StringComparison.Ordinal) || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("Invalid diagnostic fixture name.");
            var path = Path.Combine(directory, name + ".bin");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Navigation fixture file must not be redirected.");
            using (var stream = new FileStream(path, existingName == null ? FileMode.CreateNew : FileMode.Truncate))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0); // Engine's payload byte count excludes this prefix.
                writer.Write(Encoding.ASCII.GetBytes("NMG9"));
                writer.Write(6);
                for (int y = 0; y < 2; y++) for (int x = 0; x < 3; x++)
                {
                    writer.Write((float)x * 10); writer.Write((float)y * 10); writer.Write(1f + (seed - 42) * x);
                }
                int[,] edges = { {0,1}, {1,4}, {4,3}, {3,0}, {1,2}, {2,5}, {5,4} };
                writer.Write(7);
                for (int i=0; i<7; i++)
                {
                    writer.Write(-1); writer.Write(edges[i,0]); writer.Write(edges[i,1]);
                    writer.Write(-1); writer.Write(-1); writer.Write(0);
                }
                writer.Write(2);
                WriteFace(writer, new[] {0,1,4,3}, new[] {0,1,2,3}, 0);
                WriteFace(writer, new[] {1,2,5,4}, new[] {4,5,6,1}, 1);
                writer.Write(32);
                for (int i=0; i<32; i++) writer.Write(0L);
                long length = stream.Length;
                stream.Position = 0;
                writer.Write(checked((int)length - 4));
            }
            return name;
        }

        private static void WriteFace(BinaryWriter writer, int[] vertices, int[] edges, int group)
        {
            writer.Write(4);
            foreach (int value in vertices) writer.Write(value);
            foreach (int value in edges) writer.Write(value);
            writer.Write(2); writer.Write(0); writer.Write(group); writer.Write(0); writer.Write(-1); writer.Write((byte)0);
        }
    }
}
