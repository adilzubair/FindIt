using System.Diagnostics;
using System.Text.Json;
using FindIt.Client;
using FindIt.Shared;
using FindIt.Runtime;

string? Option(string name) { var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null; }
static double Percentile(IEnumerable<double> values,double percentile) {var a=values.Order().ToArray();return a.Length==0?0:a[Math.Clamp((int)Math.Ceiling(a.Length*percentile)-1,0,a.Length-1)];}
var root=Option("--root");
if(args.Contains("--scale-measure")) return await FindIt.Bench.ScaleBenchmark.MeasureChildAsync(args);
if(args.Contains("--ann-records")) return await FindIt.Bench.ScaleBenchmark.RunAsync(args);
if(root is null) { Console.Error.WriteLine("Usage: FindIt.Bench --root <drive> [--prepare <folder>] [--queries <text file>] [--output <json>] [--lexical] [--repeat 3] [--ann-records 100000]");return 2; }
try
{
    root=Path.GetFullPath(root);
    var launch=Stopwatch.StartNew();await using var backend=await WorkerArchiveClient.CreateAsync(root);
    var startupMs=launch.Elapsed.TotalMilliseconds;var status=await backend.GetStatusAsync();
    if(Option("--prepare") is { } folder)
    {
        var progress=new Progress<PreparationProgress>(p=>Console.Error.WriteLine($"{p.Stage}: {p.Completed}/{p.Discovered} {p.CurrentPath} {p.Message}"));
        status=await backend.PrepareAsync(new PreparationRequest{Folders=[folder],OcrLanguages=["eng"]},progress);
        Console.Error.WriteLine($"Prepared {status.FileCount:N0} files and {status.SegmentCount:N0} searchable records.");
    }
    var queries=Option("--queries")is{}queryFile?(await File.ReadAllLinesAsync(queryFile)).Where(q=>!string.IsNullOrWhiteSpace(q)).ToArray():new[]{"a bicycle in a forest","a bridge over water","invoice payment","someone driving a car"};
    int repeat=int.TryParse(Option("--repeat"),out int parsed)?Math.Clamp(parsed,1,100):3;
    bool exact=args.Contains("--lexical");
    // Warm with diverse queries; this is distinct from first-search and cold-start measurements.
    var first=Stopwatch.StartNew();await backend.SearchAsync(new SearchRequest{Query=queries[0],Filters=new(){ExactOnly=exact}});var firstSearchMs=first.Elapsed.TotalMilliseconds;
    foreach(var query in queries)await backend.SearchAsync(new SearchRequest{Query=query,Filters=new(){ExactOnly=exact}});
    var measurements=new List<object>();var durations=new List<double>();
    for(int run=0;run<repeat;run++)foreach(var query in queries)
    {
        var timer=Stopwatch.StartNew();var response=await backend.SearchAsync(new SearchRequest{Query=query,Filters=new(){ExactOnly=exact},Limit=60});
        var elapsed=timer.Elapsed.TotalMilliseconds;durations.Add(elapsed);
        measurements.Add(new{query,run,elapsedMs=elapsed,response.SemanticUsed,count=response.Results.Count,response.Notice,top=response.Results.Take(5).Select(r=>new{r.RelativePath,r.MatchCategory,moments=r.Moments.Select(m=>new{m.StartMs,m.EndMs,m.Page})})});
    }
    var diagnostics=backend is WorkerArchiveClient client?await client.GetDiagnosticsAsync():null;
    var summary=new{kind="live-reader-benchmark",measuredUtc=DateTimeOffset.UtcNow,framework=Environment.Version.ToString(),os=Environment.OSVersion.VersionString,startupMs,firstSearchMs,status.FileCount,status.SegmentCount,status.SemanticAvailable,p50Ms=Percentile(durations,.5),p95Ms=Percentile(durations,.95),reader=diagnostics,memoryScope="Reader worker only; UI, native preview, and whole-system frame timing require separate certification.",measurements};
    var result=JsonSerializer.Serialize(summary,new JsonSerializerOptions(WireJson.Options){WriteIndented=true});Console.WriteLine(result);
    if(Option("--output")is{}path){Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);await File.WriteAllTextAsync(path,result);}
    return 0;
}
catch(Exception exception){Console.Error.WriteLine(exception.ToString());return 1;}
