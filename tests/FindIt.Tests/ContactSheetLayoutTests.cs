using FindIt.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public class ContactSheetLayoutTests
{
    [TestMethod]
    public void MixedNaturalProportionsFillRowsWithoutCroppingOrLosingItems()
    {
        double[] ratios = [1.5, .66, 1, 1.78, .75, 2.4, 1.25, 1, .8, 1.4, .6, 1.9, 1.1];
        var rows = ContactSheetLayout.Arrange(ratios, 960, 165);
        Assert.AreEqual(ratios.Length, rows.Sum(row => row.Count));
        var nextIndex = 0;
        var nextOffset = 0d;
        foreach (var row in rows)
        {
            Assert.AreEqual(nextIndex, row.Start);
            Assert.AreEqual(nextOffset, row.Offset, .000001);
            Assert.IsTrue(row.Count is >= 1 and <= 8);
            Assert.AreEqual(row.Count, row.Widths.Count);
            Assert.IsTrue(row.ThumbnailHeight > 0 && double.IsFinite(row.ThumbnailHeight));
            for (var item = 0; item < row.Count; item++)
                Assert.AreEqual(ratios[row.Start + item], row.Widths[item] / row.ThumbnailHeight, .000001);
            var occupiedWidth = row.Widths.Sum() + (row.Count - 1) * 8;
            Assert.IsTrue(occupiedWidth <= 960.000001);
            if (row.Start + row.Count < ratios.Length) Assert.AreEqual(960, occupiedWidth, .000001);
            Assert.AreEqual(row.ThumbnailHeight + 26 + 8, row.Extent, .000001);
            nextIndex += row.Count;
            nextOffset += row.Extent;
        }
    }

    [TestMethod]
    public void IncompleteLastRowKeepsRequestedHeightAndNaturalWidths()
    {
        var single = ContactSheetLayout.Arrange([1.5], 1200, 165).Single();
        Assert.AreEqual(165, single.ThumbnailHeight);
        Assert.AreEqual(247.5, single.Widths.Single());
        Assert.IsTrue(single.Widths.Sum() < 1200);

        var rows = ContactSheetLayout.Arrange(Enumerable.Repeat(1d, 7).ToArray(), 900, 165);
        var last = rows[^1];
        Assert.IsTrue(last.ThumbnailHeight <= 165);
        Assert.IsTrue(last.Widths.Sum() + (last.Count - 1) * 8 < 900);
    }

    [TestMethod]
    public void AlmostCompleteRowCanShrinkToFitButNeverOverflows()
    {
        var row = ContactSheetLayout.Arrange([1d, 1d, 1d], 500, 165).Single();
        Assert.AreEqual(3, row.Count);
        Assert.IsTrue(row.ThumbnailHeight < 165);
        Assert.AreEqual(500, row.Widths.Sum() + 16, .000001);
    }

    [TestMethod]
    public void ExtremeAndUnknownDimensionsProduceUsableFiniteGeometry()
    {
        double[] ratios = [0, -4, double.NaN, double.PositiveInfinity, double.NegativeInfinity, .01, 1000];
        var rows = ContactSheetLayout.Arrange(ratios, 800, 165);
        double[] expected = [1, 1, 1, 1, 1, .55, 2.4];
        foreach (var row in rows)
        {
            Assert.IsTrue(row.Widths.All(value => value > 0 && double.IsFinite(value)));
            for (var item = 0; item < row.Count; item++)
                Assert.AreEqual(expected[row.Start + item], row.Widths[item] / row.ThumbnailHeight, .000001);
        }
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(-500d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void MissingViewportMeasurementsStillProduceUsableRows(double width)
    {
        var rows = ContactSheetLayout.Arrange([.55, 2.4, 1], width, double.NaN, double.NaN, double.NaN);
        Assert.AreEqual(3, rows.Sum(row => row.Count));
        Assert.IsTrue(rows.All(row => row.ThumbnailHeight > 0 && row.Extent > row.ThumbnailHeight));
        Assert.IsTrue(rows.All(row => row.Widths.Sum() + (row.Count - 1) * row.Gap <= 120.000001));
    }

    [TestMethod]
    public void EveryItemRemainsVisibleAfterDensityAndWindowReflow()
    {
        var ratios = Enumerable.Range(0, 650).Select(index => new[] { .6, 1, 1.5, 2.4, .8 }[index % 5]).ToArray();
        var original = ContactSheetLayout.Arrange(ratios, 1100, 132);
        var resized = ContactSheetLayout.Arrange(ratios, 620, 198);
        foreach (var row in original)
        {
            var anchor = ContactSheetLayout.FindAnchorIndex(original, row.Offset + row.Extent / 2);
            Assert.AreEqual(row.Start, anchor);
            var newOffset = ContactSheetLayout.GetOffsetForIndex(resized, anchor);
            var newAnchor = ContactSheetLayout.FindAnchorIndex(resized, newOffset);
            var newRow = resized[ContactSheetLayout.GetRowIndex(resized, anchor)];
            Assert.AreEqual(newRow.Start, newAnchor);
            Assert.IsTrue(newAnchor <= anchor && anchor < newAnchor + newRow.Count);
        }
        Assert.AreEqual(650, resized.Sum(row => row.Count));
        Assert.IsTrue(resized.All(row => row.Count <= 8));
        var viewportTop = resized[resized.Count / 2].Offset;
        var visibleRows = resized.Count(row => row.Offset < viewportTop + 720 && row.Offset + row.Extent > viewportTop);
        Assert.IsTrue(visibleRows <= 8, "Row virtualization should need only a small subset of a 650-item page.");
        Assert.IsTrue(resized.Count > 80);
    }

    [TestMethod]
    public void AnchorLookupsHandleEdgesAndExactVariableRowBoundaries()
    {
        var rows = ContactSheetLayout.Arrange(Enumerable.Repeat(1d, 40).ToArray(), 700, 165);
        Assert.AreEqual(0, ContactSheetLayout.FindAnchorIndex(rows, double.NaN));
        Assert.AreEqual(0, ContactSheetLayout.FindAnchorIndex(rows, -100));
        Assert.AreEqual(rows[^1].Start, ContactSheetLayout.FindAnchorIndex(rows, double.PositiveInfinity));
        Assert.AreEqual(0d, ContactSheetLayout.GetOffsetForIndex(rows, -1));
        Assert.AreEqual(rows[^1].Offset, ContactSheetLayout.GetOffsetForIndex(rows, int.MaxValue));
        foreach (var row in rows.Skip(1))
        {
            Assert.AreEqual(row.Start, ContactSheetLayout.FindAnchorIndex(rows, row.Offset));
            Assert.IsTrue(ContactSheetLayout.FindAnchorIndex(rows, row.Offset - .01) < row.Start);
        }
        Assert.AreEqual(0, ContactSheetLayout.FindAnchorIndex([], 100));
        Assert.AreEqual(0d, ContactSheetLayout.GetOffsetForIndex([], 100));
        Assert.AreEqual(-1, ContactSheetLayout.GetRowIndex([], 0));
    }

    [TestMethod]
    public void VerticalNavigationUsesHorizontalCentersAcrossDifferentRowShapes()
    {
        ContactSheetRow[] rows =
        [
            new(0, 3, 150, new double[] { 100, 300, 100 }, 0, 184),
            new(3, 2, 150, new double[] { 260, 248 }, 184, 184),
            new(5, 4, 150, new double[] { 90, 100, 180, 122 }, 368, 184)
        ];
        Assert.AreEqual(3, ContactSheetLayout.FindVerticalNeighbor(rows, 0, 1));
        Assert.AreEqual(3, ContactSheetLayout.FindVerticalNeighbor(rows, 1, 1));
        Assert.AreEqual(4, ContactSheetLayout.FindVerticalNeighbor(rows, 2, 1));
        Assert.AreEqual(6, ContactSheetLayout.FindVerticalNeighbor(rows, 3, 1));
        Assert.AreEqual(8, ContactSheetLayout.FindVerticalNeighbor(rows, 4, 1));
        Assert.AreEqual(0, ContactSheetLayout.FindVerticalNeighbor(rows, 3, -1));
        Assert.AreEqual(2, ContactSheetLayout.FindVerticalNeighbor(rows, 4, -1));
        Assert.AreEqual(0, ContactSheetLayout.FindVerticalNeighbor(rows, 0, -1));
        Assert.AreEqual(8, ContactSheetLayout.FindVerticalNeighbor(rows, 8, 1));
        Assert.AreEqual(4, ContactSheetLayout.FindVerticalNeighbor(rows, 4, 0));
        Assert.AreEqual(12, ContactSheetLayout.FindVerticalNeighbor(rows, 12, 1));
    }

    [TestMethod]
    public void CustomSpacingIsIncludedInBothGeometryAndKeyboardNavigation()
    {
        var rows = ContactSheetLayout.Arrange(Enumerable.Repeat(1d, 20).ToArray(), 600, 150, 12, 30);
        Assert.IsTrue(rows.All(row => row.Gap == 12));
        Assert.IsTrue(rows.All(row => Math.Abs(row.Extent - row.ThumbnailHeight - 42) < .000001));
        Assert.IsTrue(rows.Take(rows.Count - 1).All(row => Math.Abs(row.Widths.Sum() + (row.Count - 1) * 12 - 600) < .000001));
        Assert.AreEqual(rows[1].Start + rows[1].Count - 1,
            ContactSheetLayout.FindVerticalNeighbor(rows, rows[0].Start + rows[0].Count - 1, 1));
    }

    [TestMethod]
    public void EmptyContactSheetHasNoGeometryAndNoKeyboardTarget()
    {
        Assert.IsEmpty(ContactSheetLayout.Arrange([], 1000, 165));
        Assert.AreEqual(0, ContactSheetLayout.FindVerticalNeighbor([], 0, 1));
    }

    [TestMethod]
    public void EveryThumbnailSizeChangesTheColumnCountAtOrdinaryWidths()
    {
        // 970 is the default 1400 px window with the folder pane, where Large used to match Medium.
        foreach (var width in new[] { 560d, 785, 970, 1240, 1600, 2400 })
        {
            int small = ContactSheetLayout.UniformColumns(width, 0, 12), medium = ContactSheetLayout.UniformColumns(width, 1, 12), large = ContactSheetLayout.UniformColumns(width, 2, 12);
            Assert.IsTrue(small > medium && medium > large, $"{width}: {small}/{medium}/{large}");
        }
        Assert.AreEqual(4, ContactSheetLayout.UniformColumns(970, 1, 12));
        Assert.AreEqual(3, ContactSheetLayout.UniformColumns(970, 2, 12));
        // The narrowest gallery cannot step below one column.
        Assert.AreEqual(1, ContactSheetLayout.UniformColumns(120, 2, 12));
        Assert.AreEqual(1, ContactSheetLayout.UniformColumns(double.NaN, 1, 12));
    }

    [TestMethod]
    public void UniformGridFillsTheWidthAndKeepsTheShortFinalRowOnTheSameColumns()
    {
        var rows = ContactSheetLayout.Grid(10, 970, 4, 4d / 3, 12, 44);
        Assert.HasCount(3, rows);
        Assert.AreEqual(2, rows[^1].Count);
        var tileWidth = (970 - 3 * 12) / 4d;
        foreach (var row in rows)
        {
            Assert.IsTrue(row.Widths.All(width => Math.Abs(width - tileWidth) < 1e-9));
            Assert.AreEqual(tileWidth * .75, row.ThumbnailHeight, 1e-9);
            Assert.AreEqual(row.ThumbnailHeight + 44 + 12, row.Extent, 1e-9);
        }
        Assert.AreEqual(rows[1].Extent * 2, rows[2].Offset, 1e-9);
        Assert.AreEqual(8, rows[2].Start);
        Assert.IsEmpty(ContactSheetLayout.Grid(0, 970, 4, 4d / 3));
        Assert.HasCount(10, ContactSheetLayout.Grid(10, 970, 0, 4d / 3));
    }
}
