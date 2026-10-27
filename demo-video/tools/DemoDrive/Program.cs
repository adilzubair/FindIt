using System.Text.Json;
using FindIt.Client;
using FindIt.Shared;

// DemoDrive prepare <root>                 prepares the demo drive's folders
// DemoDrive check <root> <storyboard.json> runs every storyboard search and shows what it returns
// DemoDrive clear-recent <root>            forgets recent searches, so the home page looks unused
if (args.Length < 2 || args[0] is not ("prepare" or "check" or "clear-recent") || args[0] == "check" && args.Length < 3)
{
    Console.Error.WriteLine("Usage: DemoDrive prepare <root> | DemoDrive check <root> <storyboard.json> | DemoDrive clear-recent <root>");
    return 2;
}
var root = Path.GetFullPath(args[1]);
if (!File.Exists(Path.Combine(root, ".findit-demo-drive")))
{
    Console.Error.WriteLine($"{root} is not a generated demo drive (no .findit-demo-drive marker).");
    return 2;
}
await using var client = await WorkerArchiveClient.CreateAsync(root);

if (args[0] == "clear-recent")
{
    await client.SaveRecentQueriesAsync([]);
    Console.WriteLine($"Recent searches cleared ({(await client.GetStatusAsync()).RecentQueries.Count} left).");
    return 0;
}
if (args[0] == "prepare")
{
    var folders = new[] { "Photos", "Videos", "Voice memos", "Documents" };
    var last = "";
    var status = await client.PrepareAsync(new PreparationRequest { Folders = folders, OcrLanguages = ["eng"] }, new Progress<PreparationProgress>(p =>
    {
        var line = $"{p.Stage,-17} {p.Completed,4}/{p.Discovered,-4} {p.Message}";
        if (line != last) Console.WriteLine(last = line);
    }));
    Console.WriteLine($"Prepared {status.FileCount} files, {status.SegmentCount} segments. {status.StatusDetail}");
    return 0;
}

using var storyboard = JsonDocument.Parse(await File.ReadAllTextAsync(args[2]));
var failed = 0;
foreach (var search in storyboard.RootElement.GetProperty("searches").EnumerateArray())
{
    var query = search.GetProperty("query").GetString()!;
    var expected = search.TryGetProperty("expectFirst", out var e) ? e.GetString() : null;
    var response = await client.SearchAsync(new SearchRequest { Query = query, Limit = 12 });
    Console.WriteLine($"\n\"{query}\"  ({response.Results.Count} results, {response.ElapsedMilliseconds:0} ms, semantic {response.SemanticUsed})");
    foreach (var (r, i) in response.Results.Select((r, i) => (r, i)))
    {
        Console.WriteLine($"  {i + 1,2}. {r.Score,6:0.000} {r.MatchCategory,-8} {r.Kind,-8} {r.RelativePath}");
        foreach (var m in r.Moments.Take(2))
        {
            var at = m.StartMs is { } ms ? TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss") : m.Page is { } page ? $"p{page}" : "";
            var text = m.Text.ReplaceLineEndings(" ");
            Console.WriteLine($"        {m.Kind,-12} {at,-5} {m.Label} | {(text.Length > 110 ? text[..110] + "…" : text)}");
        }
    }
    var first = response.Results.FirstOrDefault()?.RelativePath.Replace('\\', '/');
    if (expected is not null && !string.Equals(first, expected, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"  !! expected {expected} first");
        failed++;
    }
}
return failed == 0 ? 0 : 1;
