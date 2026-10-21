using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public class BrowsePagingTests
{
    [TestMethod]
    public async Task BrowsePagesReturnEveryFileOnceWithoutRepeatingFirstPage()
    {
        var root=Path.Combine(Path.GetTempPath(),"FindIt-paging",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"Documents"));
        try
        {
            for(int i=0;i<23;i++)await File.WriteAllTextAsync(Path.Combine(root,"Documents",$"item-{i:D3}.txt"),"prepared text");
            await using var service=new ArchiveService(root,new ArchiveTests.TestEmbeddings("paging"),new ArchiveTests.TestProcessor());
            await service.PrepareAsync(new(){Folders=["Documents"]});
            var seen=new HashSet<Guid>();string? cursor=null;int pages=0;
            do
            {
                var response=await service.SearchAsync(new(){Limit=4,Cursor=cursor});
                foreach(var result in response.Results)Assert.IsTrue(seen.Add(result.FileId),"A file appeared on multiple browse pages.");
                cursor=response.NextCursor;Assert.IsLessThanOrEqualTo(6,++pages,"Browse pagination did not terminate.");
            }while(cursor is not null);
            Assert.AreEqual(23,seen.Count);Assert.AreEqual(6,pages);
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}
