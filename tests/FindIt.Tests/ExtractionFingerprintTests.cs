using System.Runtime.CompilerServices;
using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class ExtractionFingerprintTests
{
    [TestMethod]
    public async Task NewExtractionPolicyIsAppliedOnlyByExplicitPreparation()
    {
        var root=Path.Combine(Path.GetTempPath(),"FindIt-extraction-policy",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"Videos"));await File.WriteAllTextAsync(Path.Combine(root,"Videos","scene.mp4"),"unchanged source");
        try
        {
            var oldProcessor=new PolicyProcessor("intervals-v1");long originalGeneration;
            await using(var original=new ArchiveService(root,new ArchiveTests.TestEmbeddings("fixed-encoder"),oldProcessor))
            {
                originalGeneration=(await original.PrepareAsync(new(){Folders=["Videos"]})).GenerationId;
                Assert.AreEqual(1,oldProcessor.Extractions);
            }
            var processor=new PolicyProcessor("shot-boundaries-v2");
            await using var next=new ArchiveService(root,new ArchiveTests.TestEmbeddings("fixed-encoder"),processor);
            Assert.AreEqual(originalGeneration,(await next.GetStatusAsync()).GenerationId);
            Assert.AreEqual(1,(await next.SearchAsync(new(){Query="existing",Filters=new(){ExactOnly=true}})).Results.Count);
            Assert.AreEqual(0,processor.Extractions,"Opening and searching an older prepared archive must not silently regenerate media.");
            var updated=await next.PrepareAsync(new(){Folders=["Videos"]});
            Assert.IsTrue(updated.GenerationId>originalGeneration);
            Assert.AreEqual(1,processor.Extractions,"Explicit preparation must reextract unchanged sources when the temporal policy changes.");
            await next.PrepareAsync(new(){Folders=["Videos"]});Assert.AreEqual(1,processor.Extractions,"Unchanged content with the same policy must be reused.");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    private sealed class PolicyProcessor(string policy):IContentProcessor
    {
        public int Extractions{get;private set;}
        public string ExtractionFingerprint=>policy;
        public string FaceFingerprint=>"fixed-face-encoder";
        public IReadOnlyList<RuntimeCapability> Capabilities=>[];
        public async IAsyncEnumerable<ExtractedSegment> ExtractAsync(string absolutePath,FileKind kind,PreparationRequest options,[EnumeratorCancellation]CancellationToken cancellationToken=default)
        {await Task.CompletedTask;cancellationToken.ThrowIfCancellationRequested();Extractions++;yield return new(){Kind=SegmentKind.VideoFrame,StartMs=0,EndMs=1,Text="existing scene"};}
        public Task<IReadOnlyList<DetectedFace>> DetectReferenceFacesAsync(string absolutePath,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<DetectedFace>>([]);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
