using System.Diagnostics;
using System.Globalization;

namespace FindIt.DemoRecorder;

/// <summary>Records one window with Windows.Graphics.Capture through ffmpeg's gfxcapture source.</summary>
internal sealed class Capture : IDisposable
{
    private readonly Process process;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double originMs = double.NaN;
    private readonly object gate = new();

    private Capture(Process process) => this.process = process;

    /// <summary>Milliseconds on the recorded video's own timeline.</summary>
    public double Now { get { lock (gate) return clock.Elapsed.TotalMilliseconds - originMs; } }

    public static Capture Start(string ffmpeg, nint hwnd, string output)
    {
        // 4:4:4 keeps coloured UI text crisp; constant QP with no B-frames or lookahead keeps
        // the encoder's reported time close to the moment a frame was captured.
        var args = new[]
        {
            "-hide_banner", "-y", "-nostats", "-progress", "pipe:2", "-stats_period", "0.05",
            "-filter_complex", $"gfxcapture=hwnd={hwnd}:capture_cursor=0:max_framerate=60,hwdownload,format=bgra,format=yuv444p",
            "-fps_mode", "cfr", "-r", "60",
            "-c:v", "h264_nvenc", "-preset", "p7", "-tune", "hq", "-rc", "constqp", "-qp", "14", "-bf", "0", "-rc-lookahead", "0", "-profile:v", "high444p",
            output,
        };
        var info = new ProcessStartInfo(ffmpeg, args) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true };
        var capture = new Capture(Process.Start(info)!);
        var started = new ManualResetEventSlim();
        var tail = new Queue<string>();
        capture.process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (tail) { tail.Enqueue(e.Data); if (tail.Count > 20) tail.Dequeue(); }
            if (!e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) || !long.TryParse(e.Data.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) || us <= 0) return;
            lock (capture.gate)
                if (double.IsNaN(capture.originMs)) capture.originMs = capture.clock.Elapsed.TotalMilliseconds - us / 1000.0;
            started.Set();
        };
        capture.process.BeginErrorReadLine();
        if (!started.Wait(TimeSpan.FromSeconds(15)))
        {
            capture.Dispose();
            lock (tail) throw new InvalidOperationException("Capture did not start:\n" + string.Join('\n', tail));
        }
        return capture;
    }

    /// <summary>
    /// Windows.Graphics.Capture only delivers a frame when the window changes, so ffmpeg sits
    /// waiting during a still shot and would not read the quit request. <paramref name="nudge"/>
    /// makes the window repaint: that frame fills the still tail at a constant rate and ends the recording.
    /// </summary>
    public void Stop(Action nudge)
    {
        if (process.HasExited) return;
        process.StandardInput.Write('q');
        process.StandardInput.Flush();
        for (var attempt = 0; attempt < 10 && !process.WaitForExit(500); attempt++) nudge();
        if (!process.WaitForExit(15000)) { process.Kill(); throw new InvalidOperationException("ffmpeg did not stop; the recording is incomplete."); }
        if (process.ExitCode != 0) throw new InvalidOperationException($"ffmpeg exited with {process.ExitCode}.");
    }

    public void Dispose()
    {
        if (!process.HasExited) process.Kill();
        process.Dispose();
    }
}
