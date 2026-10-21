using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using FindIt.Core;
using FindIt.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class PeopleReferencePolicyTests
{
    private string root = "";
    private PolicyProcessor processor = null!;
    private static float[] ReferenceVector => Vector(1, 0);
    // Mirrors the real USB failure: video/reference cosine .866, but the video's
    // pre-existing unknown prototype has cosine 1 and must not compete as a name.
    private static float[] VideoVector => Vector(.866f, .5f);
    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "FindIt-reference-policy", Guid.NewGuid().ToString("N"));
        foreach (string directory in new[] { "Videos", "Photos", "References" }) Directory.CreateDirectory(Path.Combine(root, directory));
        processor = new PolicyProcessor();
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private ArchiveService Service() => new(root, new ArchiveTests.TestEmbeddings("reference-policy-v1"), processor);

    [TestMethod]
    public async Task EnrollingNamedReferenceOverridesAnUnconfirmedPrototypeOfTheSameFace()
    {
        await using var service = Service(); await SeedUnknownVideoAndPhotoAsync(service);
        Assert.AreEqual(1, (await service.GetPeopleAsync()).Count, "The video should first seed one conservative unnamed cluster.");
        long generation = (await service.GetStatusAsync()).GenerationId;
        var named = await EnrollAsync(service, "Test Person", ReferenceVector);
        var observations = await service.GetPersonObservationsAsync(named.Id);
        Assert.AreEqual(3, observations.Count, "Both repeated video frames and the photo must match the newly supplied identity reference.");
        CollectionAssert.AreEqual(new long[] { 6500, 10000 }, observations.Where(o => o.RelativePath.StartsWith("Videos/", StringComparison.Ordinal)).Select(o => o.StartMs!.Value).Order().ToArray());
        Assert.IsTrue(observations.All(o => !o.Confirmed && !o.Rejected), "Automatic matches must remain distinguishable from user confirmations.");
        Assert.AreEqual(1L, ReferenceCount(named.Id), "Automatic matches must not become trusted identity references.");
        Assert.AreEqual(generation, (await service.GetStatusAsync()).GenerationId, "Enrollment must not rebuild the archive generation.");
    }

    [TestMethod]
    public async Task ManyUnknownPrototypesCannotHideTheNamedReferenceFromCandidateRetrieval()
    {
        await using var service = Service(); await SeedUnknownVideoAndPhotoAsync(service);
        using (var overlay = OpenOverlay())
        {
            for (int i = 0; i < 24; i++)
            {
                using var command = overlay.CreateCommand(); command.CommandText = "INSERT INTO people(id,name,confirmed) VALUES($id,$name,0); INSERT INTO person_references(person_id,embedding,fingerprint) VALUES($id,$vector,$fingerprint);";
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D")); command.Parameters.AddWithValue("$name", "Old unnamed cluster " + i);
                command.Parameters.AddWithValue("$vector", Encode(VideoVector)); command.Parameters.AddWithValue("$fingerprint", processor.FaceFingerprint); command.ExecuteNonQuery();
            }
        }
        var named = await EnrollAsync(service, "Test Person", ReferenceVector);
        Assert.AreEqual(3, (await service.GetPersonObservationsAsync(named.Id)).Count, "More than twelve unnamed reference nodes must not starve the named-reference tier.");
    }

    [TestMethod]
    public async Task CompetingConfirmedIdentitiesRemainUnknownWhenTheNamedMarginIsAmbiguous()
    {
        await using var service = Service(); await SeedUnknownVideoAndPhotoAsync(service);
        var first = await EnrollAsync(service, "Avery", ReferenceVector);
        Assert.AreEqual(3, (await service.GetPersonObservationsAsync(first.Id)).Count);
        var second = await EnrollAsync(service, "Blake", ReferenceVector);
        Assert.AreEqual(0, (await service.GetPersonObservationsAsync(first.Id)).Count);
        Assert.AreEqual(0, (await service.GetPersonObservationsAsync(second.Id)).Count);
        Assert.IsTrue((await service.GetPeopleAsync()).Any(p => !p.Confirmed && p.ObservationCount > 0), "Ambiguity should remain reviewable as unnamed face evidence.");
    }

    [TestMethod]
    public async Task ManyReferencesForOneNameCannotHideACompetingConfirmedIdentity()
    {
        await using var service = Service(); await SeedUnknownVideoAndPhotoAsync(service);
        var first = await EnrollAsync(service, "Avery", ReferenceVector, 32);
        var second = await EnrollAsync(service, "Blake", ReferenceVector);
        Assert.AreEqual(0, (await service.GetPersonObservationsAsync(first.Id)).Count, "The runner-up identity must be evaluated even when one name occupies more than an ANN candidate page.");
        Assert.AreEqual(0, (await service.GetPersonObservationsAsync(second.Id)).Count);
        Assert.AreEqual(32L, ReferenceCount(first.Id)); Assert.AreEqual(1L, ReferenceCount(second.Id));
    }

    [TestMethod]
    public async Task ExplicitConfirmationSurvivesEnrollmentOfAnAmbiguousCompetingIdentity()
    {
        await using var service = Service(); await SeedUnknownVideoAndPhotoAsync(service);
        var first = await EnrollAsync(service, "Avery", ReferenceVector);
        var selected = (await service.GetPersonObservationsAsync(first.Id)).First(o => o.StartMs == 6500);
        await service.SavePersonAsync(new() { PersonId = first.Id, Name = first.Name, ConfirmObservationIds = [selected.Id] });
        var second = await EnrollAsync(service, "Blake", ReferenceVector);
        var remaining = await service.GetPersonObservationsAsync(first.Id);
        Assert.AreEqual(1, remaining.Count); Assert.AreEqual(selected.Id, remaining[0].Id); Assert.IsTrue(remaining[0].Confirmed);
        Assert.AreEqual(0, (await service.GetPersonObservationsAsync(second.Id)).Count);
    }

    [TestMethod]
    public async Task RejectedIdentityIsNotReassignedByEnrollmentAndCanBeExplicitlyRestored()
    {
        await using var service = Service(); await SeedUnknownVideoAndPhotoAsync(service);
        var named = await EnrollAsync(service, "Avery", ReferenceVector);
        var selected = (await service.GetPersonObservationsAsync(named.Id)).First(o => o.StartMs == 6500);
        await service.SavePersonAsync(new() { PersonId = named.Id, Name = named.Name, RejectObservationIds = [selected.Id] });
        string reference = await ReferenceAsync(ReferenceVector);
        await service.SavePersonAsync(new() { PersonId = named.Id, Name = named.Name, ReferencePaths = [reference] });
        Assert.IsFalse((await service.GetPersonObservationsAsync(named.Id)).Any(o => o.Id == selected.Id && !o.Rejected), "Re-enrollment must not override an explicit identity rejection.");
        Assert.AreEqual(1L, RejectionCount(named.Id, selected.Id));
        await service.SavePersonAsync(new() { PersonId = named.Id, Name = named.Name, ConfirmObservationIds = [selected.Id] });
        await service.SavePersonAsync(new() { PersonId = named.Id, Name = named.Name, ReferencePaths = [reference] });
        var restored = (await service.GetPersonObservationsAsync(named.Id)).Single(o => o.Id == selected.Id);
        Assert.IsTrue(restored.Confirmed && !restored.Rejected); Assert.AreEqual(0L, RejectionCount(named.Id, selected.Id));
    }

    [TestMethod]
    public async Task WeakNamedReferenceCannotPromoteAnUnknownClusterToThatIdentity()
    {
        await using var service = Service(); await SeedUnknownVideoAndPhotoAsync(service);
        var named = await EnrollAsync(service, "Avery", Vector(0, 1));
        Assert.AreEqual(0, (await service.GetPersonObservationsAsync(named.Id)).Count, "Named-reference score .5 is below the unchanged .55 gate, despite a perfect unknown prototype match.");
        Assert.AreEqual(1L, ReferenceCount(named.Id)); Assert.IsTrue((await service.GetPeopleAsync()).Any(p => !p.Confirmed && p.ObservationCount >= 3));
    }

    [TestMethod]
    public async Task AutomaticallyMatchedFacesNeverBecomeReferencesForFurtherIdentityPropagation()
    {
        await using var service = Service(); await SeedUnknownVideoAndPhotoAsync(service);
        var named = await EnrollAsync(service, "Avery", ReferenceVector);
        Assert.AreEqual(3, (await service.GetPersonObservationsAsync(named.Id)).Count);
        // This face is close to the accepted video (.804), but weak against the sole
        // actual reference (.4). It must not inherit a name through that video match.
        processor.Vectors["neighbor.mp4"] = Vector(.4f, .916515f);
        await File.WriteAllTextAsync(Path.Combine(root, "Videos", "neighbor.mp4"), "another synthetic clip");
        await service.PrepareAsync(new() { Folders = ["Videos"] });
        Assert.IsFalse((await service.GetPersonObservationsAsync(named.Id)).Any(o => o.RelativePath.EndsWith("neighbor.mp4", StringComparison.Ordinal)));
        Assert.AreEqual(1L, ReferenceCount(named.Id), "Only explicit supplied references or a user-named prototype may anchor a named identity.");
    }

    private async Task SeedUnknownVideoAndPhotoAsync(ArchiveService service)
    {
        processor.Vectors["clip.mp4"] = VideoVector; processor.Vectors["portrait.jpg"] = ReferenceVector;
        await File.WriteAllTextAsync(Path.Combine(root, "Videos", "clip.mp4"), "synthetic clip");
        await service.PrepareAsync(new() { Folders = ["Videos"] });
        await File.WriteAllTextAsync(Path.Combine(root, "Photos", "portrait.jpg"), "synthetic photo");
        await service.PrepareAsync(new() { Folders = ["Photos"] });
    }
    private async Task<PersonRecord> EnrollAsync(ArchiveService service, string name, float[] vector, int count = 1)
    {
        var paths = new List<string>(); for (int i = 0; i < count; i++) paths.Add(await ReferenceAsync(vector));
        return await service.SavePersonAsync(new() { Name = name, ReferencePaths = paths });
    }
    private async Task<string> ReferenceAsync(float[] vector)
    {
        string path = Path.Combine(root, "References", Guid.NewGuid().ToString("N") + ".jpg"); await File.WriteAllTextAsync(path, "synthetic supplied reference"); processor.Vectors[Path.GetFileName(path)] = vector; return path;
    }
    private SqliteConnection OpenOverlay()
    { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "FindIt", "archive", "people.sqlite"), Pooling = false }.ToString()); connection.Open(); return connection; }
    private long ReferenceCount(Guid id)
    { using var db = OpenOverlay(); using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM person_references WHERE person_id=$id;"; query.Parameters.AddWithValue("$id", id.ToString("D")); return Convert.ToInt64(query.ExecuteScalar()); }
    private long RejectionCount(Guid id, long observation)
    { using var db = OpenOverlay(); using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM rejections WHERE person_id=$id AND observation_id=$observation;"; query.Parameters.AddWithValue("$id", id.ToString("D")); query.Parameters.AddWithValue("$observation", observation); return Convert.ToInt64(query.ExecuteScalar()); }
    private static float[] Vector(float x, float y) { var result = new float[128]; result[0] = x; result[1] = y; return result; }
    private static byte[] Encode(float[] vector)
    { double norm = Math.Sqrt(vector.Sum(v => (double)v * v)); var bytes = new byte[vector.Length * 2]; for (int i = 0; i < vector.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), BitConverter.HalfToInt16Bits((Half)(vector[i] / norm))); return bytes; }
    private sealed class PolicyProcessor : IContentProcessor
    {
        public Dictionary<string, float[]> Vectors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string FaceFingerprint => "fixture-face-policy-v1";
        public IReadOnlyList<RuntimeCapability> Capabilities => [];
        public async IAsyncEnumerable<ExtractedSegment> ExtractAsync(string absolutePath, FileKind kind, PreparationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask; float[] vector = Vectors[Path.GetFileName(absolutePath)];
            var face = new DetectedFace(vector, 0, 0, 1, 1, .99);
            if (kind == FileKind.Video)
            { foreach (long time in new long[] { 6500, 10000 }) yield return new() { Kind = SegmentKind.VideoFrame, StartMs = time, EndMs = time + 500, Text = "face evidence", Faces = [face] }; }
            else yield return new() { Kind = SegmentKind.Image, Text = "portrait evidence", Faces = [face] };
        }
        public Task<IReadOnlyList<DetectedFace>> DetectReferenceFacesAsync(string absolutePath, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<DetectedFace>>([new(Vectors[Path.GetFileName(absolutePath)], 0, 0, 1, 1, .99)]); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
