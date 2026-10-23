using System.ComponentModel;
using System.Runtime.CompilerServices;
using FindIt.Shared;

namespace FindIt.App;

internal readonly record struct GalleryMetrics(int Columns, double TileWidth, double ThumbnailHeight, double RowExtent);

internal static class ResultPreview
{
    public static PreviewRequest Request(SearchResult result, long? segmentId = null)
    {
        // Without a chosen moment, open at the strongest match rather than the earliest.
        var moment = segmentId is long selected ? result.Moments.FirstOrDefault(m => m.SegmentId == selected) : result.Moments.MinBy(m => m.Rank);
        return new(result.FileId, segmentId ?? moment?.SegmentId, moment?.PersonEvidence.FirstOrDefault()?.ObservationId, result.MatchedPersonIds);
    }
}

/// <summary>
/// Decoded thumbnails cost several times their pixel bytes once WPF renders them, so a tile
/// decodes only the pixels its cropped (UniformToFill) box displays.
/// </summary>
internal static class ThumbnailSizing
{
    internal const int Step = 64;
    internal const int DefaultMaximumWidth = 440;

    /// <summary>Smallest decode width that still covers a box of device pixels without upscaling.</summary>
    public static int CoverWidth(int sourceWidth, int sourceHeight, double boxWidth, double boxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return DefaultMaximumWidth;
        if (!double.IsFinite(boxWidth) || !double.IsFinite(boxHeight) || boxWidth <= 0 || boxHeight <= 0) return Math.Min(sourceWidth, DefaultMaximumWidth);
        var needed = Math.Max(boxWidth, boxHeight * sourceWidth / sourceHeight);
        // Whole steps let small reflows reuse the cached image instead of decoding again.
        var stepped = (int)Math.Min(int.MaxValue - Step, Math.Ceiling(needed / Step) * Step);
        return Math.Clamp(stepped, 1, sourceWidth);
    }
}

internal static class GalleryLayout
{
    internal const double Gap = 12;
    internal const double DefaultTileWidth = 220;

    public static GalleryMetrics Calculate(double availableWidth, double targetTileWidth = DefaultTileWidth)
    {
        var width = double.IsFinite(availableWidth) ? Math.Max(120, availableWidth) : 120;
        var target = double.IsFinite(targetTileWidth) && targetTileWidth > 0 ? Math.Max(120, targetTileWidth) : DefaultTileWidth;
        var columns = (int)Math.Clamp(Math.Floor((width + Gap) / (target + Gap)), 1, int.MaxValue);
        var tileWidth = Math.Floor((width - (columns - 1) * Gap) / columns);
        var thumbnailHeight = tileWidth * 3 / 4;
        // Two metadata lines occupy 48 px; every row includes its 12 px trailing gap.
        return new(columns, tileWidth, thumbnailHeight, thumbnailHeight + 60);
    }

    public static int GetAnchorIndex(double offset, int previousColumns, double previousRowExtent)
    {
        if (!double.IsFinite(offset) || offset <= 0 || !double.IsFinite(previousRowExtent) || previousRowExtent <= 0) return 0;
        var row = Math.Floor(offset / previousRowExtent);
        return (int)Math.Min(int.MaxValue, row * Math.Max(1, previousColumns));
    }

    public static double GetAnchorOffset(int index, int newColumns, double newRowExtent)
    {
        if (index <= 0 || !double.IsFinite(newRowExtent) || newRowExtent <= 0) return 0;
        return (index / Math.Max(1, newColumns)) * newRowExtent;
    }
}

internal sealed record LibraryFilterState(
    int KindIndex = 0,
    int DateIndex = 0,
    string? Folder = null,
    Guid? PersonId = null,
    bool ExactOnly = false,
    DateTime? After = null,
    DateTime? Before = null)
{
    public SearchFilters ToSearchFilters(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        DateTimeOffset? after = DateIndex switch
        {
            1 => now.AddDays(-30).ToUniversalTime(),
            2 => LocalMidnightUtc(new DateTime(localNow.Year, 1, 1), zone),
            3 when After is DateTime start => LocalMidnightUtc(start, zone),
            _ => null
        };
        DateTimeOffset? before = DateIndex == 3 && Before is DateTime end
            ? end.Date == DateTime.MaxValue.Date ? DateTimeOffset.MaxValue : LocalMidnightUtc(end.Date.AddDays(1), zone)
            : null;
        return new()
        {
            Kind = KindIndex switch { 1 => FileKind.Image, 2 => FileKind.Video, 3 => FileKind.Document, 4 => FileKind.Audio, _ => null },
            Folder = Folder,
            PersonIds = PersonId is Guid person && person != Guid.Empty ? [person] : [],
            ExactOnly = ExactOnly,
            AfterUtc = after,
            BeforeUtc = before
        };
    }

    public string? ValidateDateRange() => DateIndex == 3 && After is DateTime start && Before is DateTime end && start.Date > end.Date
        ? "The end date must be on or after the start date."
        : null;

    private static DateTimeOffset LocalMidnightUtc(DateTime value, TimeZoneInfo zone)
    {
        var localDate = DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(localDate, zone.GetUtcOffset(localDate)).ToUniversalTime();
    }
}

