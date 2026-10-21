namespace FindIt.App;

internal sealed record ContactSheetRow(
    int Start,
    int Count,
    double ThumbnailHeight,
    IReadOnlyList<double> Widths,
    double Offset,
    double Extent)
{
    internal double Gap { get; init; } = 8;
}

/// <summary>Natural-aspect rows whose geometry is known before thumbnails are decoded.</summary>
internal static class ContactSheetLayout
{
    internal const int MaximumItemsPerRow = 8;
    internal const double DefaultTargetHeight = 165;

    public static IReadOnlyList<ContactSheetRow> Arrange(
        IReadOnlyList<double> aspectRatios,
        double availableWidth,
        double targetHeight,
        double gap = 8,
        double captionHeight = 26)
    {
        ArgumentNullException.ThrowIfNull(aspectRatios);
        if (aspectRatios.Count == 0) return [];

        var width = double.IsFinite(availableWidth) ? Math.Max(120, availableWidth) : 120;
        var target = double.IsFinite(targetHeight) && targetHeight > 0 ? Math.Clamp(targetHeight, 72, 360) : DefaultTargetHeight;
        // Even an invalid/custom spacing choice must leave useful image area in a narrow window.
        var spacing = double.IsFinite(gap) && gap >= 0 ? Math.Min(gap, width / (MaximumItemsPerRow + 1)) : Math.Min(8, width / (MaximumItemsPerRow + 1));
        var caption = double.IsFinite(captionHeight) && captionHeight >= 0 ? Math.Min(captionHeight, 120) : 26;
        var rows = new List<ContactSheetRow>();
        var offset = 0d;
        var start = 0;
        while (start < aspectRatios.Count)
        {
            var count = 0;
            var sum = 0d;
            var height = 0d;
            while (count < MaximumItemsPerRow && start + count < aspectRatios.Count)
            {
                var nextSum = sum + Ratio(aspectRatios[start + count]);
                var nextHeight = (width - count * spacing) / nextSum;
                // Select the closer of the two rows bracketing the requested height.
                if (count > 0 && Math.Abs(nextHeight - target) > Math.Abs(height - target)) break;
                sum = nextSum;
                height = nextHeight;
                count++;
                if (height <= target) break;
            }

            var finalRow = start + count == aspectRatios.Count;
            if (finalRow) height = Math.Min(height, target);
            var widths = new double[count];
            var used = 0d;
            for (var index = 0; index < count; index++)
            {
                widths[index] = Ratio(aspectRatios[start + index]) * height;
                used += widths[index];
            }
            // Absorb floating-point drift in the last image of every justified row.
            if (!finalRow || height < target)
                widths[^1] += width - (used + spacing * (count - 1));
            var extent = height + caption + spacing;
            rows.Add(new(start, count, height, widths, offset, extent) { Gap = spacing });
            offset += extent;
            start += count;
        }
        return rows;
    }

    public static int FindAnchorIndex(IReadOnlyList<ContactSheetRow> rows, double offset)
    {
        if (rows.Count == 0) return 0;
        if (double.IsNaN(offset) || offset <= 0) return rows[0].Start;
        var low = 0;
        var high = rows.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (rows[middle].Offset <= offset) low = middle;
            else high = middle - 1;
        }
        return rows[low].Start;
    }

    public static double GetOffsetForIndex(IReadOnlyList<ContactSheetRow> rows, int index)
    {
        var row = GetRowIndex(rows, index);
        return row < 0 ? 0 : rows[row].Offset;
    }

    public static int GetRowIndex(IReadOnlyList<ContactSheetRow> rows, int index)
    {
        if (rows.Count == 0) return -1;
        var low = 0;
        var high = rows.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (rows[middle].Start <= index) low = middle;
            else high = middle - 1;
        }
        return low;
    }

    public static int FindVerticalNeighbor(IReadOnlyList<ContactSheetRow> rows, int currentIndex, int direction)
    {
        var rowIndex = GetRowIndex(rows, currentIndex);
        if (rowIndex < 0 || direction == 0) return currentIndex;
        var row = rows[rowIndex];
        var itemIndex = currentIndex - row.Start;
        if (itemIndex < 0 || itemIndex >= row.Count) return currentIndex;
        var nextRowIndex = rowIndex + Math.Sign(direction);
        if (nextRowIndex < 0 || nextRowIndex >= rows.Count) return currentIndex;

        var center = Center(row, itemIndex);
        var next = rows[nextRowIndex];
        var closest = next.Start;
        var distance = double.PositiveInfinity;
        var left = 0d;
        for (var index = 0; index < next.Count; index++)
        {
            var candidateDistance = Math.Abs(left + next.Widths[index] / 2 - center);
            if (candidateDistance < distance)
            {
                closest = next.Start + index;
                distance = candidateDistance;
            }
            left += next.Widths[index] + next.Gap;
        }
        return closest;
    }

    private static double Center(ContactSheetRow row, int itemIndex)
    {
        var center = row.Widths[itemIndex] / 2;
        for (var index = 0; index < itemIndex; index++) center += row.Widths[index] + row.Gap;
        return center;
    }

    private static double Ratio(double value) => double.IsFinite(value) && value > 0 ? Math.Clamp(value, .55, 2.4) : 1;
}
