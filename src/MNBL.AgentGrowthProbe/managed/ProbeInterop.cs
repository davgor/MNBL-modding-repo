using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MNBL.AgentGrowthProbe
{
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct ProbeResult
    {
        public ulong abi_size, abi_version, status, stage, capacity, pointer_checks, header_checks;
        public ulong allocation_attempts, allocations, releases, constructor_attempts;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)] public ulong[] constructed;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)] public ulong[] destroyed;
        public ulong allocation_failure_checked, constructor_failure_checked, exhaustion_checked, reuse_checked;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] error;
        public string ErrorText { get { return error == null ? "" : Encoding.UTF8.GetString(error).TrimEnd('\0'); } }
    }

    public static class ProbeInterop
    {
        public const int ResultBytes = 712;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong ResultSize();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Execute(IntPtr result, ulong size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetModuleFileName(IntPtr module, StringBuilder path, int length);
        [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
        public static string Hash(string path)
        {
            using (var file = File.OpenRead(path)) using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }
        public static bool ValidHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
        public static void ValidateSuccess(int code, ProbeResult r)
        {
            ulong[] expected = {120, 97, 90, 90, 90};
            if (code != 0 || r.abi_size != 712 || r.abi_version != 1 || r.status != 1 || r.stage != 8 ||
                r.capacity != 90 || r.pointer_checks != 600 || r.header_checks != 450 ||
                r.allocation_attempts != 21 || r.allocations != 20 || r.releases != 20 || r.constructor_attempts != 488 ||
                r.allocation_failure_checked != 1 || r.constructor_failure_checked != 1 || r.exhaustion_checked != 1 || r.reuse_checked != 1 ||
                r.constructed == null || r.destroyed == null || r.constructed.Length != 5 || r.destroyed.Length != 5 || r.ErrorText != "")
                throw new InvalidOperationException("Private native probe result rejected: " + r.ErrorText);
            for (int i = 0; i < 5; ++i)
                if (r.constructed[i] != expected[i] || r.destroyed[i] != expected[i])
                    throw new InvalidOperationException("Private native probe cleanup counters rejected.");
        }
        public static ProbeResult Call(string dllPath, string expectedHash, out int code)
        {
            if (!Path.IsPathRooted(dllPath) || !ValidHash(expectedHash)) throw new ArgumentException("Absolute DLL path and SHA256 required.");
            dllPath = Path.GetFullPath(dllPath);
            if (Marshal.SizeOf(typeof(ProbeResult)) != ResultBytes || Marshal.OffsetOf(typeof(ProbeResult), "error").ToInt32() != 200)
                throw new InvalidOperationException("Probe ABI layout differs.");
            // Deny replacement/deletion while hashing, loading and calling the DLL.
            using (var locked = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (Hash(dllPath) != expectedHash) throw new InvalidOperationException("Probe DLL identity differs.");
                // Dependency lookup is confined to this absolute DLL directory and System32.
                IntPtr library = LoadLibraryEx(dllPath, IntPtr.Zero, 0x100 | 0x800);
                if (library == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var actual = new StringBuilder(32768);
                    uint length = GetModuleFileName(library, actual, actual.Capacity);
                    if (length == 0 || length >= actual.Capacity || !String.Equals(Path.GetFullPath(actual.ToString()), dllPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Loaded probe DLL path differs.");
                    IntPtr sizeAddress = GetProcAddress(library, "MNBLElementProbeResultSize");
                    IntPtr entryAddress = GetProcAddress(library, "MNBLElementProbe");
                    if (sizeAddress == IntPtr.Zero || entryAddress == IntPtr.Zero) throw new InvalidOperationException("Probe exports absent.");
                    var size = (ResultSize)Marshal.GetDelegateForFunctionPointer(sizeAddress, typeof(ResultSize));
                    var run = (Execute)Marshal.GetDelegateForFunctionPointer(entryAddress, typeof(Execute));
                    if (size() != ResultBytes) throw new InvalidOperationException("Native probe ABI size differs.");
                    IntPtr allocation = Marshal.AllocHGlobal(ResultBytes + 64);
                    try
                    {
                        byte[] poison = new byte[ResultBytes + 64]; for (int i = 0; i < poison.Length; ++i) poison[i] = 0xa5;
                        Marshal.Copy(poison, 0, allocation, poison.Length);
                        IntPtr result = IntPtr.Add(allocation, 32);
                        code = run(result, ResultBytes);
                        Marshal.Copy(allocation, poison, 0, poison.Length);
                        for (int i = 0; i < 32; ++i) if (poison[i] != 0xa5 || poison[ResultBytes + 32 + i] != 0xa5)
                            throw new InvalidOperationException("Probe ABI canary changed.");
                        return (ProbeResult)Marshal.PtrToStructure(result, typeof(ProbeResult));
                    }
                    finally { Marshal.FreeHGlobal(allocation); }
                }
                finally { FreeLibrary(library); }
            }
        }
    }
}
