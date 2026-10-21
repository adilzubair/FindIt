using System.Diagnostics;
using System.Text.RegularExpressions;
using FindIt.Runtime;

namespace FindIt.Bench;

public sealed record ScaleGraphSnapshot(string FileName, int Rows, long Bytes, string Sha256, double BuildMs);
public sealed record ScaleGraphCheckpoint(int Schema, string Owner, string DatasetSha256, string NativeSha256,
    int Dimensions, string Settings, ScaleGraphSnapshot Current, ScaleGraphSnapshot? Previous,
    int BuildConcurrency = 1,int BatchRows = 1);

/// <summary>Durable synthetic graph building. Only a private loaded native copy is ever writable.</summary>
public static class ScaleNativeBuilder
{
    public const int CheckpointInterval = 50_000;
    public const int BatchRows = 1024;
    public const string CheckpointFileName = "native-build.checkpoint.json";
    public const string Settings = "cosine-fp16-m16-efAdd128-efSearch128";
    private static readonly Regex OwnedGraph = new(@"^native-checkpoint-v1-[0-9]{7}-[0-9a-f]{32}\.usearch$",RegexOptions.CultureInvariant);

    public static ScaleIndex Build(string directory, ScaleDataset dataset, CancellationToken token,
        int checkpointRows = CheckpointInterval, Action<int>? progress = null,int buildConcurrency = 1)
    {
        if(checkpointRows<1||checkpointRows>500_000)throw new ArgumentOutOfRangeException(nameof(checkpointRows));
        if(buildConcurrency<1||buildConcurrency>8)throw new ArgumentOutOfRangeException(nameof(buildConcurrency));
        var owned=ScaleArtifacts.Validate(directory);
        if(owned!=dataset||dataset.CommittedRows!=dataset.Count||dataset.Sha256 is null)throw new InvalidDataException("Native build requires the matching complete owned canonical dataset.");
        string leasePath=Path.Combine(directory,".native-build.lock");ScaleArtifacts.RejectLinkedFile(leasePath);
        using var lease=new FileStream(leasePath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        string path=Path.Combine(directory,"index.usearch"),manifest=Path.Combine(directory,"index.json");
        string pointer=Path.Combine(directory,CheckpointFileName);
        string native=new RuntimePaths(AppContext.BaseDirectory).Native("libusearch_c.dll")??throw new FileNotFoundException("Pinned USearch DLL is unavailable.");
        string nativeHash=ScaleArtifacts.Hash(native);
        if(File.Exists(manifest))
        {
            // The original completed-index schema is deliberately unchanged: prior benchmark artifacts remain reusable.
            var completed=ScaleArtifacts.Read<ScaleIndex>(manifest);
            if(completed.DatasetSha256==dataset.Sha256&&completed.NativeSha256==nativeHash&&File.Exists(path)&&completed.Bytes==new FileInfo(path).Length&&ScaleArtifacts.Hash(path)==completed.Sha256)
            {Console.Error.WriteLine("Reusing checksum-verified native index.");return completed;}
            throw new InvalidDataException("Completed native index identity/checksum mismatch. Keep these artifacts for diagnosis and choose a fresh artifacts root.");
        }
        ScaleArtifacts.RejectLinkedFile(path);
        ScaleGraphCheckpoint? checkpoint=null;
        NativeVectorIndex? builder=null;
        if(File.Exists(pointer))
        {
            checkpoint=ScaleArtifacts.Read<ScaleGraphCheckpoint>(pointer);
            if(checkpoint.Schema!=1||checkpoint.Owner!=ScaleArtifacts.Owner||checkpoint.DatasetSha256!=dataset.Sha256||checkpoint.NativeSha256!=nativeHash||checkpoint.Dimensions!=dataset.Dimensions||checkpoint.Settings!=Settings||checkpoint.BuildConcurrency!=buildConcurrency||checkpoint.BatchRows!=BatchRows)
                throw new InvalidDataException("Native graph checkpoint identity/settings mismatch; refusing to reuse or remove it.");
            foreach(var snapshot in new[]{checkpoint.Current,checkpoint.Previous}.Where(x=>x is not null))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string saved=SnapshotPath(directory,snapshot!);
                    if(snapshot!.Rows<1||snapshot.Rows>dataset.Count||snapshot.Bytes<=0||!double.IsFinite(snapshot.BuildMs)||snapshot.BuildMs<0||!File.Exists(saved)||new FileInfo(saved).Length!=snapshot.Bytes||ScaleArtifacts.Hash(saved)!=snapshot.Sha256)
                        throw new InvalidDataException("Native graph checkpoint checksum/size/count metadata mismatch.");
                    builder=NativeVectorIndex.OpenForUpdate(saved);
                    if(builder.Count!=snapshot.Rows||builder.Dimensions!=dataset.Dimensions)
                    {builder.Dispose();builder=null;throw new InvalidDataException("Native graph checkpoint native count/dimensions mismatch.");}
                    // If current is damaged, the verified previous graph becomes the only authoritative base.
                    checkpoint=checkpoint with{Current=snapshot,Previous=snapshot==checkpoint.Current?checkpoint.Previous:null};
                    Console.Error.WriteLine($"Resuming verified native graph at {snapshot.Rows:N0}/{dataset.Count:N0}; cumulative build {snapshot.BuildMs/1000:F1}s.");
                    break;
                }
                catch(Exception error)when(error is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
                {Console.Error.WriteLine($"Rejected native checkpoint {snapshot!.FileName}: {error.Message}");}
            }
            if(builder is null)throw new InvalidDataException("No valid current or previous native graph checkpoint remains. Existing artifacts were preserved.");
        }
        int rows=checkpoint?.Current.Rows??0;
        double previousElapsed=checkpoint?.Current.BuildMs??0;
        var timer=Stopwatch.StartNew();
        using(builder??=new NativeVectorIndex(dataset.Dimensions))
        using(var file=new FileStream(Path.Combine(directory,"vectors.f16"),FileMode.Open,FileAccess.Read,FileShare.Read,1<<20,FileOptions.SequentialScan))
        {
            file.Position=(long)rows*ScaleAlgorithms.BytesPerVector;
            var bytes=new byte[ScaleAlgorithms.BytesPerVector*BatchRows];var vectors=new float[ScaleAlgorithms.Dimensions*BatchRows];
            Console.Error.WriteLine($"Native graph build concurrency {buildConcurrency}; bounded batches ≤{BatchRows}; checkpoint interval {checkpointRows:N0}.");
            try
            {
                for(;rows<dataset.Count;)
                {
                    token.ThrowIfCancellationRequested();
                    int take=Math.Min(BatchRows,Math.Min(dataset.Count-rows,checkpointRows-rows%checkpointRows));
                    file.ReadExactly(bytes.AsSpan(0,take*ScaleAlgorithms.BytesPerVector));
                    for(int i=0;i<take;i++)ScaleAlgorithms.Decode(bytes.AsSpan(i*ScaleAlgorithms.BytesPerVector,ScaleAlgorithms.BytesPerVector),vectors.AsSpan(i*ScaleAlgorithms.Dimensions,ScaleAlgorithms.Dimensions));
                    // Failure joins every native worker and poisons this handle. Never checkpoint a partial batch.
                    builder.AddBatch((ulong)rows,vectors.AsMemory(0,take*ScaleAlgorithms.Dimensions),buildConcurrency,dataset.Count);rows+=take;
                    if(rows%checkpointRows==0) checkpoint=SaveCheckpoint(directory,dataset,nativeHash,builder,rows,previousElapsed,timer,checkpoint,buildConcurrency);
                    if(rows%10000<take)Console.Error.WriteLine($"Native index {rows:N0}/{dataset.Count:N0}; elapsed {(previousElapsed+timer.Elapsed.TotalMilliseconds)/1000:F1}s");
                    progress?.Invoke(rows);
                }
                token.ThrowIfCancellationRequested();
                if(checkpoint?.Current.Rows!=rows)checkpoint=SaveCheckpoint(directory,dataset,nativeHash,builder,rows,previousElapsed,timer,checkpoint,buildConcurrency);
            }
            catch(OperationCanceledException)
            {
                // A graceful stop preserves the extra inserted rows too. A hard kill loses at most the checkpoint interval.
                if(rows>0&&checkpoint?.Current.Rows!=rows)
                {
                    try{checkpoint=SaveCheckpoint(directory,dataset,nativeHash,builder,rows,previousElapsed,timer,checkpoint,buildConcurrency);}
                    catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {Console.Error.WriteLine($"Could not save final cancellation checkpoint; the prior snapshot remains authoritative: {error.Message}");}
                }
                throw;
            }
        }
        token.ThrowIfCancellationRequested();
        var final=checkpoint?.Current??throw new InvalidDataException("Native graph build did not produce a complete checkpoint.");
        if(final.Rows!=dataset.Count)throw new InvalidDataException("Native graph build count is incomplete.");
        string temporary=path+".partial";ScaleArtifacts.RejectLinkedFile(temporary);
        using(var input=new FileStream(SnapshotPath(directory,final),FileMode.Open,FileAccess.Read,FileShare.Read,1<<20,FileOptions.SequentialScan))
        using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,1<<20,FileOptions.SequentialScan))
        {input.CopyTo(output);output.Flush(true);}
        if(ScaleArtifacts.Hash(temporary)!=final.Sha256)throw new InvalidDataException("Final native index copy checksum mismatch; checkpoints were preserved.");
        File.Move(temporary,path,true);
        var result=new ScaleIndex(dataset.Sha256!,nativeHash,final.Bytes,final.Sha256,previousElapsed+timer.Elapsed.TotalMilliseconds,buildConcurrency,BatchRows);
        ScaleArtifacts.AtomicWrite(manifest,result);
        // Only after the final flushed index and manifest exist can resumable graphs be discarded.
        CleanupOwnedGraphs(directory,new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        try{File.Delete(pointer);}catch(IOException error){Console.Error.WriteLine($"Completed index retained; checkpoint pointer cleanup deferred: {error.Message}");}
        return result;
    }

    private static ScaleGraphCheckpoint SaveCheckpoint(string directory,ScaleDataset dataset,string nativeHash,
        NativeVectorIndex builder,int rows,double previousElapsed,Stopwatch timer,ScaleGraphCheckpoint? previous,int buildConcurrency)
    {
        if(builder.Count!=rows||builder.Dimensions!=dataset.Dimensions)throw new InvalidDataException("Builder count or dimensions differ from canonical progress.");
        string name=$"native-checkpoint-v1-{rows:D7}-{Guid.NewGuid():N}.usearch";
        string path=Path.Combine(directory,name);builder.Save(path);
        var snapshot=new ScaleGraphSnapshot(name,rows,new FileInfo(path).Length,ScaleArtifacts.Hash(path),previousElapsed+timer.Elapsed.TotalMilliseconds);
        var next=new ScaleGraphCheckpoint(1,ScaleArtifacts.Owner,dataset.Sha256!,nativeHash,dataset.Dimensions,Settings,snapshot,previous?.Current,buildConcurrency,BatchRows);
        ScaleArtifacts.AtomicWrite(Path.Combine(directory,CheckpointFileName),next);
        // The current and former current remain intact across pointer replacement. Unreferenced token graphs are owned debris.
        CleanupOwnedGraphs(directory,new[]{next.Current.FileName,next.Previous?.FileName}.Where(x=>x is not null).Select(x=>x!).ToHashSet(StringComparer.OrdinalIgnoreCase));
        Console.Error.WriteLine($"Native graph checkpoint durable at {rows:N0}/{dataset.Count:N0} ({snapshot.Bytes:N0} bytes).");
        return next;
    }

    public static string SnapshotPath(string directory,ScaleGraphSnapshot snapshot)
    {
        if(!OwnedGraph.IsMatch(snapshot.FileName))throw new InvalidDataException("Invalid owned native checkpoint filename.");
        string path=Path.Combine(directory,snapshot.FileName);ScaleArtifacts.RejectLinkedFile(path);return path;
    }

    private static void CleanupOwnedGraphs(string directory,IReadOnlySet<string> retained)
    {
        foreach(string path in Directory.EnumerateFiles(directory,"native-checkpoint-v1-*.usearch",SearchOption.TopDirectoryOnly))
        {
            string name=Path.GetFileName(path);
            if(!OwnedGraph.IsMatch(name)||retained.Contains(name))continue;
            try{ScaleArtifacts.RejectLinkedFile(path);File.Delete(path);}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException)
            {Console.Error.WriteLine($"Checkpoint cleanup deferred for {name}: {error.Message}");}
        }
    }
}
