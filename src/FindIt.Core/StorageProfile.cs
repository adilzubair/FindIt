using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FindIt.Core;

internal static class StorageProfile
{
    public static bool ShouldPreload(string archiveRoot, long serializedBytes)
    {
        if (serializedBytes > 1536L * 1024 * 1024) return false;
        string? mode = Environment.GetEnvironmentVariable("FINDIT_INDEX_MODE");
        if (string.Equals(mode, "view", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(mode, "load", StringComparison.OrdinalIgnoreCase)) return EnoughMemory(serializedBytes);
        if (!OperatingSystem.IsWindows()) return false;
        string? volume = Path.GetPathRoot(archiveRoot);
        if (volume is null || volume.Length < 2 || volume[1] != ':') return false;
        try
        {
            using var handle = CreateFileW(@"\\.\" + volume[..2], 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return false;
            var query = new byte[12]; query[0] = 7; // StorageDeviceSeekPenaltyProperty; PropertyStandardQuery.
            var descriptor = new byte[16];
            return DeviceIoControl(handle, 0x002D1400, query, query.Length, descriptor, descriptor.Length, out int returned, IntPtr.Zero) && returned >= 9 && descriptor[8] != 0 && EnoughMemory(serializedBytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    private static bool EnoughMemory(long bytes)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) && status.AvailablePhysical >= (ulong)bytes + 800UL * 1024 * 1024;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
