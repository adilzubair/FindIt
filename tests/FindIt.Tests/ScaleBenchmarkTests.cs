using System.Security.Cryptography;
using FindIt.Bench;
using FindIt.Runtime;
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

    [TestMethod]
    public void NativePrivateBuilderDoesNotEditSourceAndNormalOpenRemainsImmutable()
    {
        if(!NativeVectorIndex.Available)Assert.Inconclusive("Native USearch is required for private-copy builder validation.");
        var (directory,state)=Dataset(100);
        string original=Path.Combine(directory,"source.usearch"),extended=Path.Combine(directory,"extended.usearch");
        using(var initial=new NativeVectorIndex(512))
        {for(ulong row=0;row<40;row++)initial.Add(row,ScaleAlgorithms.Vector(state.Seed,row,state.Distribution));initial.Save(original);}
        string sourceHash=ScaleArtifacts.Hash(original);
        foreach(bool mapped in new[]{true,false})
        {
            using var immutable=NativeVectorIndex.Open(original,mapped);
            Assert.AreEqual(40L,immutable.Count);Assert.AreEqual(512,immutable.Dimensions);
            Assert.ThrowsExactly<InvalidOperationException>(()=>immutable.Add(40,ScaleAlgorithms.Vector(state.Seed,40,state.Distribution)));
        }
        using(var builder=NativeVectorIndex.OpenForUpdate(original))
        {
            for(ulong row=40;row<100;row++)builder.Add(row,ScaleAlgorithms.Vector(state.Seed,row,state.Distribution));
            Assert.AreEqual(100L,builder.Count);builder.Save(extended);
            Assert.AreEqual(sourceHash,ScaleArtifacts.Hash(original));
        }
        Assert.AreEqual(sourceHash,ScaleArtifacts.Hash(original));
        using var result=NativeVectorIndex.Open(extended);
        Assert.AreEqual(100L,result.Count);
        for(ulong row=0;row<100;row++)Assert.AreEqual(row,result.Search(ScaleAlgorithms.Vector(state.Seed,row,state.Distribution),1)[0].Key);
    }

    [TestMethod]
    public void NativeGraphCancellationResumesPrivateSnapshotWithoutChangingCanonicalData()
    {
        if(!NativeVectorIndex.Available)Assert.Inconclusive("Native USearch is required for resumable graph validation.");
        var (directory,state)=Dataset(768);string canonical=ScaleArtifacts.Hash(Path.Combine(directory,"vectors.f16"));
        using var cancel=new CancellationTokenSource();
        Assert.ThrowsExactly<OperationCanceledException>(()=>ScaleNativeBuilder.Build(directory,state,cancel.Token,128,rows=>{if(rows>=337)cancel.Cancel();},buildConcurrency:4));
        string pointer=Path.Combine(directory,ScaleNativeBuilder.CheckpointFileName);
        var snapshot=ScaleArtifacts.Read<ScaleGraphCheckpoint>(pointer);
        Assert.AreEqual(384,snapshot.Current.Rows);Assert.AreEqual(256,snapshot.Previous!.Rows);Assert.AreEqual(4,snapshot.BuildConcurrency);Assert.AreEqual(1024,snapshot.BatchRows);
        string immutableSource=ScaleNativeBuilder.SnapshotPath(directory,snapshot.Current);
        string sourceHash=ScaleArtifacts.Hash(immutableSource);
        int firstResumedRow=0;
        var finished=ScaleNativeBuilder.Build(directory,state,CancellationToken.None,128,rows=>
        {
            if(firstResumedRow==0)
            {firstResumedRow=rows;Assert.AreEqual(sourceHash,ScaleArtifacts.Hash(immutableSource));}
        },buildConcurrency:4);
        Assert.AreEqual(512,firstResumedRow);
        Assert.AreEqual(canonical,ScaleArtifacts.Hash(Path.Combine(directory,"vectors.f16")));
        Assert.AreEqual(finished.Sha256,ScaleArtifacts.Hash(Path.Combine(directory,"index.usearch")));
        using var complete=NativeVectorIndex.Open(Path.Combine(directory,"index.usearch"));
        Assert.AreEqual(768L,complete.Count);
        // Resumption must preserve every stored vector. Parallel graph insertion
        // does not guarantee perfect top-one ANN recall, even for corpus queries.
        for(ulong row=0;row<768;row++)
        {
            Assert.IsTrue(complete.Contains(row));
            CollectionAssert.AreEqual(ScaleAlgorithms.Vector(state.Seed,row,state.Distribution),complete.Get(row));
        }
        Assert.IsFalse(File.Exists(pointer));
        Assert.AreEqual(0,Directory.GetFiles(directory,"native-checkpoint-v1-*.usearch").Length);
        int callbacks=0;var reused=ScaleNativeBuilder.Build(directory,state,CancellationToken.None,128,_=>callbacks++,buildConcurrency:4);
        Assert.AreEqual(0,callbacks);Assert.AreEqual(finished,reused);
    }

    [TestMethod]
    public void NativeGraphResumeFallsBackToPreviousChecksumVerifiedSnapshot()
    {
        if(!NativeVectorIndex.Available)Assert.Inconclusive("Native USearch is required for checkpoint recovery validation.");
        var (directory,state)=Dataset(400);
        using var cancel=new CancellationTokenSource();
        Assert.ThrowsExactly<OperationCanceledException>(()=>ScaleNativeBuilder.Build(directory,state,cancel.Token,100,rows=>{if(rows>=250)cancel.Cancel();}));
        var checkpoint=ScaleArtifacts.Read<ScaleGraphCheckpoint>(Path.Combine(directory,ScaleNativeBuilder.CheckpointFileName));
        Assert.AreEqual(300,checkpoint.Current.Rows);Assert.AreEqual(200,checkpoint.Previous!.Rows);
        string damaged=ScaleNativeBuilder.SnapshotPath(directory,checkpoint.Current);
        using(var file=new FileStream(damaged,FileMode.Open,FileAccess.ReadWrite)){file.Position=file.Length-1;int prior=file.ReadByte();file.Position--;file.WriteByte((byte)(prior^0xff));}
        string unrelated=Path.Combine(directory,"native-checkpoint-v1-keep-original.usearch");File.WriteAllText(unrelated,"preserve");
        int first=0;
        ScaleNativeBuilder.Build(directory,state,CancellationToken.None,100,rows=>{if(first==0)first=rows;});
        Assert.AreEqual(300,first);Assert.AreEqual("preserve",File.ReadAllText(unrelated));
        using var complete=NativeVectorIndex.Open(Path.Combine(directory,"index.usearch"));Assert.AreEqual(400L,complete.Count);
    }

    [TestMethod]
    public void ParallelNativeBatchesRetainEveryKeyAndRejectInvalidBatchBeforeMutation()
    {
        if(!NativeVectorIndex.Available)Assert.Inconclusive("Native USearch is required for bounded parallel insertion validation.");
        var (directory,state)=Dataset(2048);string path=Path.Combine(directory,"parallel.usearch");
        float[] Batch(int first)=>Enumerable.Range(first,1024).SelectMany(row=>ScaleAlgorithms.Vector(state.Seed,(ulong)row,state.Distribution)).ToArray();
        float[] firstBatch=Batch(0),secondBatch=Batch(1024);
        using(var builder=new NativeVectorIndex(512))
        {
            builder.AddBatch(0,firstBatch,8,2048);Assert.AreEqual(1024L,builder.Count);
            var invalid=(float[])secondBatch.Clone();invalid[^1]=float.NaN;
            Assert.ThrowsExactly<ArgumentException>(()=>builder.AddBatch(1024,invalid,8,2048));Assert.AreEqual(1024L,builder.Count);
            Assert.ThrowsExactly<ArgumentException>(()=>builder.AddBatch(512,secondBatch,8,2048));Assert.AreEqual(1024L,builder.Count);
            builder.AddBatch(1024,secondBatch,8,2048);Assert.AreEqual(2048L,builder.Count);builder.Save(path);
        }
        foreach(bool mapped in new[]{true,false})
        {
            using var result=NativeVectorIndex.Open(path,mapped);Assert.AreEqual(2048L,result.Count);
            Assert.ThrowsExactly<InvalidOperationException>(()=>result.AddBatch(2048,firstBatch,8,4096));
            for(ulong row=0;row<2048;row++)
            {Assert.IsTrue(result.Contains(row));CollectionAssert.AreEqual(ScaleAlgorithms.Vector(state.Seed,row,state.Distribution),result.Get(row));}
            Assert.IsFalse(result.Contains(2048));
            Assert.IsNull(result.Get(2048));
            for(ulong row=0;row<2048;row+=67)
            {
                // This is storage/corruption validation, not an assertion that a short ANN query has perfect recall.
                var neighbors=result.Search(ScaleAlgorithms.Vector(state.Seed,row,state.Distribution),2048);
                Assert.IsTrue(neighbors.Count>0&&neighbors.Count<=2048);Assert.AreEqual(neighbors.Count,neighbors.Select(n=>n.Key).Distinct().Count());
                Assert.IsTrue(neighbors.All(n=>n.Key<2048&&float.IsFinite(n.Distance)));
                var query=ScaleAlgorithms.Vector(state.Seed,row,state.Distribution);
                foreach(var neighbor in neighbors.Take(8))
                {
                    double expected=1-ScalarCosine(query,ScaleAlgorithms.Vector(state.Seed,neighbor.Key,state.Distribution));
                    Assert.IsTrue(Math.Abs(neighbor.Distance-expected)<.0001);
                }
            }
        }
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
