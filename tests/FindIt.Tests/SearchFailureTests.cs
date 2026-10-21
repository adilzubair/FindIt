using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public class SearchFailureTests
{
    [TestMethod]
    public async Task EncoderFailurePreservesKeywordResultsAndReportsDegradation()
    {
        var root=Path.Combine(Path.GetTempPath(),"FindIt-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"Documents"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root,"Documents","lease.txt"),"The lease permits cats.");
            await using(var service=new ArchiveService(root,new ArchiveTests.TestEmbeddings("test-v1"),new ArchiveTests.TestProcessor()))await service.PrepareAsync(new(){Folders=["Documents"]});
            await using var failing=new ArchiveService(root,new FailingEncoder(),new ArchiveTests.TestProcessor());
            var result=await failing.SearchAsync(new(){Query="cats"});
            Assert.AreEqual(1,result.Results.Count);Assert.IsFalse(result.SemanticUsed);Assert.IsFalse(string.IsNullOrWhiteSpace(result.Notice));
        }
        finally{Directory.Delete(root,true);}
    }

    [TestMethod]
    public async Task AmbiguousAliasesNeverChooseAnArbitraryPerson()
    {
        var root=Path.Combine(Path.GetTempPath(),"FindIt-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"Documents"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root,"Documents","sam.txt"),"Sam is driving.");
            await using var service=new ArchiveService(root,new ArchiveTests.TestEmbeddings("test-v1"),new ArchiveTests.TestProcessor());
            await service.PrepareAsync(new(){Folders=["Documents"]});
            await service.SavePersonAsync(new(){Name="Samuel",Aliases=["Sam"]});
            await service.SavePersonAsync(new(){Name="Samantha",Aliases=["Sam"]});
            var result=await service.SearchAsync(new(){Query="Sam driving",Filters=new(){ExactOnly=true}});
            Assert.AreEqual(0,result.People.Count);Assert.IsFalse(string.IsNullOrWhiteSpace(result.Notice));
        }
        finally{Directory.Delete(root,true);}
    }

    private sealed class FailingEncoder:IEmbeddingProvider
    {
        public bool Available=>true;public string Fingerprint=>"test-v1";
        public Task<float[]?> EmbedTextAsync(string text,bool query,CancellationToken cancellationToken=default)=>throw new IOException("Fixture: model could not initialize.");
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
