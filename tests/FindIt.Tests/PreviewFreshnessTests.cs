using System.Security.Cryptography;
using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class PreviewFreshnessTests
{
    private string root = "";
    private string Original => Path.Combine(root, "Videos", "trip.mp4");
    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(AppContext.BaseDirectory, "preview-guard-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Videos")); File.WriteAllText(Original, "original fixture bytes");
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private ArchiveService Service() => new(root, new ArchiveTests.TestEmbeddings("preview-freshness-v1"), new ArchiveTests.TestProcessor { VideoFaces = true });

    [TestMethod]
    public async Task ChangedOriginalSizeCannotBeOpenedAsItsPreviouslyPreparedPreview()
    {
        await using var service = Service(); var request = await PrepareRequestAsync(service);
        await File.WriteAllTextAsync(Original, "replacement contents belonging to another recording after preparation");
        var error = await Assert.ThrowsAsync<IOException>(() => service.GetPreviewAsync(request));
        StringAssert.Contains(error.Message, "original changed since preparation"); StringAssert.Contains(error.Message, "prepare this folder again");
    }

    [TestMethod]
    public async Task ChangedOriginalSizeIsDetectedEvenWhenItsTimestampIsPreserved()
    {
        await using var service = Service(); var request = await PrepareRequestAsync(service); DateTime timestamp = File.GetLastWriteTimeUtc(Original);
        await File.WriteAllTextAsync(Original, "different-sized replacement"); File.SetLastWriteTimeUtc(Original, timestamp);
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(Original));
        await Assert.ThrowsAsync<IOException>(() => service.GetPreviewAsync(request));
    }

    [TestMethod]
    public async Task TimestampOnlyChangeRequiresAnExplicitPreparationRefresh()
    {
        await using var service = Service(); var request = await PrepareRequestAsync(service); byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(Original));
        File.SetLastWriteTimeUtc(Original, File.GetLastWriteTimeUtc(Original).AddMinutes(5));
        await Assert.ThrowsAsync<IOException>(() => service.GetPreviewAsync(request));
        CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(Original)));
        await service.PrepareAsync(new() { Folders = ["Videos"] });
        Assert.AreEqual(Original, (await service.GetPreviewAsync(request with { SegmentId = null })).OriginalPath);
    }

    [TestMethod]
    public async Task UnchangedOriginalStillOpensAfterTheArchiveMovesToAnotherRoot()
    {
        PreviewRequest request;
        await using (var service = Service()) request = await PrepareRequestAsync(service);
        DateTime timestamp = File.GetLastWriteTimeUtc(Original); string moved = root + "-moved";
        Directory.Move(root, moved); root = moved;
        await using var reopened = Service(); var preview = await reopened.GetPreviewAsync(request);
        Assert.AreEqual(Original, preview.OriginalPath); Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(Original));
        Assert.IsTrue(File.Exists(preview.OriginalPath));
    }

    [TestMethod]
    public async Task ChangedSourceCannotReturnAcceptedPersonEvidenceForNewPlayback()
    {
        await using var service = Service(); var request = await PrepareRequestAsync(service);
        var person = (await service.GetPeopleAsync()).Single(); person = await service.SavePersonAsync(new() { PersonId = person.Id, Name = "Sam" });
        request = request with { PersonIds = [person.Id] };
        var prepared = await service.GetPreviewAsync(request);
        Assert.IsTrue(prepared.PersonEvidence.Count > 0, "The fixture must first have accepted person evidence.");
        await File.WriteAllTextAsync(Original, "replacement with unrelated people and a different timeline");
        await Assert.ThrowsAsync<IOException>(() => service.GetPreviewAsync(request));
    }

    [TestMethod]
    public async Task PreviewChecksNeverMutateOriginalOrPublishedArchiveBytes()
    {
        await using var service = Service(); var request = await PrepareRequestAsync(service);
        byte[] originalHash = SHA256.HashData(await File.ReadAllBytesAsync(Original)); var published = SnapshotPublished();
        Assert.AreEqual(Original, (await service.GetPreviewAsync(request)).OriginalPath);
        CollectionAssert.AreEqual(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(Original)));
        CollectionAssert.AreEqual(published, SnapshotPublished());

        await File.WriteAllTextAsync(Original, "replacement used only by this workspace fixture");
        byte[] replacementHash = SHA256.HashData(await File.ReadAllBytesAsync(Original));
        await Assert.ThrowsAsync<IOException>(() => service.GetPreviewAsync(request));
        CollectionAssert.AreEqual(replacementHash, SHA256.HashData(await File.ReadAllBytesAsync(Original)));
        CollectionAssert.AreEqual(published, SnapshotPublished());
    }

    private static async Task<PreviewRequest> PrepareRequestAsync(ArchiveService service)
    {
        await service.PrepareAsync(new() { Folders = ["Videos"] });
        var result = (await service.SearchAsync(new() { Query = "waving", Filters = new() { ExactOnly = true } })).Results.Single();
        return new(result.FileId, result.Moments.Single().SegmentId);
    }
    private string[] SnapshotPublished()
    {
        string archive = Path.Combine(root, "FindIt", "archive");
        return new[] { "generations", "packs" }.SelectMany(folder => Directory.EnumerateFiles(Path.Combine(archive, folder), "*", SearchOption.AllDirectories))
            .Append(Path.Combine(archive, "current.json")).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(archive, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    }
}
