using System.IO;
using System.Text.Json;
using System.Windows;

namespace FindIt.DemoRecorder;

/// <summary>What happened during a take, in milliseconds and pixels of the recorded video.</summary>
internal sealed class TakeLog(string name, Capture capture, nint hwnd, uint dpi)
{
    private readonly Win32.Rect frame = Win32.Bounds(hwnd);
    public List<Event> Events { get; } = [];

    public sealed record Event(double T, string Type, string? Target, double[]? Rect, string? Text);

    public void Add(string type, string? target = null, Rect? screen = null, string? text = null)
        => Events.Add(new(Math.Round(capture.Now, 1), type, target, screen is { } r ? Local(r) : null, text));

    public void Mark(string label, IEnumerable<(string Target, Rect Screen)>? rects = null)
    {
        var t = Math.Round(capture.Now, 1);
        Events.Add(new(t, "mark", label, null, null));
        foreach (var (target, screen) in rects ?? []) Events.Add(new(t, "rect", target, Local(screen), label));
    }

    private double[] Local(Rect r) => [Math.Round(r.X - frame.Left, 1), Math.Round(r.Y - frame.Top, 1), Math.Round(r.Width, 1), Math.Round(r.Height, 1)];

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        take = name,
        dpi,
        frame = new { width = frame.Right - frame.Left, height = frame.Bottom - frame.Top },
        events = Events,
    }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
}
