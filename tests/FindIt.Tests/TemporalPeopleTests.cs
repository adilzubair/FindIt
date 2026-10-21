using System.Runtime.CompilerServices;
using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class TemporalPeopleTests
{
    private string root = "";
    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "FindIt-temporal-people", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Videos")); File.WriteAllText(Path.Combine(root, "Videos", "moments.mp4"), "synthetic timeline");
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [TestMethod]
    public async Task NamedFaceRequiresActualOverlapAndCannotLeakAcrossAdjacentMomentBoundaries()
    {
        await using var service = new ArchiveService(root, new ArchiveTests.TestEmbeddings("temporal-people-v1"), new TimelineProcessor());
        await service.PrepareAsync(new() { Folders = ["Videos"] });
        var unnamed = (await service.GetPeopleAsync()).Single();
        var named = await service.SavePersonAsync(new() { PersonId = unnamed.Id, Name = "Sam" });

        foreach (string activity in new[] { "cycling", "dog", "driving" })
        {
            var response = await service.SearchAsync(new() { Query = "Sam " + activity, Filters = new() { ExactOnly = true } });
            Assert.AreEqual(0, response.Results.Count, $"A face sampled at6500 must not be joined to the distinct {activity} moment.");
        }
        var direct = await service.SearchAsync(new() { Query = "Sam waving", Filters = new() { ExactOnly = true } });
        Assert.AreEqual(1, direct.Results.Count); Assert.AreEqual(6500L, direct.Results[0].Moments.Single().StartMs);
        Assert.AreEqual(MatchCategory.PossiblePersonAction, direct.Results[0].MatchCategory);

        var overlapping = await service.SearchAsync(new() { Query = "Sam greeting", Filters = new() { ExactOnly = true } });
        Assert.AreEqual(1, overlapping.Results.Count); var moment = overlapping.Results[0].Moments.Single();
        Assert.AreEqual(SegmentKind.Transcript, moment.Kind); Assert.AreEqual(6500L, moment.StartMs); Assert.AreEqual(6900L, moment.EndMs);

        var explicitFilter = await service.SearchAsync(new() { Query = "dog", Filters = new() { ExactOnly = true, PersonIds = [named.Id] } });
        Assert.AreEqual(0, explicitFilter.Results.Count, "Explicit person filters must use the same temporal constraint as names in queries.");
    }

    [TestMethod]
    public async Task MissingEndTimeIsAnInstantRatherThanAnImplicitProximityWindow()
    {
        await using var service = new ArchiveService(root, new ArchiveTests.TestEmbeddings("temporal-point-v1"), new TimelineProcessor { PointFace = true });
        await service.PrepareAsync(new() { Folders = ["Videos"] });
        var person = (await service.GetPeopleAsync()).Single(); await service.SavePersonAsync(new() { PersonId = person.Id, Name = "Sam" });
        var sameInstant = await service.SearchAsync(new() { Query = "Sam welcome", Filters = new() { ExactOnly = true } });
        Assert.AreEqual(1, sameInstant.Results.Count, "A containing interval should include face evidence sampled at its start.");
        var later = await service.SearchAsync(new() { Query = "Sam farewell", Filters = new() { ExactOnly = true } });
        Assert.AreEqual(0, later.Results.Count, "A face without an end time must not extend into later moments.");
    }

    private sealed class TimelineProcessor : IContentProcessor
    {
        public bool PointFace { get; init; }
        public string FaceFingerprint => "temporal-face-v1";
        public IReadOnlyList<RuntimeCapability> Capabilities => [];
        public async IAsyncEnumerable<ExtractedSegment> ExtractAsync(string absolutePath, FileKind kind, PreparationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
            var vector = new float[128]; vector[0] = 1; var face = new DetectedFace(vector, 0, 0, 1, 1, .99);
            if (PointFace)
            {
                yield return new() { Kind = SegmentKind.VideoFrame, Text = "waving", StartMs = 6500, Faces = [face] };
                yield return new() { Kind = SegmentKind.Transcript, Text = "welcome", StartMs = 6500, EndMs = 6600 };
                yield return new() { Kind = SegmentKind.Transcript, Text = "farewell", StartMs = 6501, EndMs = 6600 };
                yield break;
            }
            yield return new() { Kind = SegmentKind.VideoFrame, Text = "cycling", StartMs = 5000, EndMs = 6000 };
            yield return new() { Kind = SegmentKind.VideoFrame, Text = "dog", StartMs = 6000, EndMs = 6500 };
            yield return new() { Kind = SegmentKind.VideoFrame, Text = "waving", StartMs = 6500, EndMs = 7000, Faces = [face] };
            yield return new() { Kind = SegmentKind.Transcript, Text = "greeting", StartMs = 6500, EndMs = 6900 };
            yield return new() { Kind = SegmentKind.VideoFrame, Text = "driving", StartMs = 7000, EndMs = 7500 };
        }
        public Task<IReadOnlyList<DetectedFace>> DetectReferenceFacesAsync(string absolutePath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DetectedFace>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
