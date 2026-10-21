using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public class SplitPeopleTests
{
    [TestMethod]
    public async Task ExplicitlyMovingFaceObservationSplitsPeopleAndSearchMemberships()
    {
        var root=Path.Combine(Path.GetTempPath(),"FindIt-split",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"Videos"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root,"Videos","first.mp4"),"fixture");
            await File.WriteAllTextAsync(Path.Combine(root,"Videos","second.mp4"),"fixture");
            await using var service=new ArchiveService(root,new ArchiveTests.TestEmbeddings("split-v1"),new ArchiveTests.TestProcessor{VideoFaces=true});
            await service.PrepareAsync(new(){Folders=["Videos"]});
            var source=(await service.GetPeopleAsync()).First();
            source=await service.SavePersonAsync(new(){PersonId=source.Id,Name="Sam"});
            var observations=await service.GetPersonObservationsAsync(source.Id);
            Assert.AreEqual(2,observations.Count);
            var moved=observations[0];
            var target=await service.SavePersonAsync(new(){Name="Taylor",ConfirmObservationIds=[moved.Id]});
            Assert.AreNotEqual(source.Id,target.Id);
            Assert.AreEqual(1,(await service.GetPersonObservationsAsync(source.Id)).Count);
            Assert.AreEqual(moved.Id,(await service.GetPersonObservationsAsync(target.Id)).Single().Id);
            var matches=await service.SearchAsync(new(){Query="Taylor waving",Filters=new(){ExactOnly=true}});
            Assert.AreEqual(1,matches.Results.Count);Assert.AreEqual(moved.FileId,matches.Results[0].FileId);
            var oldMatches=await service.SearchAsync(new(){Query="Sam waving",Filters=new(){ExactOnly=true}});
            Assert.AreEqual(1,oldMatches.Results.Count);Assert.AreNotEqual(moved.FileId,oldMatches.Results[0].FileId);
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}
