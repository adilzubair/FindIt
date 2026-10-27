using FindIt.App;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public class LibraryPresentationTests
{
    [TestMethod]
    [DataRow(176d, 5, 195d)]
    [DataRow(220d, 4, 247d)]
    [DataRow(264d, 3, 333d)]
    public void GalleryDensityFitsWholeTilesAndFourByThreePreviews(double target, int columns, double tileWidth)
    {
        var metrics = GalleryLayout.Calculate(1024, target);
        Assert.AreEqual(columns, metrics.Columns);
        Assert.AreEqual(tileWidth, metrics.TileWidth);
        Assert.AreEqual(tileWidth * .75, metrics.ThumbnailHeight);
        Assert.AreEqual(metrics.ThumbnailHeight + 60, metrics.RowExtent);
        Assert.IsTrue(metrics.Columns * metrics.TileWidth + (metrics.Columns - 1) * GalleryLayout.Gap <= 1024);
    }

    [TestMethod]
    // A 4:3 tile box of 248×186 device pixels.
    [DataRow(640, 360, 384)] // wide video frame: the height decides (186 × 16/9 = 331 → 384)
    [DataRow(640, 480, 256)] // same shape as the box: its width (248 → 256)
    [DataRow(480, 640, 256)] // portrait photo: the width decides
    [DataRow(200, 150, 200)] // never upscale a small stored thumbnail
    public void ThumbnailDecodeCoversTheCroppedTileWithoutExtraPixels(int sourceWidth, int sourceHeight, int expected)
    {
        Assert.AreEqual(expected, ThumbnailSizing.CoverWidth(sourceWidth, sourceHeight, 248, 186));
    }

    [TestMethod]
    public void ThumbnailDecodeFollowsDisplayScaleInWholeSteps()
    {
        Assert.AreEqual(384, ThumbnailSizing.CoverWidth(1024, 768, 248 * 1.5, 186 * 1.5)); // 372 at 150% scale
        Assert.AreEqual(ThumbnailSizing.CoverWidth(1024, 768, 250, 187.5), ThumbnailSizing.CoverWidth(1024, 768, 255, 191.25), "Small reflows reuse one decode.");
    }

    [TestMethod]
    [DataRow(0d, 100d)]
    [DataRow(double.NaN, 100d)]
    [DataRow(100d, double.PositiveInfinity)]
    public void UnmeasuredThumbnailBoxKeepsThePreviewDecodeLimit(double width, double height)
    {
        Assert.AreEqual(ThumbnailSizing.DefaultMaximumWidth, ThumbnailSizing.CoverWidth(1024, 768, width, height));
        Assert.AreEqual(300, ThumbnailSizing.CoverWidth(300, 200, width, height));
        Assert.AreEqual(ThumbnailSizing.DefaultMaximumWidth, ThumbnailSizing.CoverWidth(0, 0, 248, 186));
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(-500d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void UnmeasuredOrNarrowGalleryStillHasOneUsableTile(double width)
    {
        Assert.AreEqual(new GalleryMetrics(1, 120, 90, 150), GalleryLayout.Calculate(width));
    }

    [TestMethod]
    public void GalleryReflowsAtTheExactColumnThreshold()
    {
        Assert.AreEqual(1, GalleryLayout.Calculate(451, 220).Columns);
        Assert.AreEqual(2, GalleryLayout.Calculate(452, 220).Columns);
        Assert.AreEqual(220d, GalleryLayout.Calculate(452, 220).TileWidth);
        Assert.AreEqual(GalleryLayout.Calculate(1024, 220), GalleryLayout.Calculate(1024, double.NaN));
    }

    [TestMethod]
    public void ResizeKeepsThePreviousFirstVisibleItemInTheVisibleRow()
    {
        var previous = GalleryLayout.Calculate(1024, 220);
        var anchor = GalleryLayout.GetAnchorIndex(previous.RowExtent * 7 + 20, previous.Columns, previous.RowExtent);
        Assert.AreEqual(28, anchor);
        var current = GalleryLayout.Calculate(700, 264);
        var offset = GalleryLayout.GetAnchorOffset(anchor, current.Columns, current.RowExtent);
        Assert.AreEqual(anchor, GalleryLayout.GetAnchorIndex(offset, current.Columns, current.RowExtent));
        var threeColumns = GalleryLayout.Calculate(850, 264);
        var newFirst = GalleryLayout.GetAnchorIndex(GalleryLayout.GetAnchorOffset(anchor, threeColumns.Columns, threeColumns.RowExtent), threeColumns.Columns, threeColumns.RowExtent);
        Assert.IsTrue(newFirst <= anchor && anchor < newFirst + threeColumns.Columns);
    }

    [TestMethod]
    public void InvalidAnchorMeasurementsStartAtTheTop()
    {
        Assert.AreEqual(0, GalleryLayout.GetAnchorIndex(double.NaN, 4, 225));
        Assert.AreEqual(0, GalleryLayout.GetAnchorIndex(-1, 4, 225));
        Assert.AreEqual(0, GalleryLayout.GetAnchorIndex(200, 4, 0));
        Assert.AreEqual(0d, GalleryLayout.GetAnchorOffset(20, 4, double.NaN));
        Assert.AreEqual(0d, GalleryLayout.GetAnchorOffset(-1, 4, 225));
    }

    [TestMethod]
    [DataRow(0, null)]
    [DataRow(1, FileKind.Image)]
    [DataRow(2, FileKind.Video)]
    [DataRow(3, FileKind.Document)]
    [DataRow(4, FileKind.Audio)]
    [DataRow(99, null)]
    public void TypeTabsMapToExistingFileKinds(int index, FileKind? expected)
    {
        Assert.AreEqual(expected, new LibraryFilterState(KindIndex: index).ToSearchFilters(DateTimeOffset.UtcNow, TimeZoneInfo.Utc).Kind);
    }

    [TestMethod]
    public void FilterMappingPreservesFolderAndPersonIdentity()
    {
        var person = Guid.NewGuid();
        var state = new LibraryFilterState(Folder: "Photos/Family", PersonId: person, ExactOnly: true);
        var mapped = state.ToSearchFilters(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        Assert.AreEqual("Photos/Family", mapped.Folder);
        Assert.AreEqual(person, mapped.PersonIds.Single());
        Assert.IsTrue(mapped.ExactOnly);
        Assert.IsEmpty(new LibraryFilterState(PersonId: Guid.Empty).ToSearchFilters(DateTimeOffset.UtcNow, TimeZoneInfo.Utc).PersonIds);
    }

    [TestMethod]
    public void DateRangeIncludesTheWholeLocalEndDay()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UI-test-Dubai", TimeSpan.FromHours(4), "Dubai", "Dubai");
        var state = new LibraryFilterState(DateIndex: 3, After: new DateTime(2026, 10, 20), Before: new DateTime(2026, 10, 21));
        var mapped = state.ToSearchFilters(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero), zone);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 19, 20, 0, 0, TimeSpan.Zero), mapped.AfterUtc);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.Zero), mapped.BeforeUtc);
        Assert.IsNull(state.ValidateDateRange());
    }

    [TestMethod]
    public void InclusiveEndUsesTheNextDaysDaylightSavingOffset()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var state = new LibraryFilterState(DateIndex: 3, After: new DateTime(2026, 3, 8), Before: new DateTime(2026, 3, 8));
        var mapped = state.ToSearchFilters(DateTimeOffset.UtcNow, zone);
        Assert.AreEqual(TimeSpan.FromHours(23), mapped.BeforeUtc - mapped.AfterUtc);
    }

    [TestMethod]
    public void ThisYearUsesTheSelectedTimeZonesYear()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UI-test-year", TimeSpan.FromHours(4), "Dubai", "Dubai");
        var now = new DateTimeOffset(2025, 12, 31, 22, 0, 0, TimeSpan.Zero);
        var mapped = new LibraryFilterState(DateIndex: 2).ToSearchFilters(now, zone);
        Assert.AreEqual(new DateTimeOffset(2025, 12, 31, 20, 0, 0, TimeSpan.Zero), mapped.AfterUtc);
        Assert.IsNull(mapped.BeforeUtc);
        Assert.AreEqual(now.AddDays(-30), new LibraryFilterState(DateIndex: 1).ToSearchFilters(now, zone).AfterUtc);
    }

    [TestMethod]
    public void CustomDateValuesAreInactiveOutsideCustomRangeAndReversedRangesHaveAUsefulMessage()
    {
        var state = new LibraryFilterState(After: new DateTime(2026, 10, 22), Before: new DateTime(2026, 10, 21));
        var mapped = state.ToSearchFilters(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        Assert.IsNull(mapped.AfterUtc);
        Assert.IsNull(mapped.BeforeUtc);
        Assert.IsNull(state.ValidateDateRange());
        Assert.AreEqual("The end date must be on or after the start date.", (state with { DateIndex = 3 }).ValidateDateRange());
    }

    [TestMethod]
    public void PersonSuggestionsShowOneFacePerMomentInQualityOrder()
    {
        Guid video = Guid.NewGuid(), other = Guid.NewGuid(), photo = Guid.NewGuid();
        PersonObservation Face(long id, Guid file, long? at) => new() { Id = id, FileId = file, StartMs = at };
        // Faces sampled twice a second from one shot, a later scene, another video and a photo with two faces.
        var ordered = new[]
        {
            Face(1, video, 10_000), Face(2, video, 10_500), Face(3, video, 11_000), Face(4, other, 10_000),
            Face(5, video, 39_999), Face(6, video, 40_000), Face(7, photo, null), Face(8, photo, null), Face(9, video, 9_000),
        };
        var kept = PersonSuggestions.DistinctMoments(ordered).Select(face => face.Id).ToArray();
        CollectionAssert.AreEqual(new long[] { 1, 4, 6, 7 }, kept);
    }
}
