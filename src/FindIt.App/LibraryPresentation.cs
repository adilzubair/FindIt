using FindIt.Shared;

namespace FindIt.App;

internal readonly record struct GalleryMetrics(int Columns, double TileWidth, double ThumbnailHeight, double RowExtent);

internal static class ResultPreview
{
    public static PreviewRequest Request(SearchResult result, long? segmentId = null)
    {
        var moment = segmentId is long selected ? result.Moments.FirstOrDefault(m => m.SegmentId == selected) : result.Moments.FirstOrDefault();
        return new(result.FileId, segmentId ?? moment?.SegmentId, moment?.PersonEvidence.FirstOrDefault()?.ObservationId, result.MatchedPersonIds);
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