/// <summary>Media positions as people read them: 0:06, 4:05, 1:02:10.</summary>
public static class ClockFormat
{
    public static string Format(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";
    }
}

/// <summary>
/// One matching moment as a viewer sees it. A scene window, the frames sampled inside it and the
/// speech over it are separate matches in the catalog but a single place in the recording.
/// </summary>
public sealed class MomentGroup : INotifyPropertyChanged
{
    // Matches this close together read as one moment.
    private const long JoinMs = 1500;
    /// <summary>The match opened for this moment: one with face evidence first, then the strongest.</summary>
    public required SearchMoment Lead { get; init; }
    public long? StartMs { get; init; }
    public long? EndMs { get; init; }
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public BlobReference? Thumbnail { get; init; }
    public bool IsBest { get; init; }
    public bool HasPerson => Lead.PersonEvidence.Count > 0;
    public string TimeLabel => StartMs is not long start ? "" : EndMs is long end && end - start >= JoinMs ? $"{ClockFormat.Format(start)}–{ClockFormat.Format(end)}" : ClockFormat.Format(start);
    public bool HasTime => StartMs is not null;
    public bool HasText => Text.Length > 0;
    public string AccessibleName => $"{(IsActive ? "Playing, " : "")}{(IsBest ? "Best match, " : "")}{Title}{(TimeLabel.Length > 0 ? " at " + TimeLabel.Replace("–", " to ") : "")}{(Text.Length > 0 ? ". " + Text : "")}";
    private bool _isActive;
    public bool IsActive { get => _isActive; set { if (_isActive == value) return; _isActive = value; Changed(); Changed(nameof(AccessibleName)); } }
    public bool Contains(long ms) => StartMs is long start && ms >= start - 250 && ms <= (EndMs ?? start) + 250;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    public static IReadOnlyList<MomentGroup> Build(IReadOnlyList<SearchMoment> moments)
    {
        // Pages and photos have no time, so each stays its own entry in the order given.
        var sets = moments.Where(m => Time(m) is null).Select(m => new List<SearchMoment> { m }).ToList();
        List<SearchMoment>? current = null; long currentEnd = 0;
        foreach (var moment in moments.Where(m => Time(m) is not null).OrderBy(m => Time(m)))
        {
            var start = Time(moment)!.Value;
            if (current is null || start > currentEnd + JoinMs) { sets.Add(current = []); currentEnd = start; }
            current.Add(moment); currentEnd = Math.Max(currentEnd, Math.Max(start, moment.EndMs ?? start));
        }
        // Marking the strongest only helps when there is a choice.
        return sets.Select(set => From(set, sets.Count > 1)).ToArray();
    }
    private static long? Time(SearchMoment moment) => moment.PersonEvidence.FirstOrDefault()?.StartMs ?? moment.StartMs;
    private static MomentGroup From(IReadOnlyList<SearchMoment> members, bool markBest)
    {
        var lead = members.OrderBy(m => m.PersonEvidence.Count > 0 ? 0 : 1).ThenBy(m => m.Rank).First();
        var start = members.Select(Time).Min();
        long? end = start is null ? null : members.Max(m => Math.Max(Time(m) ?? 0, m.EndMs ?? Time(m) ?? 0));
        var names = members.SelectMany(m => m.PersonEvidence).Select(e => e.Name).Distinct().Take(2);
        var kinds = members.Select(m => m.Kind switch
        {
            SegmentKind.VideoClip or SegmentKind.VideoFrame => "Scene", SegmentKind.Transcript => "Speech", SegmentKind.Audio => "Sound",
            SegmentKind.DocumentPage when m.Page is int page => $"Page {page}", SegmentKind.Image => "Image", _ => "Text"
        }).Distinct();
        var speech = members.Where(m => m.Kind is SegmentKind.Transcript or SegmentKind.DocumentPage or SegmentKind.Text && !string.IsNullOrWhiteSpace(m.Text))
            .OrderBy(m => m.StartMs ?? 0).Select(m => string.Join(' ', m.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Distinct();
        var text = string.Join(" … ", speech);
        return new()
        {
            Lead = lead, StartMs = start, EndMs = end, Title = string.Join(" · ", names.Concat(kinds)),
            Text = text.Length > 160 ? text[..159] + "…" : text,
            Thumbnail = lead.PersonEvidence.FirstOrDefault()?.Thumbnail ?? lead.Thumbnail ?? members.OrderBy(m => Time(m) ?? 0).Select(m => m.Thumbnail).FirstOrDefault(t => t is not null),
            IsBest = markBest && members.Any(m => m.Rank == 0)
        };
    }
}
