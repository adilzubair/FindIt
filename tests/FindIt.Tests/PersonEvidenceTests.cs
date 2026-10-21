using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FindIt.App;
using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class PersonEvidenceTests
{
    private string root = "";
    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "FindIt-person-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Videos"));
        File.WriteAllText(Path.Combine(root, "Videos", "montage.mp4"), "synthetic old broad intervals");
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [TestMethod]
    public async Task BroadClipOpensActualFaceSampleAndUsesItsContextThumbnailWithoutRewritingPublishedData()
    {
        await using var service = Service(); await PrepareAndNameAsync(service);
        var before = PublishedHashes();
        var response = await service.SearchAsync(new() { Query = "Sam driving", Filters = new() { ExactOnly = true } });
        var result = response.Results.Single(); var match = result.Moments.Single(); var evidence = match.PersonEvidence.Single();
        Assert.AreEqual(SegmentKind.VideoClip, match.Kind); Assert.AreEqual(0L, match.StartMs);
        Assert.AreEqual(6500L, evidence.StartMs); Assert.AreEqual("Sam", evidence.Name);
        Assert.AreEqual(evidence.Thumbnail, result.Thumbnail); Assert.AreEqual(evidence.Thumbnail, match.Thumbnail);
        Assert.AreNotEqual(evidence.Thumbnail, evidence.Portrait, "The context frame and face crop must remain separately inspectable.");
        StringAssert.Contains(match.Label, "00:00:06.500");
        Assert.AreEqual(MatchCategory.PossiblePersonAction, result.MatchCategory);
        StringAssert.Contains(response.Notice, "action, role and speaker are not verified");
        var request = ResultPreview.Request(result);
        Assert.AreEqual(match.SegmentId, request.SegmentId); Assert.AreEqual(evidence.ObservationId, request.EvidenceObservationId);
        var preview = await service.GetPreviewAsync(request);
        Assert.AreEqual(6500L, preview.StartMs, "Playback must start on the face rather than the clip's dog poster at0.");
        Assert.AreEqual(evidence.ObservationId, preview.PersonEvidence.Single().ObservationId);
        Assert.IsTrue(preview.Moments.All(m => m.PersonEvidence.Count > 0));
        CollectionAssert.AreEqual(before, PublishedHashes(), "Searching and opening person evidence must not migrate or rewrite existing generations.");
    }

    [TestMethod]
    public async Task LegacyBroadFaceIntervalCannotLeakIdentityIntoTheNextShotOrLaterSpeech()
    {
        await using var service = Service(); await PrepareAndNameAsync(service);
        foreach (string query in new[] { "Sam traffic", "Sam laterword" })
            Assert.AreEqual(0, (await service.SearchAsync(new() { Query = query, Filters = new() { ExactOnly = true } })).Results.Count, "Only the sampled timestamp6500 is identity evidence, even when an old face-bearing segment ends at7500.");
        var speech = await service.SearchAsync(new() { Query = "Sam greeting", Filters = new() { ExactOnly = true } });
        Assert.AreEqual(1, speech.Results.Count); Assert.AreEqual(SegmentKind.Transcript, speech.Results[0].Moments.Single().Kind);
        Assert.AreEqual(6500L, speech.Results[0].Moments.Single().PersonEvidence.Single().StartMs);
    }

    [TestMethod]
    public async Task DifferentPeopleInDifferentShotsDoNotBecomeACooccurrenceInsideOneBroadClip()
    {
        await using var service = Service(); var people = await PrepareAndNameAsync(service);
        var query = await service.SearchAsync(new() { Query = "Sam Alex driving", Filters = new() { ExactOnly = true } });
        Assert.AreEqual(0, query.Results.Count);
        var explicitFilters = await service.SearchAsync(new() { Query = "driving", Filters = new() { ExactOnly = true, PersonIds = people } });
        Assert.AreEqual(0, explicitFilters.Results.Count);
        var browse = await service.SearchAsync(new() { Filters = new() { PersonIds = people } });
        Assert.AreEqual(0, browse.Results.Count);
    }

    [TestMethod]
    public async Task CooccurringPeopleShareTheExactPlaybackSampleAndBothEvidenceRecords()
    {
        await using var service = Service(together: true); var people = await PrepareAndNameAsync(service);
        var response = await service.SearchAsync(new() { Query = "Sam Alex driving", Filters = new() { ExactOnly = true } });
        var result = response.Results.Single(); var match = result.Moments.Single();
        CollectionAssert.AreEquivalent(people, result.MatchedPersonIds.ToArray());
        Assert.AreEqual(2, match.PersonEvidence.Count); Assert.IsTrue(match.PersonEvidence.All(e => e.StartMs == 9500));
        var preview = await service.GetPreviewAsync(ResultPreview.Request(result));
        Assert.AreEqual(9500L, preview.StartMs); Assert.AreEqual(2, preview.PersonEvidence.Count);
    }

    [TestMethod]
    public async Task RejectedFaceEvidenceCannotBeReopenedFromAStaleSearchResult()
    {
        await using var service = Service(); var people = await PrepareAndNameAsync(service);
        var result = (await service.SearchAsync(new() { Query = "Sam driving", Filters = new() { ExactOnly = true } })).Results.Single();
        var request = ResultPreview.Request(result); long observation = request.EvidenceObservationId!.Value;
        await service.SavePersonAsync(new() { PersonId = people[0], Name = "Sam", RejectObservationIds = [observation] });
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.GetPreviewAsync(request));
        Assert.AreEqual(0, (await service.SearchAsync(new() { Query = "Sam driving", Filters = new() { ExactOnly = true } })).Results.Count);
    }

    [TestMethod]
    public async Task EvidenceFromAnotherFileCannotChangeTheSelectedPreviewTimestamp()
    {
        File.WriteAllText(Path.Combine(root, "Videos", "other.mp4"), "second synthetic timeline");
        await using var service = Service(); await PrepareAndNameAsync(service);
        var results = (await service.SearchAsync(new() { Query = "Sam driving", Filters = new() { ExactOnly = true } })).Results;
        Assert.AreEqual(2, results.Count);
        var request = ResultPreview.Request(results[0]) with { EvidenceObservationId = results[1].Moments[0].PersonEvidence[0].ObservationId };
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.GetPreviewAsync(request));
    }

    [TestMethod]
    public void SelectingAnotherMomentDoesNotReuseAnUnrelatedObservationId()
    {
        var result = new SearchResult { FileId = Guid.NewGuid(), MatchedPersonIds = [Guid.NewGuid()], Moments = [new() { SegmentId = 5, PersonEvidence = [new() { ObservationId = 20 }] }] };
        var request = ResultPreview.Request(result, 99);
        Assert.AreEqual(99L, request.SegmentId); Assert.IsNull(request.EvidenceObservationId);
        CollectionAssert.AreEquivalent(result.MatchedPersonIds.ToArray(), request.PersonIds!.ToArray());
    }

    private ArchiveService Service(bool together = false) => new(root, new ArchiveTests.TestEmbeddings("evidence-v1"), new EvidenceProcessor(together));
    private async Task<Guid[]> PrepareAndNameAsync(ArchiveService service)
    {
        await service.PrepareAsync(new() { Folders = ["Videos"] });
        var people = await service.GetPeopleAsync();
        var observationGroups = new List<(PersonRecord Person, IReadOnlyList<PersonObservation> Observations)>();
        foreach (var person in people) observationGroups.Add((person, await service.GetPersonObservationsAsync(person.Id)));
        var sam = observationGroups.Single(p => p.Observations.Any(o => o.StartMs == 6500)).Person;
        var alex = observationGroups.Single(p => p.Observations.Any(o => o.StartMs == 8000)).Person;
        await service.SavePersonAsync(new() { PersonId = sam.Id, Name = "Sam" });
        await service.SavePersonAsync(new() { PersonId = alex.Id, Name = "Alex" });
        return [sam.Id, alex.Id];
    }
    private string[] PublishedHashes() => Directory.EnumerateFiles(Path.Combine(root, "FindIt", "archive", "generations"), "*", SearchOption.AllDirectories).Order().Select(p => p + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();

    private sealed class EvidenceProcessor(bool together) : IContentProcessor
    {
        public string FaceFingerprint => "sampled-evidence-face-v1";
        public IReadOnlyList<RuntimeCapability> Capabilities => [];
        public async IAsyncEnumerable<ExtractedSegment> ExtractAsync(string absolutePath, FileKind kind, PreparationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested();
            var sam = Face(0, 7); var alex = Face(1, 8);
            yield return new() { Kind = SegmentKind.VideoFrame, StartMs = 0, EndMs = 6500, Text = "dog", ThumbnailJpeg = [1, 2, 3] };
            // Deliberately retains the old broad interval to certify read compatibility.
            yield return new() { Kind = SegmentKind.VideoFrame, StartMs = 6500, EndMs = 7500, Text = "portrait", Faces = [sam], ThumbnailJpeg = [4, 5, 6] };
            yield return new() { Kind = SegmentKind.VideoFrame, StartMs = 7000, EndMs = 7500, Text = "traffic" };
            yield return new() { Kind = SegmentKind.Transcript, StartMs = 6500, EndMs = 6750, Text = "greeting" };
            yield return new() { Kind = SegmentKind.Transcript, StartMs = 6750, EndMs = 7000, Text = "laterword" };
            yield return new() { Kind = SegmentKind.VideoFrame, StartMs = 8000, EndMs = 8001, Text = "other portrait", Faces = [alex], ThumbnailJpeg = [10, 11, 12] };
            if (together) yield return new() { Kind = SegmentKind.VideoFrame, StartMs = 9500, EndMs = 9501, Faces = [sam, alex], ThumbnailJpeg = [13, 14, 15] };
            yield return new() { Kind = SegmentKind.VideoClip, StartMs = 0, EndMs = 12000, Text = "driving" };
        }
        private static DetectedFace Face(int dimension, byte portrait) { var vector = new float[128]; vector[dimension] = 1; return new(vector, 0, 0, 1, 1, .99, [portrait]); }
        public Task<IReadOnlyList<DetectedFace>> DetectReferenceFacesAsync(string absolutePath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DetectedFace>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
