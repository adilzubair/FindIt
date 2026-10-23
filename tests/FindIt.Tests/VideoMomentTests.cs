using System.Runtime.CompilerServices;
using FindIt.App;
using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class VideoMomentTests
{
    private string root = "";
    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "FindIt-video-moments", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Videos")); File.WriteAllText(Path.Combine(root, "Videos", "harbour.mp4"), "synthetic timeline");
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [TestMethod]
    public async Task SpeechAndSceneMatchesShowTheFrameSampledNearestThem()
    {
        await using var service = new ArchiveService(root, new ArchiveTests.TestEmbeddings("video-moments-v1"), new SampledVideo());
        await service.PrepareAsync(new() { Folders = ["Videos"] });
        var speech = (await service.SearchAsync(new() { Query = "lighthouse", Filters = new() { ExactOnly = true } })).Results.Single();
        var preview = await service.GetPreviewAsync(new(speech.FileId, speech.Moments[0].SegmentId));

        CollectionAssert.AreEqual(new long[] { 0, 1000, 2000, 3000 }, preview.Frames.Select(f => f.StartMs).ToArray());
        var frameAt = preview.Frames.ToDictionary(f => f.StartMs, f => f.Thumbnail);
        Assert.AreEqual(frameAt[2000], speech.Thumbnail, "Speech at 1.9 s has no picture of its own; the frame sampled at 2 s is nearest.");
        Assert.AreEqual(frameAt[2000], speech.Moments.Single().Thumbnail);

        var scene = (await service.SearchAsync(new() { Query = "pier", Filters = new() { ExactOnly = true } })).Results.Single();
        Assert.AreEqual(frameAt[3000], scene.Thumbnail, "A scene window shows the frame at its start.");

        var both = (await service.SearchAsync(new() { Query = "beacon", Filters = new() { ExactOnly = true } })).Results.Single();
        CollectionAssert.AreEquivalent(new[] { 0, 1 }, both.Moments.Select(m => m.Rank).ToArray(), "Each match keeps its strength within the file.");
        Assert.IsTrue(both.Moments.All(m => m.Thumbnail is not null));
        Assert.AreEqual(both.Moments.Single(m => m.Rank == 0).SegmentId, ResultPreview.Request(both).SegmentId, "The preview opens at the strongest match, not the earliest.");
    }

    [TestMethod]
    public void OverlappingMatchesReadAsOneMomentAndTheStrongestIsMarked()
    {
        var thumbnail = new BlobReference("FindIt/archive/packs/t.pack", 0, 10);
        SearchMoment At(SegmentKind kind, long start, long end, int rank, string text = "") => new() { SegmentId = rank + 1, Kind = kind, StartMs = start, EndMs = end, Rank = rank, Text = text, Thumbnail = kind == SegmentKind.VideoFrame ? thumbnail : null };
        var groups = MomentGroup.Build(
        [
            At(SegmentKind.VideoFrame, 0, 1, 3), At(SegmentKind.VideoClip, 0, 2002, 1),
            At(SegmentKind.Transcript, 190, 3190, 0, "they  watch the\nlighthouse"), At(SegmentKind.VideoClip, 3003, 9009, 2),
            At(SegmentKind.VideoFrame, 20000, 20001, 4)
        ]);

        Assert.AreEqual(2, groups.Count, "A scene window, its frames and the speech over it are one moment; a later scene is another.");
        Assert.AreEqual(0L, groups[0].StartMs); Assert.AreEqual(9009L, groups[0].EndMs); Assert.AreEqual("0:00–0:09", groups[0].TimeLabel);
        Assert.AreEqual("Scene · Speech", groups[0].Title); Assert.AreEqual("they watch the lighthouse", groups[0].Text);
        Assert.AreEqual(1L, groups[0].Lead.SegmentId, "The strongest match leads the moment.");
        Assert.AreEqual(thumbnail, groups[0].Thumbnail);
        Assert.IsTrue(groups[0].IsBest); Assert.IsFalse(groups[1].IsBest);
        Assert.AreEqual("0:20", groups[1].TimeLabel);
        Assert.IsTrue(groups[1].Contains(20100)); Assert.IsFalse(groups[0].Contains(15000));

        var single = MomentGroup.Build([At(SegmentKind.VideoClip, 0, 8000, 0)]);
        Assert.IsFalse(single.Single().IsBest, "With one moment there is nothing to compare.");
        var pages = MomentGroup.Build([new() { SegmentId = 1, Kind = SegmentKind.DocumentPage, Page = 3 }, new() { SegmentId = 2, Kind = SegmentKind.DocumentPage, Page = 1 }]);
        CollectionAssert.AreEqual(new[] { "Page 3", "Page 1" }, pages.Select(p => p.Title).ToArray());
        Assert.IsFalse(pages[0].HasTime);
    }

    private sealed class SampledVideo : IContentProcessor
    {
        public string FaceFingerprint => "video-moments-face-v1";
        public IReadOnlyList<RuntimeCapability> Capabilities => [];
        public async IAsyncEnumerable<ExtractedSegment> ExtractAsync(string absolutePath, FileKind kind, PreparationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
            for (long at = 0; at <= 3000; at += 1000)
                yield return new() { Kind = SegmentKind.VideoFrame, StartMs = at, EndMs = at + 1, ThumbnailJpeg = [(byte)(at / 1000 + 1), 2, 3] };
            yield return new() { Kind = SegmentKind.Transcript, Text = "the beacon lighthouse", StartMs = 1900, EndMs = 2600 };
            yield return new() { Kind = SegmentKind.VideoClip, Text = "beacon pier", StartMs = 3000, EndMs = 5000 };
        }
        public Task<IReadOnlyList<DetectedFace>> DetectReferenceFacesAsync(string absolutePath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DetectedFace>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
