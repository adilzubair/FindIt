using System.Diagnostics;

namespace FindIt.Core;

/// <summary>Only compiled into explicitly requested fault-validation builds.</summary>
internal static class PreparationFaults
{
    [Conditional("FINDIT_FAULT_INJECTION")]
    internal static void Hit(string archiveRoot, string point)
    {
#if FINDIT_FAULT_INJECTION
        // Activation requires the test runner's process environment AND a nonce in its
        // generated sandbox. An archive, manifest, query, or original cannot enable it.
        if (Environment.GetEnvironmentVariable("FINDIT_FAULT_POINT") != point) return;
        var nonce = Environment.GetEnvironmentVariable("FINDIT_FAULT_NONCE");
        var sandbox = Environment.GetEnvironmentVariable("FINDIT_FAULT_SANDBOX");
        if (!Guid.TryParseExact(nonce, "N", out _) || string.IsNullOrEmpty(sandbox)) return;
        sandbox = Path.GetFullPath(sandbox);
        if (Path.GetFileName(sandbox) != "FindIt-recovery-check-" + nonce ||
            !PortablePaths.IsWithin(sandbox, Path.GetFullPath(archiveRoot))) return;
        PortablePaths.RejectLinks(sandbox, archiveRoot);
        var marker = Path.Combine(sandbox, ".fault-test-nonce");
        PortablePaths.RejectLinks(sandbox, marker);
        if (!File.Exists(marker) || File.ReadAllText(marker) != nonce) return;
        var mode = Environment.GetEnvironmentVariable("FINDIT_FAULT_MODE");
        if (mode is not ("block" or "disk-full")) return;
        var hit = Path.Combine(sandbox, $"hit-{Environment.ProcessId}.json");
        var writing = hit + ".writing";
        using (var stream = new FileStream(writing, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            System.Text.Json.JsonSerializer.Serialize(stream, new { point, mode, processId = Environment.ProcessId, archiveRoot });
            stream.Flush(true);
        }
        File.Move(writing, hit);
        if (mode == "disk-full") throw new ArchiveSpaceException("Fault validation injected disk-full. The previous searchable snapshot is preserved.");
        var release = Path.Combine(sandbox, $"release-{Environment.ProcessId}");
        var wait = Stopwatch.StartNew();
        while (!File.Exists(release))
        {
            if (wait.Elapsed > TimeSpan.FromMinutes(2)) throw new IOException("Fault validation wait timed out.");
            Thread.Sleep(25);
        }
#endif
    }
}
