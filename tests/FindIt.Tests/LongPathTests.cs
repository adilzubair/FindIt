using System.Security.Cryptography;
using FindIt.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class LongPathTests
{
    private string home = "", root = "";
    [TestInitialize]
    public void Initialize()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows SQLite VFS regression.");
        home = Path.Combine(Path.GetTempPath(), "FindIt-longpaths", Guid.NewGuid().ToString("N"));
        root = Path.Combine(home, new string('a', 70), "أرشيف-[];#-" + new string('b', 65), new string('c', 70));
        Directory.CreateDirectory(Path.Combine(root, "Documents"));
        Assert.IsTrue(Path.Combine(root, "FindIt", "archive", "people.sqlite").Length > 260);
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(home)) Directory.Delete(home, true); }
    private static ArchiveService Open(string path) => new(path, new ArchiveTests.TestEmbeddings("long-path-v1"), new ArchiveTests.TestProcessor());

    [TestMethod]
    public async Task DeepUnicodeArchiveSupportsPublicationAttachedPeopleAndReadOnlyQueries()
    {
        string original = Path.Combine(root, "Documents", "فاتورة.txt");
        await File.WriteAllTextAsync(original, "Invoice DEEP-4927 has a bicycle in a forest.");
        long generation;
        await using (var writer = Open(root))
        {
            var prepared = await writer.PrepareAsync(new() { Folders = ["Documents"] });
            Assert.AreEqual(1, prepared.FileCount); Assert.AreEqual(0, prepared.Issues.Count);
            generation = prepared.GenerationId;
            // Opens both overlay -> catalog and catalog -> overlay attachment directions.
            await writer.GetPeopleAsync();
            await writer.SavePersonAsync(new() { Name = "Deep path person" });
        }
        var before = await Snapshot(root);
        await using (var reader = Open(root))
        {
            Assert.AreEqual(generation, (await reader.GetStatusAsync()).GenerationId);
            var result = await reader.SearchAsync(new() { Query = "DEEP-4927", Filters = new() { ExactOnly = true } });
            Assert.AreEqual(1, result.Results.Count);
            Assert.AreEqual("Documents/فاتورة.txt", result.Results[0].RelativePath);
            Assert.AreEqual(original, (await reader.GetPreviewAsync(new(result.Results[0].FileId))).OriginalPath);
            await reader.GetPeopleAsync();
        }
        CollectionAssert.AreEqual(before, await Snapshot(root), "Reader must not modify a deep archive or its originals.");
    }

    [TestMethod]
    public async Task DeepArchiveCanUpdateDeleteAndMoveWithoutAbsoluteDatabasePaths()
    {
        string original = Path.Combine(root, "Documents", "notes.txt");
        await File.WriteAllTextAsync(original, "OLDPATH8473");
        await using (var writer = Open(root)) await writer.PrepareAsync(new() { Folders = ["Documents"] });
        await File.WriteAllTextAsync(original, "NEWPATH8473 plus changed content");
        await using (var writer = Open(root))
        {
            var status = await writer.PrepareAsync(new() { Folders = ["Documents"], VerifyContents = true });
            Assert.AreEqual(0, status.Issues.Count);
            Assert.AreEqual(1, (await writer.SearchAsync(new() { Query = "NEWPATH8473", Filters = new() { ExactOnly = true } })).Results.Count);
        }
        var destination = Path.Combine(Path.GetDirectoryName(root)!, new string('m', 75));
        Directory.Move(root, destination); root = destination;
        await using (var reader = Open(root))
            Assert.AreEqual(1, (await reader.SearchAsync(new() { Query = "NEWPATH8473", Filters = new() { ExactOnly = true } })).Results.Count);
        File.Delete(Path.Combine(root, "Documents", "notes.txt"));
        await using (var writer = Open(root))
        {
            Assert.AreEqual(0, (await writer.PrepareAsync(new() { Folders = ["Documents"] })).FileCount);
            Assert.AreEqual(0, (await writer.SearchAsync(new() { Query = "NEWPATH8473", Filters = new() { ExactOnly = true } })).Results.Count);
        }
    }

    private static async Task<string[]> Snapshot(string path)
    {
        var rows = new List<string>();
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            await using var stream = File.OpenRead(file);
            rows.Add(Path.GetRelativePath(path, file) + ":" + Convert.ToHexString(await SHA256.HashDataAsync(stream)) + ":" + File.GetLastWriteTimeUtc(file).Ticks);
        }
        return rows.ToArray();
    }
}
