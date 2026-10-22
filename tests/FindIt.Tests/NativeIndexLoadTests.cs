using FindIt.Bench;
using FindIt.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public class NativeIndexLoadTests
{
    private string root=null!;
    [TestInitialize]public void Initialize()=>root=Path.Combine(Path.GetTempPath(),"FindIt-NativeLoad",Guid.NewGuid().ToString("N"));
    [TestCleanup]public void Cleanup(){if(Directory.Exists(root))Directory.Delete(root,true);}

    [TestMethod]
    public void SearchExplorationCanChangeWithoutChangingReturnedCountOrPersistedGraph()
    {
        RequireNative(); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "index.usearch"); Save(path, 384);
        string hash = ScaleArtifacts.Hash(path);
        foreach (bool mapped in new[] { true, false })
        {
            using var index = NativeVectorIndex.Open(path, mapped);
            var query = ScaleAlgorithms.Vector(71, 999, "clustered-512", query: true);
            var allowed = Enumerable.Range(0, 384).Where(i => i % 2 == 0).Select(i => (ulong)i).ToHashSet();
            var baseline = index.Search(query, 50);
            var explored = index.Search(query, 50, searchExpansion: 8192);
            Assert.AreEqual(50, explored.Count);
            var exact = Enumerable.Range(0, 384).Select(i => new { Key = (ulong)i, Score = ScaleAlgorithms.Cosine(Vector((ulong)i), query, ScaleAlgorithms.Norm(Vector((ulong)i)), ScaleAlgorithms.Norm(query)) })
                .OrderByDescending(x => x.Score).ThenBy(x => x.Key).Take(50).Select(x => x.Key).ToHashSet();
            Assert.IsTrue(explored.Count(n => exact.Contains(n.Key)) >= baseline.Count(n => exact.Contains(n.Key)));
            CollectionAssert.AreEqual(baseline.ToArray(), index.Search(query, 50).ToArray(), "An earlier exploration setting leaked into a later default call.");
            Assert.IsTrue(index.Search(query, 50, allowed, 8192).All(n => allowed.Contains(n.Key)));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => index.Search(query, 50, searchExpansion: 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => index.Search(query, 50, searchExpansion: 65537));
            Assert.AreEqual(hash, ScaleArtifacts.Hash(path), "Search tuning edited an immutable persisted graph.");
        }
    }

    [TestMethod]
    public void StreamingEligibilityUsesResolvedShortAsciiPathsAndAccountsForFallbackCopy()
    {
        string syntheticRoot=Path.Combine(Path.GetPathRoot(root)!,"findit-load-predicate"); // Pure policy test: no files under this path.
        string ascii=Path.Combine(syntheticRoot,"index.usearch"),unicode=Path.Combine(syntheticRoot,"العثور","index.usearch");
        string longPath=Path.Combine(syntheticRoot,new string('a',140),new string('b',80),"index.usearch");
        Assert.IsTrue(NativeVectorIndex.CanStreamLoad(ascii));
        Assert.IsFalse(NativeVectorIndex.CanStreamLoad(unicode));Assert.IsFalse(NativeVectorIndex.CanStreamLoad(longPath));
        Assert.AreEqual(100L,NativeVectorIndex.EstimateLoadPeakBytes(ascii,100));
        Assert.AreEqual(200L,NativeVectorIndex.EstimateLoadPeakBytes(unicode,100));
        Assert.AreEqual(200L,NativeVectorIndex.EstimateLoadPeakBytes(longPath,100));
        Assert.AreEqual(long.MaxValue,NativeVectorIndex.EstimateLoadPeakBytes(unicode,long.MaxValue));
    }

    [TestMethod]
    public void StreamingAndUnicodeLongPathFallbackPreserveValuesAndPrivateBuilderOwnership()
    {
        RequireNative();
        foreach(string directory in new[]{root,Path.Combine(root,"صور"),Path.Combine(root,new string('a',140),new string('b',80))})
        {
            Directory.CreateDirectory(directory);string path=Path.Combine(directory,"index.usearch");Save(path,96);
            string original=ScaleArtifacts.Hash(path);
            foreach(bool mapped in new[]{true,false})
            {
                using var reader=NativeVectorIndex.Open(path,mapped);Assert.AreEqual(96L,reader.Count);
                for(ulong row=0;row<96;row++)CollectionAssert.AreEqual(Vector(row),reader.Get(row));
                Assert.ThrowsExactly<InvalidOperationException>(()=>reader.Add(96,Vector(96)));
            }
            using(var builder=NativeVectorIndex.OpenForUpdate(path))
            {
                builder.Add(96,Vector(96));Assert.AreEqual(97L,builder.Count);
                CollectionAssert.AreEqual(Vector(96),builder.Get(96));Assert.AreEqual(original,ScaleArtifacts.Hash(path));
                string moved=path+".moved";File.Move(path,moved);File.Move(moved,path);
            }
            Assert.AreEqual(original,ScaleArtifacts.Hash(path));
        }
    }

    [TestMethod]
    public void MappedReadersHoldSourceLeaseAndLoadedReadersReleaseIt()
    {
        RequireNative();Directory.CreateDirectory(root);string path=Path.Combine(root,"index.usearch");Save(path,96);
        using(var mapped=NativeVectorIndex.Open(path))
        {
            Assert.ThrowsExactly<IOException>(()=>{using var file=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);});
            Assert.ThrowsExactly<IOException>(()=>File.Move(path,path+".moved"));
        }
        using var loaded=NativeVectorIndex.Open(path,false);
        File.Move(path,path+".moved");Assert.AreEqual(96L,loaded.Count);CollectionAssert.AreEqual(Vector(17),loaded.Get(17));
    }

    [TestMethod]
    public void FailedStreamAndBufferLoadsReleaseEveryHandle()
    {
        RequireNative();
        foreach(string directory in new[]{root,Path.Combine(root,"صور")})
        {
            Directory.CreateDirectory(directory);string path=Path.Combine(directory,"truncated.usearch");Save(path,96);
            // Keep the 32-bit matrix dimensions, 96 FP16 vectors and 64-byte dense metadata valid,
            // but leave only one byte of the required graph header. Trailing allocator padding is not a failure fixture.
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Write)){file.SetLength(8L+96L*512*2+64+1);}
            Assert.ThrowsExactly<InvalidOperationException>(()=>{using var failed=NativeVectorIndex.Open(path,false);});
            using var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
            Assert.IsTrue(exclusive.Length>0);
        }
    }

    private static float[] Vector(ulong row)=>ScaleAlgorithms.Vector(71,row,"clustered-512");
    private static void Save(string path,int rows)
    {using var index=new NativeVectorIndex(512);for(ulong row=0;row<(ulong)rows;row++)index.Add(row,Vector(row));index.Save(path);}
    private static void RequireNative(){if(!NativeVectorIndex.Available)Assert.Inconclusive("The pinned native USearch runtime is required.");}
}
