using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FindIt.Shared;

namespace FindIt.App;

public partial class MainWindow
{
    // Explicit workspace validation: real reader, prepared evidence and native player.
    public async Task RunEvidenceSmokeAsync(string reportPath, string query)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var checks = new List<string>();
        void Require(bool condition, string name)
        {
            if (!condition) throw new IOException(name);
            checks.Add(name);
        }
        await RenderArchiveAsync(query, true, "video");
        Require(_preview?.PersonEvidence.Count > 0 && _player is not null, "real-person-evidence-and-player-open");
        Require(_showingPreparedFrame && PreviewImage.Source is BitmapSource { PixelWidth: > 0 } && VideoPlayer.Visibility == Visibility.Hidden && VideoPlayer.WindowHandle != 0,
            "sampled-context-frame-visible-before-play");
        Require(PersonEvidenceFaces.ItemsSource is IReadOnlyCollection<EvidenceFaceTile> faces && faces.Count == _preview!.PersonEvidence.Count && faces.All(f => f.Portrait is not null),
            "matched-face-crops-visible");
        long expected = _preview!.StartMs!.Value;
        var opened = await _player!.GetStateAsync(timeout.Token);
        Require(!opened.Playing && Math.Abs(opened.PositionMs - expected) <= 100, "opens-paused-at-evidence-time");
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 600)
        {
            await Task.Delay(75, timeout.Token);
            var state = await _player.GetStateAsync(timeout.Token);
            Require(!state.Playing && Math.Abs(state.PositionMs - expected) <= 100, "paused-clock-remains-at-evidence-time");
        }
        var button = VisualChildren<Button>(VideoControls).Single(b => Equals(b.Content, "Play / pause"));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        while (!(await _player.GetStateAsync(timeout.Token)).Playing || _showingPreparedFrame)
            await Task.Delay(50, timeout.Token);
        Require(VideoPlayer.Visibility == Visibility.Visible && PreviewImage.Source is null, "explicit-play-reveals-decoded-footage");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        while ((await _player.GetStateAsync(timeout.Token)).Playing) await Task.Delay(50, timeout.Token);
        Require(true, "explicit-pause-stops-playback");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            passed = true, query, file = _selectedResult!.RelativePath, expectedStartMs = expected, opened,
            checks = checks.Distinct().ToArray(),
            scope = "Real workspace archive and isolated LibVLC player. The initial poster is the prepared model-input frame; decoded seek pictures may round at scene boundaries."
        }, new JsonSerializerOptions(WireJson.Options) { WriteIndented = true }));
        await CleanupAfterCaptureAsync();
    }
}
