using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class CheckpointTests
{
    private string root = "";
    [TestInitialize] public void Initialize() { root=Path.Combine(Path.GetTempPath(),"FindIt-checkpoint-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"Documents")); }
    [TestCleanup] public void Cleanup() { if(Directory.Exists(root))Directory.Delete(root,true); }
    private ArchiveService Service(IContentProcessor processor)=>new(root,new ArchiveTests.TestEmbeddings("checkpoint-512-v1"),processor);
    private async Task Originals(int count)
    { for(int i=0;i<count;i++)await File.WriteAllTextAsync(Path.Combine(root,"Documents",$"item-{i:D3}.txt"),$"Checkpoint item {i} forest."); }

    [TestMethod]
    public async Task FirstAndHundredFileCheckpointsAreReadableAndTheirPacksStayImmutable()
    {
        await Originals(102);
        var processor=new GatedProcessor(102);
        await using var preparer=Service(processor);
        var preparing=preparer.PrepareAsync(new(){Folders=["Documents"]});
        try
        {
            await processor.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.IsFalse(preparing.IsCompleted,"Preparation must still be processing the last file.");
            await using var reader=Service(new ArchiveTests.TestProcessor());
            var snapshot=await reader.GetStatusAsync();
            Assert.AreEqual(101L,snapshot.FileCount,"The first file and the following hundred committed files should already be searchable.");
            Assert.AreEqual(2L,snapshot.GenerationId);
            Assert.IsTrue((await reader.SearchAsync(new(){Query="Checkpoint",Filters=new(){ExactOnly=true}})).Results.Count>0);
            var packs=Directory.GetFiles(Path.Combine(root,"FindIt","archive","packs"),"*.pack")
                .Where(p=>new FileInfo(p).Length>0).ToDictionary(p=>p,p=>(Hash:SHA256.HashData(File.ReadAllBytes(p)),Modified:File.GetLastWriteTimeUtc(p)));
            Assert.IsTrue(packs.Count>=2,"Checkpoints should seal distinct vector packs.");
            processor.Release.TrySetResult();
            var final=await preparing.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(102L,final.FileCount);
            foreach(var pack in packs)
            {
                CollectionAssert.AreEqual(pack.Value.Hash,SHA256.HashData(File.ReadAllBytes(pack.Key)),"A published pack was appended or rewritten.");
                Assert.AreEqual(pack.Value.Modified,File.GetLastWriteTimeUtc(pack.Key));
            }
            Assert.AreEqual(102L,(await reader.GetStatusAsync()).FileCount,"A running reader should observe the final atomic pointer switch.");
        }
        finally { processor.Release.TrySetResult();await preparing; }
    }

    [TestMethod]
    public async Task PausePublishesOnlyCompleteFilesAndResumeReusesThem()
    {
        await Originals(3);
        var processor=new GatedProcessor(3);
        await using var service=Service(processor);
        using var cancellation=new CancellationTokenSource();
        var preparing=service.PrepareAsync(new(){Folders=["Documents"]},cancellationToken:cancellation.Token);
        await processor.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await using var reader=Service(new ArchiveTests.TestProcessor());
        Assert.AreEqual(1L,(await reader.GetStatusAsync()).FileCount,"The first committed file must be searchable before the first full walk ends.");
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(()=>preparing);
        Assert.AreEqual(2L,(await reader.GetStatusAsync()).FileCount,"Pause should publish committed work while omitting the interrupted file.");
        processor.Release.TrySetResult();
        Assert.AreEqual(3L,(await service.PrepareAsync(new(){Folders=["Documents"]})).FileCount);
        Assert.AreEqual(4,processor.Extracted,"Only the interrupted third file should be extracted again.");
    }

    [TestMethod]
    public async Task PausedReconciliationKeepsUnseenOldFilesUntilTheWalkFinishes()
    {
        await Originals(3);
        await using(var seed=Service(new ArchiveTests.TestProcessor()))await seed.PrepareAsync(new(){Folders=["Documents"]});
        File.Delete(Path.Combine(root,"Documents","item-002.txt"));
        await File.WriteAllTextAsync(Path.Combine(root,"Documents","item-000.txt"),"Updated checkpoint document zero.");
        await File.WriteAllTextAsync(Path.Combine(root,"Documents","item-001.txt"),"Updated checkpoint document one.");
        var processor=new GatedProcessor(2);
        await using var service=Service(processor);
        using var cancellation=new CancellationTokenSource();
        var preparing=service.PrepareAsync(new(){Folders=["Documents"]},cancellationToken:cancellation.Token);
        await processor.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(30));cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(()=>preparing);
        Assert.AreEqual(3L,(await service.GetStatusAsync()).FileCount,"An incomplete folder walk must not delete previously prepared items.");
        processor.Release.TrySetResult();
        Assert.AreEqual(2L,(await service.PrepareAsync(new(){Folders=["Documents"]})).FileCount,"Final reconciliation should remove the missing original.");
    }

    [TestMethod]
    public async Task ExplicitlySkippedAndEmptyEmbeddingSegmentsDoNotCausePerpetualReextraction()
    {
        await Originals(1);
        var processor=new GatedProcessor(int.MaxValue){SkipEmbedding=true};
        await using var service=Service(processor);
        await service.PrepareAsync(new(){Folders=["Documents"]});
        await service.PrepareAsync(new(){Folders=["Documents"]});
        Assert.AreEqual(1,processor.Extracted);
        Assert.AreEqual(1,(await service.SearchAsync(new(){Query="900001",Filters=new(){ExactOnly=true}})).Results.Count,"Skipped semantic content must remain in exact search.");
    }

    private sealed class GatedProcessor(int blockAt):IContentProcessor
    {
        public TaskCompletionSource Blocked { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Extracted { get; private set; }
        public bool SkipEmbedding { get; init; }
        public string FaceFingerprint=>"checkpoint-face-v1";
        public IReadOnlyList<RuntimeCapability> Capabilities=>[];
        public async IAsyncEnumerable<ExtractedSegment> ExtractAsync(string absolutePath,FileKind kind,PreparationRequest options,[EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            Extracted++;
            if(Extracted==blockAt){Blocked.TrySetResult();await Release.Task.WaitAsync(cancellationToken);}
            yield return new(){Kind=SegmentKind.Text,Text=SkipEmbedding?"900001":await File.ReadAllTextAsync(absolutePath,cancellationToken),SkipTextEmbedding=SkipEmbedding};
            if(SkipEmbedding)yield return new(){Kind=SegmentKind.VideoFrame,StartMs=0,EndMs=500};
        }
        public Task<IReadOnlyList<DetectedFace>> DetectReferenceFacesAsync(string absolutePath,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<DetectedFace>>([]);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
