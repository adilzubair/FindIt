using System.Text.Json;
using FindIt.Shared;
using Microsoft.Data.Sqlite;

namespace FindIt.Core;

public sealed partial class ArchiveService
{
    private sealed record CheckpointResult(StageState State,GenerationManifest Manifest);

    /// <summary>Publish only committed files, while the writable reconciliation catalog remains private.</summary>
    private async Task<CheckpointResult> PublishCheckpointAsync(SqliteConnection live,StageState state,
        GenerationManifest? previous,PackWriter thumbnails,PackWriter vectors,
        Action<PreparationStage,string,string> report,CancellationToken cancellationToken,bool continueWriting)
    {
        // A reader may retain these packs after the next pointer switches. They can never be appended again.
        thumbnails.Seal();vectors.Seal();
        var folder=Path.Combine(storage.Home,"checkpoints","snapshot-"+Guid.NewGuid().ToString("N"));
        PortablePaths.RejectLinks(RootPath,folder);
        Directory.CreateDirectory(folder);
        try
        {
            // A prior publication can survive an interrupted pointer/state update. Never reuse its generation ID.
            while(Directory.Exists(storage.GenerationDirectory(state.GenerationId)))state=state with { GenerationId=checked(state.GenerationId+1) };
            GenerationManifest published;
            using(var snapshot=ArchiveDatabase.Open(Path.Combine(folder,"catalog.sqlite")))
            {
                live.BackupDatabase(snapshot);
                published=await PublishSnapshotAsync(snapshot,state,previous,report,cancellationToken,folder);
            }
            var suffix=Guid.NewGuid().ToString("N");
            var next=state with
            {
                GenerationId=checked(published.GenerationId+1),
                ThumbnailPack=$"FindIt/archive/packs/thumbnails-{suffix}.pack",
                VectorPack=$"FindIt/archive/packs/vectors-{suffix}.pack"
            };
            // Complete this tiny durable transition even if Pause arrived just after atomic publication.
            await ArchiveStorage.WriteAtomicAsync(Path.Combine(storage.StagePath,"state.json"),JsonSerializer.Serialize(next,WireJson.Options),CancellationToken.None);
            if(continueWriting){thumbnails.Rotate(next.ThumbnailPack);vectors.Rotate(next.VectorPack);}
            return new(next,published);
        }
        finally
        {
            // Successful publication moved the private folder. Failed attempts contain only app-owned artifacts.
            var resolved=Path.GetFullPath(folder);
            if(Directory.Exists(resolved)&&PortablePaths.IsWithin(Path.GetFullPath(Path.Combine(storage.Home,"checkpoints")),resolved))
            {
                try{PortablePaths.RejectLinks(RootPath,resolved);Directory.Delete(resolved,true);}
                catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
    }
}
