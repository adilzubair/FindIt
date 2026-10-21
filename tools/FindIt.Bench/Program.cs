using System.Diagnostics;
using System.Text.Json;
using FindIt.Client;
using FindIt.Shared;
using FindIt.Runtime;

string? Option(string name) { var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null; }
static double Percentile(IEnumerable<double> values,double percentile) {var a=values.Order().ToArray();return a.Length==0?0:a[Math.Clamp((int)Math.Ceiling(a.Length*percentile)-1,0,a.Length-1)];}
var root=Option("--root");
if(root is null) { Console.Error.WriteLine("Usage: FindIt.Bench --root <drive> [--prepare <folder>] [--queries <text file>] [--output <json>] [--lexical] [--repeat 3] [--ann-records 100000]");return 2; }
try
{
    root=Path.GetFullPath(root);
    if(Option("--ann-records") is { } countText)
    {
        int count=int.Parse(countText);if(count<100||count>2_000_000)throw new ArgumentOutOfRangeException("--ann-records");
        var timer=Stopwatch.StartNew();var random=new Random(42);var vectors=new List<float[]>();
        var nativePath=Path.Combine(root,"ann-benchmark.usearch");Directory.CreateDirectory(root);
        using(var index=new NativeVectorIndex(512))
        {
            for(int i=0;i<count;i++)
            {
                var v=new float[512];double sum=0;for(int d=0;d<v.Length;d++){v[d]=(float)(random.NextDouble()*2-1);sum+=v[d]*v[d];}
                var scale=(float)(1/Math.Sqrt(sum));for(int d=0;d<v.Length;d++)v[d]*=scale;
                if(i<100)vectors.Add(v);index.Add((ulong)i,v);
                if(i>0&&i%10000==0)Console.Error.WriteLine($"Indexed {i:N0}/{count:N0} synthetic vectors");
            }
            index.Save(nativePath);
        }
        var buildMs=timer.Elapsed.TotalMilliseconds;var elapsed=new List<double>();int selfMatches=0;
        long residentWhileOpen,privateWhileOpen;
        using(var index=NativeVectorIndex.Open(nativePath,true))
        {
            foreach(var (vector,i) in vectors.Select((v,i)=>(v,i))){timer.Restart();var neighbors=index.Search(vector,50);elapsed.Add(timer.Elapsed.TotalMilliseconds);if(neighbors.Any(n=>n.Key==(ulong)i))selfMatches++;}
            using var active=Process.GetCurrentProcess();active.Refresh();residentWhileOpen=active.WorkingSet64;privateWhileOpen=active.PrivateMemorySize64;
        }
        using var current=Process.GetCurrentProcess();var report=new {kind="synthetic-ann-performance-only",count,dimensions=512,buildMs,indexBytes=new FileInfo(nativePath).Length,p50Ms=Percentile(elapsed,.5),p95Ms=Percentile(elapsed,.95),selfRecall=(double)selfMatches/vectors.Count,residentWhileOpen,privateWhileOpen,peakWorkingSetBytes=current.PeakWorkingSet64,memoryScope="This process, including managed vector generation; resident mapping measured before disposal. Self recall is not Recall@50."};
        var json=JsonSerializer.Serialize(report,new JsonSerializerOptions(WireJson.Options){WriteIndented=true});Console.WriteLine(json);
        if(Option("--output")is{}output){Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);await File.WriteAllTextAsync(output,json);}
        return 0;
    }
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
