using System.Security.Cryptography;
using FindIt.Bench;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public class ScaleBenchmarkTests
{
    private string temporary = null!;
    [TestInitialize] public void Initialize() => temporary = Path.Combine(Path.GetTempPath(), "FindIt-ScaleTests", Guid.NewGuid().ToString("N"), "artifacts");
    [TestCleanup] public void Cleanup()
    {
        // This exact UUID-owned test directory is the only recursive cleanup target.
        if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
    }

    [TestMethod]
    public void HeldOutQueriesAreIndependentAndFp16Stable()
    {
        foreach (string distribution in ScaleAlgorithms.Distributions)
        {
            var corpus = ScaleAlgorithms.Vector(42, 17, distribution);
            var query = ScaleAlgorithms.Vector(42, 17, distribution, query: true);
            CollectionAssert.AreEqual(corpus, ScaleAlgorithms.Vector(42, 17, distribution));
            Assert.IsFalse(corpus.SequenceEqual(query));
            Assert.IsTrue(Math.Abs(ScaleAlgorithms.Norm(corpus) - 1) < .001);
            var encoded = new byte[ScaleAlgorithms.BytesPerVector]; var decoded = new float[ScaleAlgorithms.Dimensions];
            ScaleAlgorithms.Encode(corpus, encoded); ScaleAlgorithms.Decode(encoded, decoded);
            CollectionAssert.AreEqual(corpus, decoded);
        }
    }

    [TestMethod]
    public void ExactTopKRetainsBestFiftyAndBreaksTiesByKey()
    {
        var queue = new ExactTopK();
        for (ulong id = 99; ; id--)
        {
            queue.Add(id, id % 10);
            if (id == 0) break;
        }
        var expected = Enumerable.Range(0, 100).OrderByDescending(id => id % 10).ThenBy(id => id).Take(50).Select(id => (ulong)id).ToArray();
        CollectionAssert.AreEqual(expected, queue.Results().Select(x => x.Key).ToArray());
    }

    [TestMethod]
    public void ExhaustiveReferenceAgreesWithIndependentScalarCosineAndFilters()
    {
        var (directory, state) = Dataset(300);
        var truth = ScaleBenchmark.EnsureTruth(directory, state, 4, state.Seed, CancellationToken.None);
        Assert.AreEqual(300L, truth.ScannedRows);
        var corpus = Enumerable.Range(0, 300).Select(row => ScaleAlgorithms.Vector(state.Seed, (ulong)row, state.Distribution)).ToArray();
        for (int queryId = 0; queryId < 4; queryId++)
        {
            var query = ScaleAlgorithms.Vector(state.Seed, (ulong)queryId, state.Distribution, query: true);
            for (int filter = 0; filter < 3; filter++)
            {
                int modulus = ScaleAlgorithms.FilterModuli[filter];
                var expected = corpus.Select((v, id) => new { Id = id, Score = ScalarCosine(v, query) }).Where(x => ScaleAlgorithms.Accept((ulong)x.Id, modulus))
                    .OrderByDescending(x => x.Score).ThenBy(x => x.Id).Take(50).Select(x => (ulong)x.Id).ToArray();
                CollectionAssert.AreEqual(expected, truth.Neighbors[queryId][filter].Select(x => x.Key).ToArray());
                Assert.AreEqual(ScaleAlgorithms.EligibleCount(300, modulus), corpus.Select((_, id) => id).LongCount(id => ScaleAlgorithms.Accept((ulong)id, modulus)));
            }
        }
        var reloaded = ScaleBenchmark.EnsureTruth(directory, state, 4, state.Seed, CancellationToken.None);
        Assert.AreEqual(truth.ElapsedMs, reloaded.ElapsedMs);
    }

    [TestMethod]
    public void CanonicalGenerationResumesDurablePrefixAndDiscardsOnlyUncommittedTail()
    {
        var (directory, complete) = Dataset(9000);
        string path = Path.Combine(directory, "vectors.f16");
        string expectedHash = ScaleArtifacts.Hash(path);
        const int rows = 8192;
        byte[] prefix = File.ReadAllBytes(path)[..(rows * ScaleAlgorithms.BytesPerVector)];
        var checkpoint = complete with { CommittedRows = rows, Sha256 = Convert.ToHexString(SHA256.HashData(prefix)).ToLowerInvariant() };
        File.WriteAllBytes(path, [..prefix, 0x2a, 0x19, 0x05]);
        ScaleArtifacts.AtomicWrite(Path.Combine(directory, "dataset.json"), checkpoint);
        var resumed = ScaleBenchmark.EnsureDataset(directory, checkpoint, CancellationToken.None);
        Assert.AreEqual(expectedHash, resumed.Sha256);
        Assert.AreEqual(expectedHash, ScaleArtifacts.Hash(path));
    }

    [TestMethod]
    public void CorruptCompletedDatasetIsRejectedWithoutOverwritingIt()
    {
        var (directory, state) = Dataset(100);
        string path = Path.Combine(directory, "vectors.f16");
        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) { file.Position = 257; int old = file.ReadByte(); file.Position--; file.WriteByte((byte)(old ^ 0xff)); }
        string damagedHash = ScaleArtifacts.Hash(path);
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleBenchmark.EnsureDataset(directory, state, CancellationToken.None));
        Assert.AreEqual(damagedHash, ScaleArtifacts.Hash(path));
    }

    [TestMethod]
    public void CancellationLeavesAReusableExactReferenceCheckpoint()
    {
        var (directory, state) = Dataset(100);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => ScaleBenchmark.EnsureTruth(directory, state, 4, state.Seed, cancelled.Token));
        var recovered = ScaleBenchmark.EnsureTruth(directory, state, 4, state.Seed, CancellationToken.None);
        Assert.AreEqual(100L, recovered.ScannedRows);
        Assert.AreEqual(50, recovered.Neighbors[0][0].Length);
    }

    [TestMethod]
    public void StorageRefusesArchivesAndUnownedExistingDirectories()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => ScaleArtifacts.DatasetDirectory(Path.Combine(Path.GetTempPath(), "FindIt-not-artifact"), 100, 42, "isotropic-512"));
        string target = Path.Combine(temporary, "scale-v1-isotropic-512-100-42"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "original.txt"), "preserve");
        Assert.ThrowsExactly<InvalidOperationException>(() => ScaleArtifacts.DatasetDirectory(temporary, 100, 42, "isotropic-512"));
        Assert.AreEqual("preserve", File.ReadAllText(Path.Combine(target, "original.txt")));
        ScaleArtifacts.AtomicWrite(Path.Combine(target, "dataset.json"), new ScaleDataset(1, "some other owner", 100, 42, "isotropic-512", 512));
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleArtifacts.Validate(target));
    }

    private (string Directory, ScaleDataset State) Dataset(int count)
    {
        string directory = ScaleArtifacts.DatasetDirectory(temporary, count, 42, "isotropic-512");
        var state = new ScaleDataset(1, ScaleArtifacts.Owner, count, 42, "isotropic-512", 512);
        ScaleArtifacts.AtomicWrite(Path.Combine(directory, "dataset.json"), state);
        return (directory, ScaleBenchmark.EnsureDataset(directory, state, CancellationToken.None));
    }

    private static double ScalarCosine(float[] left, float[] right)
    {
        double dot = 0, a = 0, b = 0;
        for (int i = 0; i < left.Length; i++) { dot += (double)left[i] * right[i]; a += (double)left[i] * left[i]; b += (double)right[i] * right[i]; }
        return dot / Math.Sqrt(a * b);
    }
}
