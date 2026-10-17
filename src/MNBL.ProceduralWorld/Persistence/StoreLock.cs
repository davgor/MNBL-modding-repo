using System;
using System.IO;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-62: one game process at a time may recover journals and clean staging in the store. The lock is <store>/store.lock opened without
    // sharing and held until it is disposed (the process's lifetime for SaveBindingRuntime); the operating system releases it when the
    // process ends, a crash included, so a stale lock never blocks a later start.
    internal sealed class StoreLock : IDisposable
    {
        private FileStream stream;

        public static StoreLock TryAcquire(string store)
        {
            Directory.CreateDirectory(store);
            try { return new StoreLock { stream = new FileStream(Path.Combine(store, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) }; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public bool Held { get { return stream != null; } }

        public void Dispose()
        {
            if (stream != null) { stream.Dispose(); stream = null; }
        }
    }
}
