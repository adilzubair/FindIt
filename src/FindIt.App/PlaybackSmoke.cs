using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FindIt.Client;
using FindIt.Shared;

namespace FindIt.App;

public partial class MainWindow
{
    /// <summary>Explicit release-QA mode: exercises the actual isolated player in a real child HWND.</summary>
    public async Task RunPlaybackSmokeAsync(string path, string reportPath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var timer = Stopwatch.StartNew();
        path = Path.GetFullPath(path);
        _playbackSmoke = true; ApplyResponsiveLayout();
        PreviewName.Text = Path.GetFileName(path); PreviewPath.Text = path; PreviewPlaceholder.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Visible; UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        _player = new PreviewPlayerClient(_root, VideoPlayer.WindowHandle);
        await _player.PlayAsync(path, 0, timeout.Token);
        await _player.SetVolumeAsync(0, timeout.Token);
        async Task<PlaybackState> WaitAsync(Func<PlaybackState, bool> condition)
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var state = await _player.GetStateAsync(timeout.Token);
                if (condition(state)) return state;
                await Task.Delay(75, timeout.Token);
            }
        }
        var first = await WaitAsync(state => state.Playing && state.DurationMs > 0 && state.PositionMs > 50);
        // LibVLC publishes clock samples on its own cadence; allow bounded time for a new sample.
        var moving = await WaitAsync(state => state.Playing && state.PositionMs > first.PositionMs + 100);
        await _player.PauseAsync(timeout.Token);
        var paused = await WaitAsync(state => !state.Playing);
        await Task.Delay(200, timeout.Token);
        var stable = await _player.GetStateAsync(timeout.Token);
        if (Math.Abs(stable.PositionMs - paused.PositionMs) > 150) throw new IOException("The player did not remain paused.");
        await _player.PauseAsync(timeout.Token);
        var resumed = await WaitAsync(state => state.Playing && state.PositionMs > stable.PositionMs);
        var seekTarget = Math.Max(100, first.DurationMs / 3);
        await _player.SeekAsync(seekTarget, timeout.Token);
        var sought = await WaitAsync(state => state.PositionMs >= seekTarget && state.PositionMs < seekTarget + 1200);
        var stopped = await _player.StopAsync(timeout.Token);
        if (stopped.Playing) throw new IOException("Playback did not stop.");
        var report = new { passed = true, path, nativeWindow = VideoPlayer.WindowHandle.ToInt64(), elapsedMs = timer.Elapsed.TotalMilliseconds, first, moving, paused, stable, resumed, seekTarget, sought, stopped, isolation = "LibVLC runs in a separate preview worker; UI owns only the child HWND." };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(WireJson.Options) { WriteIndented = true }));
        await CleanupAfterCaptureAsync();
    }

    public async Task CleanupAfterCaptureAsync()
    {
        _statusTimer.Stop();
        await DisposePlaybackAsync();
        if (_backend is not null) { await _backend.DisposeAsync(); _backend = null; }
    }
}
