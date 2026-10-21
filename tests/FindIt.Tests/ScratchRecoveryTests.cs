using FindIt.Runtime;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class ScratchRecoveryTests
{
    [TestMethod]
    public async Task ExtractionReclaimsMarkedAbandonedScratchAndKeepsActiveOrUnmarkedFolders()
    {
        var root=Path.Combine(Path.GetTempPath(),"FindIt-scratch-tests",Guid.NewGuid().ToString("N"));
        var scratch=Path.Combine(root,"FindIt","archive","work","runtime");
        var abandoned=Path.Combine(scratch,Guid.NewGuid().ToString("N"));
        var active=Path.Combine(scratch,Guid.NewGuid().ToString("N"));
        var unmarked=Path.Combine(scratch,Guid.NewGuid().ToString("N"));
        foreach(var folder in new[]{abandoned,active,unmarked})Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(abandoned,"active.lock"),"");
        await File.WriteAllTextAsync(Path.Combine(abandoned,"decoded.jpg"),"private scratch fixture");
        try
        {
            using(var lease=new FileStream(Path.Combine(active,"active.lock"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
            {
                await using var processor=RuntimeFactory.CreateContentProcessor(root);
                await foreach(var segment in processor.ExtractAsync(Path.Combine(root,"unknown.bin"),FileKind.Other,new())){}
                Assert.IsFalse(Directory.Exists(abandoned),"A dead worker's marked scratch should be reclaimed.");
                Assert.IsTrue(Directory.Exists(active),"A worker holding its lease must keep its scratch.");
                Assert.IsTrue(Directory.Exists(unmarked),"Unmarked directories must not be guessed to be stale.");
                Assert.AreEqual(2,Directory.GetDirectories(scratch).Length,"The current extraction should clean its own scratch on success.");
            }
        }
        finally {var resolved=Path.GetFullPath(root);if(resolved.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(),"FindIt-scratch-tests"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))Directory.Delete(resolved,true);}
    }
}
