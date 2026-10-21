using System.Text.Json;

namespace FindIt.Runtime;

public sealed record SpeechPassage(long StartMs, long EndMs, string Text);

/// <summary>Converts whisper.cpp millisecond offsets to intervals on the original file.</summary>
public static class SpeechTimestamps
{
    public static IReadOnlyList<SpeechPassage> Parse(JsonElement document, long windowStartMs, long windowDurationMs)
    {
        if (windowStartMs < 0 || windowDurationMs < 0) throw new ArgumentOutOfRangeException(nameof(windowStartMs));
        if (!document.TryGetProperty("transcription", out var rows) || rows.ValueKind != JsonValueKind.Array) return [];
        var result = new List<SpeechPassage>();
        foreach (var row in rows.EnumerateArray())
        {
            string text = row.TryGetProperty("text", out var words) ? words.GetString()?.Trim() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            long start = 0, end = windowDurationMs;
            if (row.TryGetProperty("offsets", out var offsets))
            {
                if (offsets.TryGetProperty("from", out var from) && from.TryGetInt64(out var value)) start = value;
                if (offsets.TryGetProperty("to", out var to) && to.TryGetInt64(out value)) end = value;
            }
            start = Math.Clamp(start, 0, windowDurationMs); end = Math.Clamp(end, start, windowDurationMs);
            if (end > start) result.Add(new(checked(windowStartMs + start), checked(windowStartMs + end), text));
        }
        return result;
    }
}
