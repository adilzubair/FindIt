using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FindIt.Core;
using FindIt.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class PackRollbackTests
{
    private string root="";
    [TestInitialize] public void Initialize(){root=Path.Combine(Path.GetTempPath(),"FindIt-pack-rollback-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"Documents"));}
    [TestCleanup] public void Cleanup(){if(Directory.Exists(root))Directory.Delete(root,true);}
    private ArchiveService Service(IContentProcessor processor)=>new(root,new ArchiveTests.TestEmbeddings("pack-rollback-v1"),processor);

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task InterruptedFileDiscardsItsPackedPayloadsAndResumeDoesNotDuplicateThem(bool cancel)
    {
        await File.WriteAllTextAsync(Path.Combine(root,"Documents","baseline.txt"),"An already published original.");
        await using(var seed=Service(new SegmentedProcessor(false)))await seed.PrepareAsync(new(){Folders=["Documents"]});
        var publishedPacks=Directory.GetFiles(Path.Combine(root,"FindIt","archive","packs"),"*.pack")
            .ToDictionary(p=>p,p=>SHA256.HashData(File.ReadAllBytes(p)));
        await File.WriteAllTextAsync(Path.Combine(root,"Documents","first.txt"),"First new original.");
        await File.WriteAllTextAsync(Path.Combine(root,"Documents","second.txt"),"Second new original.");
        var processor=new SegmentedProcessor(true){Cancel=cancel};
        await using var service=Service(processor);
        using var cancellation=new CancellationTokenSource();
        var preparing=service.PrepareAsync(new(){Folders=["Documents"]},cancellationToken:cancellation.Token);
        await processor.Interrupted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if(cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(()=>preparing);
            Assert.AreEqual(2L,(await service.GetStatusAsync()).FileCount);
        }
        else
        {
            processor.Release.TrySetResult();
            Assert.AreEqual(3L,(await preparing).FileCount,"A decoder failure should still retain its filename.");
        }
        AssertPackedBytesMatchCommittedReferences((await service.GetStatusAsync()).GenerationId);
        foreach(var pack in publishedPacks)
            CollectionAssert.AreEqual(pack.Value,SHA256.HashData(File.ReadAllBytes(pack.Key)),"Rolling back a new file changed a previously published pack.");
        processor.Release.TrySetResult();
        var resumed=await service.PrepareAsync(new(){Folders=["Documents"]});
        Assert.AreEqual(3L,resumed.FileCount);
        Assert.AreEqual(3,processor.Extracted,"Only the interrupted original should be extracted again.");
        Assert.AreEqual(5L,resumed.SegmentCount,"The failed attempt's three segments must not survive rollback or duplicate on resume.");
        AssertPackedBytesMatchCommittedReferences(resumed.GenerationId);
    }

    private void AssertPackedBytesMatchCommittedReferences(long generation)
    {
        var catalog=Path.Combine(root,"FindIt","archive","generations",generation.ToString("D20"),"catalog.sqlite");
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=catalog,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());db.Open();
        var ends=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        using(var command=db.CreateCommand())
        {
            command.CommandText="SELECT vector_path,vector_offset+vector_length FROM segments WHERE vector_path IS NOT NULL;";
            using var reader=command.ExecuteReader();while(reader.Read())Include(reader.GetString(0),reader.GetInt64(1));
        }
        using(var command=db.CreateCommand())
        {
            command.CommandText="SELECT thumbnail FROM segments WHERE thumbnail IS NOT NULL UNION ALL SELECT portrait FROM observations WHERE portrait IS NOT NULL;";
            using var reader=command.ExecuteReader();while(reader.Read())
            {
                var blob=JsonSerializer.Deserialize<BlobReference>(reader.GetString(0),WireJson.Options)!;
                Include(blob.RelativePath,blob.Offset+blob.Length);
            }
        }
        Assert.IsTrue(ends.Keys.Count(p=>p.Contains("vectors-",StringComparison.Ordinal))>=2,"The new committed file should share its writable pack with the cancelled file, separately from the old published pack.");
        foreach(var pack in Directory.GetFiles(Path.Combine(root,"FindIt","archive","packs"),"*.pack"))
        {
            string relative=Path.GetRelativePath(root,pack).Replace('\\','/');
            Assert.AreEqual(ends.GetValueOrDefault(relative),new FileInfo(pack).Length,$"Uncommitted payload bytes remain in {relative}.");
        }
        void Include(string path,long end)=>ends[path]=Math.Max(ends.GetValueOrDefault(path),end);
    }

    private sealed class SegmentedProcessor(bool interrupt):IContentProcessor
    {
        public TaskCompletionSource Interrupted { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancel { get; init; }
        public int Extracted { get; private set; }
        public string FaceFingerprint=>"pack-face-v1";
        public IReadOnlyList<RuntimeCapability> Capabilities=>[];
        public async IAsyncEnumerable<ExtractedSegment> ExtractAsync(string absolutePath,FileKind kind,PreparationRequest options,[EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            Extracted++;int count=interrupt&&Extracted>=2?3:1;
            for(int i=0;i<count;i++)
            {
                var face=new float[128];face[0]=1;
                yield return new(){Kind=SegmentKind.Text,Text="Forest packed segment "+i,ThumbnailJpeg=new byte[17+i],Faces=[new(face,0,0,1,1,.99,new byte[11+i])]};
            }
            if(interrupt&&Extracted==2)
            {
                Interrupted.TrySetResult();await Release.Task.WaitAsync(cancellationToken);
                if(!Cancel)throw new IOException("Decoder failed after returning partial content.");
            }
        }
        public Task<IReadOnlyList<DetectedFace>> DetectReferenceFacesAsync(string absolutePath,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<DetectedFace>>([]);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
