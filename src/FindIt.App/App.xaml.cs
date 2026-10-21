using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.Json;
using FindIt.Client;

namespace FindIt.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        AppIdentity.Initialize();
        base.OnStartup(e);
        string? Argument(string name) => Array.IndexOf(e.Args, name) is var index && index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : null;
        var root = Argument("--root") ?? Path.GetPathRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        var renderPath = Argument("--render-ui");
        try
        {
            var window = new MainWindow(null, root);
            MainWindow = window;
            window.Show();
            if (Argument("--interaction-smoke") is { } interactionReport)
            {
                await window.RunInteractionSmokeAsync(interactionReport);
                Shutdown();
                return;
            }
            if ((Argument("--playback-smoke") ?? Argument("--paused-playback-smoke")) is { } mediaPath)
            {
                await window.RunPlaybackSmokeAsync(mediaPath, Argument("--playback-report") ?? Path.Combine(root, "playback-smoke.json"), e.Args.Contains("--paused-playback-smoke"), long.TryParse(Argument("--playback-start-ms"), out var startMs) ? startMs : 0);
                Shutdown();
                return;
            }
            if (renderPath is null || e.Args.Contains("--render-open-archive"))
            {
                var backend = await WorkerArchiveClient.CreateAsync(root, CancellationToken.None);
                await window.AttachBackendAsync(backend);
            }
            if (Argument("--evidence-smoke") is { } evidenceReport)
            {
                await window.RunEvidenceSmokeAsync(evidenceReport, Argument("--render-query") ?? "Test Person driving");
                Shutdown();
                return;
            }
            if (renderPath is not null)
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.ShowRenderPage(Argument("--render-page") ?? "search");
                if (e.Args.Contains("--render-open-archive")) await window.RenderArchiveAsync(Argument("--render-query"), e.Args.Contains("--render-select-first"), Argument("--render-kind"), e.Args.Contains("--render-person-first"), e.Args.Contains("--render-exact"));
                window.SetDarkTheme(e.Args.Contains("--dark"));
                if (double.TryParse(Argument("--width"), out var width)) window.Width = width;
                if (double.TryParse(Argument("--height"), out var height)) window.Height = height;
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (e.Args.Contains("--render-open-archive")) await Task.Delay(500);
                await window.PrepareCaptureAsync();
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var surface = (FrameworkElement)window.Content;
                var dpi = double.TryParse(Argument("--render-dpi"), out var requestedDpi) ? Math.Clamp(requestedDpi, 96, 384) : 96;
                var bitmap = new RenderTargetBitmap((int)(surface.ActualWidth * dpi / 96), (int)(surface.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(renderPath))!);
                await using var file = File.Create(renderPath);
                encoder.Save(file);
                await window.CleanupAfterCaptureAsync();
                Shutdown();
            }
        }
        catch (Exception ex)
        {
            if (Argument("--evidence-smoke") is { } evidenceReport)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(evidenceReport))!);
                await File.WriteAllTextAsync(evidenceReport, JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
                Shutdown(1);
                return;
            }
            if (Argument("--interaction-smoke") is not null) { Shutdown(1); return; }
            if (Argument("--playback-smoke") is not null || Argument("--paused-playback-smoke") is not null)
            {
                var output = Argument("--playback-report") ?? Path.Combine(root, "playback-smoke.json");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
                Shutdown(1);
                return;
            }
            ThemedDialog.Show(null, $"FindIt could not open this archive.\n\n{root}\n\n{ex.Message}\n\nKeep the drive connected and try again.", "Open archive", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
    }
}
