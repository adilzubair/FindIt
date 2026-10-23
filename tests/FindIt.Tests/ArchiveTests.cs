using System.Runtime.CompilerServices;
using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public class ArchiveTests
{
    private string _root = "";
    [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "FindIt-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(_root, "Documents")); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private ArchiveService Service(TestProcessor? processor = null, string fingerprint = "test-512-v1") => new(_root, new TestEmbeddings(fingerprint), processor ?? new());
    private Task Write(string path, string text) => File.WriteAllTextAsync(Path.Combine(_root, "Documents", path), text);

    [TestMethod]
    public async Task UnpreparedReaderDoesNotCreateArchiveOrModifyOriginals()
    {
        await Write("invoice.txt", "Invoice 2026-001");
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        await using (var service = Service()) { Assert.AreEqual(0, (await service.GetStatusAsync()).FileCount); Assert.AreEqual(0, (await service.SearchAsync(new SearchRequest { Query = "invoice" })).Results.Count); }
        CollectionAssert.AreEquivalent(before, Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task PreparationFoldersAndRecentSearchesTravelWithTheDrive()
    {
        await Write("lease.txt", "The lease permits two cats.");
        await using (var unprepared = Service())
        {
            await unprepared.SaveRecentQueriesAsync(["cats"]);
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "FindIt")), "Saving searches must not create an archive on an unprepared drive.");
        }
        await using (var service = Service())
        {
            await service.PrepareAsync(new PreparationRequest { Folders = ["Documents"] });
            await service.SaveRecentQueriesAsync(["cats", "  ", "forest", "CATS"]);
        }
        await using var reopened = Service();
        var status = await reopened.GetStatusAsync();
        CollectionAssert.AreEqual(new[] { "Documents" }, status.PreparationFolders.ToArray());
        CollectionAssert.AreEqual(new[] { "cats", "forest" }, status.RecentQueries.ToArray());
    }

    [TestMethod]
    public async Task PreparationReportsMeasuredProgressWithoutPerSegmentStageChanges()
    {
        for (int i = 0; i < 20; i++) await Write($"note-{i:D2}.txt", $"Progress note {i} about forests.");
        var reports = new List<PreparationProgress>();
        await using var service = Service();
        await service.PrepareAsync(new PreparationRequest { Folders = ["Documents"] }, new InlineProgress(reports.Add));
        Assert.IsTrue(reports.Any(p => p.Stage == PreparationStage.Extracting && p.Fraction is > 0 and < 1), "File progress should report a measured fraction once the census finishes.");
        Assert.AreEqual(1d, reports[^1].Fraction);
        Assert.IsFalse(reports.Any(p => p.Stage == PreparationStage.Embedding), "Embedding is part of reading each file, not a separate job phase.");
        Assert.IsTrue(reports.Select(p => p.CurrentPath).Distinct().Count() >= 20, "Every file still gets its own progress update.");
    }

    [TestMethod]
    public async Task PrepareSearchUpdateDeleteAndOriginalSafety()
    {
        await Write("lease.txt", "The lease permits two cats. Exact invoice AB-0091.");
        await Write("forest.txt", "A red bicycle rides through a forest.");
        var processor = new TestProcessor(); await using var service = Service(processor);
        var request = new PreparationRequest { Folders = ["Documents"] };
        var first = await service.PrepareAsync(request);
        Assert.AreEqual(2, first.FileCount); Assert.IsTrue(first.SemanticAvailable);
        var match = await service.SearchAsync(new SearchRequest { Query = "cats", Filters = new SearchFilters { ExactOnly = true } });
        Assert.AreEqual("lease.txt", match.Results[0].Name);
        Assert.AreEqual("The lease permits two cats. Exact invoice AB-0091.", await File.ReadAllTextAsync(Path.Combine(_root, "Documents", "lease.txt")));
        var extracted = processor.Extracted;
        var second = await service.PrepareAsync(request);
        Assert.AreEqual(extracted, processor.Extracted, "Unchanged files should not be re-extracted.");
        Assert.IsGreaterThan(first.GenerationId, second.GenerationId);
        await Write("lease.txt", "The updated lease forbids pets and allows plants.");
        File.Delete(Path.Combine(_root, "Documents", "forest.txt"));
        var third = await service.PrepareAsync(request);
        Assert.AreEqual(1, third.FileCount);
        Assert.AreEqual(0, (await service.SearchAsync(new SearchRequest { Query = "bicycle", Filters = new SearchFilters { ExactOnly = true } })).Results.Count);
        Assert.AreEqual("lease.txt", (await service.SearchAsync(new SearchRequest { Query = "plants", Filters = new SearchFilters { ExactOnly = true } })).Results[0].Name);
    }

    [TestMethod]
    public async Task ArchiveCanMoveToAnotherRootAndStillOpenOriginals()
    {
        await Write("رحلة summer.txt", "A bicycle in the forest.");
        await using (var service = Service()) await service.PrepareAsync(new PreparationRequest { Folders = ["Documents"] });
        var moved = _root + "-moved"; Directory.Move(_root, moved); _root = moved;
        await using var reopened = Service();
        var results = await reopened.SearchAsync(new SearchRequest { Query = "bicycle", Filters = new SearchFilters { ExactOnly = true } });
        Assert.AreEqual(1, results.Results.Count);
        var preview = await reopened.GetPreviewAsync(new(results.Results[0].FileId));
        Assert.IsTrue(preview.OriginalPath.StartsWith(moved, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(File.Exists(preview.OriginalPath));
    }

    [TestMethod]
    public async Task TraversalAndOutsideFoldersAreRejected()
    {
        await using var service = Service();
        await Assert.ThrowsAsync<IOException>(() => service.PrepareAsync(new PreparationRequest { Folders = ["../outside"] }));
        await Assert.ThrowsAsync<IOException>(() => service.PrepareAsync(new PreparationRequest { Folders = [Path.GetTempPath()] }));
    }

    [TestMethod]
    public async Task CancelledPreparationResumesCompletedFiles()
    {
        await Write("a.txt", "First cats"); await Write("b.txt", "Second forest");
        var processor = new TestProcessor(); using var cancellation = new CancellationTokenSource();
        await using var service = Service(processor);
        var progress = new InlineProgress(p => { if (p.Completed == 1) cancellation.Cancel(); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.PrepareAsync(new PreparationRequest { Folders = ["Documents"] }, progress, cancellation.Token));
        var firstCount = processor.Extracted;
        var status = await service.PrepareAsync(new PreparationRequest { Folders = ["Documents"] });
        Assert.AreEqual(2, status.FileCount);
        Assert.IsLessThanOrEqualTo(firstCount + 1, processor.Extracted);
    }

    [TestMethod]
    public async Task DifferentQueryEncoderCannotSearchOldVectors()
    {
        await Write("forest.txt", "A bicycle in a forest.");
        await using (var service = Service()) await service.PrepareAsync(new PreparationRequest { Folders = ["Documents"] });
        await using var changed = Service(fingerprint: "different-model");
        Assert.IsFalse((await changed.GetStatusAsync()).SemanticAvailable);
        var result = await changed.SearchAsync(new SearchRequest { Query = "bicycle" });
        Assert.IsFalse(result.SemanticUsed); Assert.AreEqual(1, result.Results.Count);
    }

    [TestMethod]
    public async Task MissingModelsLeaveHonestKeywordSearch()
    {
        await Write("forest.txt", "A bicycle in a forest.");
        await using var service = new ArchiveService(_root, new UnavailableEmbeddings(), new TestProcessor());
        var status = await service.PrepareAsync(new PreparationRequest { Folders = ["Documents"] });
        Assert.IsFalse(status.SemanticAvailable);
        var result = await service.SearchAsync(new SearchRequest { Query = "bicycle" });
        Assert.AreEqual(1, result.Results.Count); Assert.IsFalse(result.SemanticUsed);
    }

    [TestMethod]
    public async Task ExactIdentifiersAndLiteralSearchCannotInjectFtsOrSql()
    {
        await Write("invoice.txt", "Invoice AB-0091 has a balance of $55.");
        await using var service = Service(); await service.PrepareAsync(new PreparationRequest { Folders = ["Documents"] });
        foreach (var query in new[] { "AB-0091", "\"AB-0091\"", "' OR 1=1 --", "\" NEAR( \"", "100%_" })
            await service.SearchAsync(new SearchRequest { Query = query, Filters = new SearchFilters { ExactOnly = true } });
        Assert.AreEqual(1, (await service.GetStatusAsync()).FileCount);
    }

    [TestMethod]
    public async Task PeopleMustOverlapTheMatchingVideoMoment()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Documents", "trip.mp4"), "fixture");
        var processor = new TestProcessor { VideoFaces = true }; await using var service = Service(processor);
        await service.PrepareAsync(new PreparationRequest { Folders = ["Documents"] });
        var people = await service.GetPeopleAsync(); Assert.IsGreaterThan(0, people.Count);
        var person = await service.SavePersonAsync(new PersonUpdateRequest { PersonId = people[0].Id, Name = "Sam" });
        var observations = await service.GetPersonObservationsAsync(person.Id); Assert.IsGreaterThan(0, observations.Count);
        var result = await service.SearchAsync(new SearchRequest { Query = "Sam driving", Filters = new SearchFilters { ExactOnly = true } });
        Assert.AreEqual(0, result.Results.Count, "Sam at00:00 cannot be joined to driving at01:00.");
        var visible = await service.SearchAsync(new SearchRequest { Query = "Sam waving", Filters = new SearchFilters { ExactOnly = true } });
        Assert.AreEqual(1, visible.Results.Count);
        Assert.AreEqual(MatchCategory.PossiblePersonAction, visible.Results[0].MatchCategory);
    }

    private sealed class InlineProgress(Action<PreparationProgress> report) : IProgress<PreparationProgress> { public void Report(PreparationProgress value) => report(value); }
    internal sealed class TestEmbeddings(string fingerprint) : IEmbeddingProvider
    {
        public bool Available => true; public string Fingerprint => fingerprint;
        public Task<float[]?> EmbedTextAsync(string text, bool query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); var result = new float[512]; result[0] = .1f;
            string[] terms = ["cat", "forest", "bicycle", "lease", "plant", "driving", "waving"];
            for (var i = 0; i < terms.Length; i++) if (text.Contains(terms[i], StringComparison.OrdinalIgnoreCase)) result[i + 1] = 1;
            return Task.FromResult<float[]?>(result);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class UnavailableEmbeddings : IEmbeddingProvider
    {
        public bool Available => false; public string Fingerprint => "none";
        public Task<float[]?> EmbedTextAsync(string text, bool query, CancellationToken cancellationToken = default) => Task.FromResult<float[]?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal sealed class TestProcessor : IContentProcessor
    {
        public int Extracted { get; private set; } public bool VideoFaces { get; init; }
        public IReadOnlyList<RuntimeCapability> Capabilities => []; public string FaceFingerprint => "test-face-v1";
        public async IAsyncEnumerable<ExtractedSegment> ExtractAsync(string absolutePath, FileKind kind, PreparationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Extracted++; cancellationToken.ThrowIfCancellationRequested();
            if (kind == FileKind.Video && VideoFaces)
            {
                var face = new float[128]; face[0] = 1;
                yield return new() { Kind = SegmentKind.VideoFrame, Text = "Sam waving", StartMs = 0, EndMs = 1000, Faces = [new(face, 0, 0, 1, 1, .99)] };
                yield return new() { Kind = SegmentKind.VideoFrame, Text = "Someone driving a car", StartMs = 60000, EndMs = 61000 };
            }
            else yield return new() { Kind = SegmentKind.Text, Text = await File.ReadAllTextAsync(absolutePath, cancellationToken) };
        }
        public Task<IReadOnlyList<DetectedFace>> DetectReferenceFacesAsync(string absolutePath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DetectedFace>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
