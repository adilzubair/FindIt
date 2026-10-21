using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FindIt.Bench;

public sealed record ScaleMemory(long WorkingSetBytes, long PrivateBytes, long ProcessPeakWorkingSetBytes);

public sealed class ScaleMemoryMonitor : IDisposable
{
    private readonly Process process = Process.GetCurrentProcess();
    private readonly object gate = new();
    private readonly Timer timer;
    private long residentPeak, privatePeak;
    public ScaleMemoryMonitor() { timer = new Timer(_ => Snapshot(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(25)); }
    public ScaleMemory Snapshot()
    {
        lock (gate)
        {
            process.Refresh();
            long resident = process.WorkingSet64, allocated = process.PrivateMemorySize64;
            residentPeak = Math.Max(residentPeak, resident); privatePeak = Math.Max(privatePeak, allocated);
            return new(resident, allocated, process.PeakWorkingSet64);
        }
    }
    public object Peak() { lock (gate) return new { workingSetBytes = residentPeak, privateBytes = privatePeak, samplingIntervalMs = 25 }; }
    public void Dispose() { timer.DisposeAsync().AsTask().GetAwaiter().GetResult(); lock (gate) process.Dispose(); }
}

public static class ScaleHardware
{
    public static object Read()
    {
        string? cpu = null; ulong? memory = null;
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = key?.GetValue("ProcessorNameString")?.ToString()?.Trim();
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref status)) memory = status.TotalPhysical;
        }
        return new { cpu, logicalProcessors = Environment.ProcessorCount, totalPhysicalMemoryBytes = memory,
            os = RuntimeInformation.OSDescription, processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runtime = RuntimeInformation.FrameworkDescription, vectorAccelerated = System.Numerics.Vector.IsHardwareAccelerated,
            simdFloatLanes = System.Numerics.Vector<float>.Count };
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
