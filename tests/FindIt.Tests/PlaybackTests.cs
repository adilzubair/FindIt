using System.Diagnostics;
using FindIt.Client;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class PlaybackTests
{
    [TestMethod]
    public async Task SelectedMediaOpensPausedAtTimestampAndOnlyExplicitPlayAdvancesIt()
    {
        // A generated PCM original keeps this a real bundled-LibVLC/worker integration check
        // without depending on downloaded fixtures or a host-installed media codec.
        var worker = FindWorker();
        var workerFolder = Path.GetDirectoryName(worker)!;
        if (!File.Exists(Path.Combine(workerFolder, "libvlc", "win-x64", "libvlc.dll")) && !File.Exists(Path.Combine(workerFolder, "runtime", "vlc", "libvlc.dll")))
            Assert.Inconclusive("Build FindIt.Worker with its bundled LibVLC runtime before running playback integration tests.");
        var root = Path.Combine(Path.GetTempPath(), "FindIt-playback-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var media = Path.Combine(root, "six-second-silence.wav");
        const int samplesPerSecond = 16000, durationSeconds = 6;
        using (var file = new BinaryWriter(File.Create(media)))
        {
            var dataBytes = samplesPerSecond * durationSeconds * sizeof(short);
            file.Write("RIFF"u8); file.Write(36 + dataBytes); file.Write("WAVEfmt "u8);
            file.Write(16); file.Write((short)1); file.Write((short)1); file.Write(samplesPerSecond);
            file.Write(samplesPerSecond * sizeof(short)); file.Write((short)sizeof(short)); file.Write((short)16);
            file.Write("data"u8); file.Write(dataBytes); file.Write(new byte[dataBytes]);
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var player = new PreviewPlayerClient(root, 0);
            var opened = await player.OpenAsync(media, 1000, timeout.Token);
            Assert.IsFalse(opened.Playing, "Selecting a preview must not autoplay.");
            Assert.AreEqual(6000, opened.DurationMs);
            Assert.IsTrue(opened.PositionMs >= 950 && opened.PositionMs <= 1100, $"Expected the selected timestamp, got {opened.PositionMs}.");
            await AssertRemainsPausedAsync(opened.PositionMs);

            await player.SetVolumeAsync(0, timeout.Token);
            await player.PauseAsync(timeout.Token); // Explicit Play resumes the initially paused media.
            var playing = await WaitAsync(state => state.Playing && state.PositionMs > opened.PositionMs + 100);
            await player.PauseAsync(timeout.Token);
            var paused = await WaitAsync(state => !state.Playing);
            await AssertRemainsPausedAsync(paused.PositionMs);

            await player.SeekAsync(3000, timeout.Token);
            var sought = await WaitAsync(state => !state.Playing && Math.Abs(state.PositionMs - 3000) <= 100);
            await AssertRemainsPausedAsync(sought.PositionMs);
            await player.PauseAsync(timeout.Token);
            await WaitAsync(state => state.Playing && state.PositionMs > sought.PositionMs + 100);
            Assert.IsFalse((await player.StopAsync(timeout.Token)).Playing);

            // The separate explicit autoplay path remains supported after a paused input.
            await player.PlayAsync(media, 0, timeout.Token);
            await WaitAsync(state => state.Playing && state.PositionMs > 100);
            Assert.IsFalse((await player.StopAsync(timeout.Token)).Playing);

            async Task<PlaybackState> WaitAsync(Func<PlaybackState, bool> condition)
            {
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var state = await player.GetStateAsync(timeout.Token);
                    if (condition(state)) return state;
                    await Task.Delay(50, timeout.Token);
                }
            }
            async Task AssertRemainsPausedAsync(long position)
            {
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromMilliseconds(350))
                {
                    await Task.Delay(50, timeout.Token);
                    var state = await player.GetStateAsync(timeout.Token);
                    Assert.IsFalse(state.Playing, "Media started without an explicit play action.");
                    Assert.IsTrue(Math.Abs(state.PositionMs - position) <= 50, "The paused clock advanced.");
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static string FindWorker()
    {
        var configured = Environment.GetEnvironmentVariable("FINDIT_WORKER_PATH");
        if (configured is not null && File.Exists(configured)) return configured;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(folder.FullName, "src", "FindIt.Worker", "bin", configuration, "net10.0", "FindIt.Worker.exe");
                if (File.Exists(candidate)) return candidate;
            }
        Assert.Inconclusive("Build FindIt.Worker before running playback integration tests.");
        throw new InvalidOperationException();
    }
}
