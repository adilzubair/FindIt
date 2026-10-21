using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FindIt.Shared;
using Microsoft.Data.Sqlite;

namespace FindIt.Core;

public sealed partial class ArchiveService : IArchiveBackend
{
    private const int Dimension = 512;
    private readonly ArchiveStorage storage;
    private readonly IEmbeddingProvider embeddings;
    private readonly IContentProcessor processor;
    private readonly SemaphoreSlim preparationGate = new(1, 1);
    private readonly SemaphoreSlim overlayGate = new(1, 1);
    private volatile bool preparing;
    public string RootPath => storage.Root;

    public ArchiveService(string rootPath, IEmbeddingProvider embeddingProvider, IContentProcessor contentProcessor)
    {
        storage = new ArchiveStorage(rootPath);
        embeddings = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
        processor = contentProcessor ?? throw new ArgumentNullException(nameof(contentProcessor));
    }

    public async Task<ArchiveStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var generation = await storage.RecoverAsync(cancellationToken);
        var capabilities = processor.Capabilities.Concat(new[] {
            new RuntimeCapability("keyword", "Filename and keyword search", true, "Available offline without an embedding model."),
            new RuntimeCapability("semantic", "Semantic search", embeddings.Available, embeddings.Available ? "Local query encoder is available." : "Install the verified local model package to enable semantic search.")
        }).GroupBy(c => c.Id).Select(g => g.Last()).ToArray();
        if (generation is null) return new ArchiveStatus { RootPath = RootPath, DisplayName = new DirectoryInfo(RootPath).Name, IsPreparing = preparing, Capabilities = capabilities, StatusDetail = storage.RecoveryNotice ?? "Choose folders to prepare this drive." };
        long peopleCount = 0;
        if (File.Exists(storage.OverlayPath))
        {
            PortablePaths.RejectLinks(RootPath, storage.OverlayPath);
            using var overlay = ArchiveDatabase.Open(storage.OverlayPath, true);
            peopleCount = Convert.ToInt64(ArchiveDatabase.Scalar(overlay, "SELECT COUNT(*) FROM people WHERE confirmed=1;"), CultureInfo.InvariantCulture);
        }
        bool semantic = generation.VectorCount > 0 && embeddings.Available && generation.EmbeddingFingerprint == embeddings.Fingerprint;
        return new ArchiveStatus {
            RootPath = RootPath, DisplayName = new DirectoryInfo(RootPath).Name, LibraryId = generation.LibraryId,
            GenerationId = generation.GenerationId, PreparedUtc = generation.PreparedUtc, IsPreparing = preparing,
            FileCount = generation.FileCount, SegmentCount = generation.SegmentCount, TotalBytes = generation.TotalBytes,
            PeopleCount = peopleCount, Kinds = generation.Kinds, Folders = generation.Folders,
            Issues = generation.Issues, SemanticAvailable = semantic, Capabilities = capabilities,
            StatusDetail = storage.RecoveryNotice ?? ((semantic ? "Prepared for offline search." : generation.VectorCount == 0 ? "Keyword search is ready. Semantic indexing requires the local model package." : "Keyword search is ready. The matching query encoder is unavailable.") + (peopleCount > 1000 ? " The People browser displays the first 1,000 groups; narrower people paging is not yet available." : ""))
        };
    }

    public async Task<ArchiveStatus> PrepareAsync(PreparationRequest request, IProgress<PreparationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await preparationGate.WaitAsync(cancellationToken);
        Guid jobId = Guid.NewGuid(); long discovered = 0, completed = 0, unchanged = 0, failed = 0, segmentCount = 0;
        void Report(PreparationStage stage, string path, string message) => progress?.Report(new PreparationProgress { JobId = jobId, Stage = stage, Discovered = discovered, Completed = completed, Unchanged = unchanged, Failed = failed, Segments = segmentCount, CurrentPath = path, Message = message });
        try
        {
            preparing = true; nextObservationId = reservedObservationEnd = 0; storage.EnsureWritable();
            await using var writerLock = new FileStream(Path.Combine(storage.Home, "preparation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var previous = await storage.RecoverAsync(cancellationToken);
            if (previous is not null && request.VerifyContents) await storage.VerifyAllAsync(previous, cancellationToken);
            var scopes = NormalizeScopes(request.Folders);
            string extractionFingerprint = ArchiveStorage.HashText(JsonSerializer.Serialize(new { version = 2, face = FaceFingerprint, capabilities = processor.Capabilities.Where(c=>c.Id!="gpu").OrderBy(c => c.Id).Select(c=>new { c.Id,c.Available }).ToArray(), languages = request.OcrLanguages.Order().ToArray() }, WireJson.Options));
            string settings = ArchiveStorage.HashText(JsonSerializer.Serialize(new { folders = scopes, request.VerifyContents, extractionFingerprint }, WireJson.Options));
            StageState? state = null;
            var statePath = Path.Combine(storage.StagePath, "state.json");
            if (File.Exists(statePath))
            {
                PortablePaths.RejectLinks(RootPath, statePath);
                try { state = JsonSerializer.Deserialize<StageState>(await File.ReadAllTextAsync(statePath, cancellationToken), WireJson.Options); }
                catch (JsonException) { }
            }
            if (state is null || state.SettingsFingerprint != settings || state.EmbeddingFingerprint != embeddings.Fingerprint || (previous is not null && state.GenerationId <= previous.GenerationId))
            {
                if (Directory.Exists(storage.StagePath))
                {
                    PortablePaths.RejectLinks(RootPath, storage.StagePath);
                    Directory.Move(storage.StagePath, Path.Combine(storage.Home, "interrupted-" + Guid.NewGuid().ToString("N")));
                }
                Directory.CreateDirectory(storage.StagePath);
                var existing = Directory.EnumerateDirectories(Path.Combine(storage.Home, "generations")).Select(p => long.TryParse(Path.GetFileName(p), out var id) ? id : 0).DefaultIfEmpty().Max();
                state = new StageState(previous?.LibraryId ?? Guid.NewGuid(), existing + 1, settings, embeddings.Fingerprint, $"FindIt/archive/packs/thumbnails-{jobId:N}.pack", $"FindIt/archive/packs/vectors-{jobId:N}.pack");
                using var stage = ArchiveDatabase.Open(Path.Combine(storage.StagePath, "catalog.sqlite"));
                if (previous is not null) { using var source = ArchiveDatabase.Open(storage.CatalogPath(previous), true); source.BackupDatabase(stage); }
                ArchiveDatabase.InitializeCatalog(stage);
                ArchiveDatabase.Execute(stage, "UPDATE files SET seen=1; DELETE FROM completed; DELETE FROM issues;");
                foreach (var scope in scopes)
                    ArchiveDatabase.Execute(stage, "UPDATE files SET seen=0 WHERE $root=1 OR path=$path COLLATE NOCASE OR path LIKE $prefix ESCAPE '\\';", ("$root", scope == "." ? 1 : 0), ("$path", scope), ("$prefix", EscapeLike(scope) + "/%"));
                // Embeddings from another encoder are never compared to this query model.
                if (previous is not null && previous.EmbeddingFingerprint != embeddings.Fingerprint)
                    ArchiveDatabase.Execute(stage, "UPDATE segments SET vector_path=NULL,vector_offset=NULL,vector_length=NULL;");
                if (previous is not null && previous.FaceFingerprint != FaceFingerprint)
                    ArchiveDatabase.Execute(stage, "DELETE FROM observations; DELETE FROM completed; UPDATE files SET hash='';");
                await ArchiveStorage.WriteAtomicAsync(statePath, JsonSerializer.Serialize(state, WireJson.Options), cancellationToken);
            }
            using var db = ArchiveDatabase.Open(Path.Combine(storage.StagePath, "catalog.sqlite"));
            ArchiveDatabase.InitializeCatalog(db);
            using (var thumbnails = new PackWriter(RootPath, state.ThumbnailPack))
            using (var vectors = new PackWriter(RootPath, state.VectorPack))
            {
                long checkpointedCompleted=0;
                long checkpointTime=Stopwatch.GetTimestamp();
                async Task CheckpointIfDueAsync(bool pause=false)
                {
                    if(completed<=checkpointedCompleted)return;
                    bool due=pause||previous is null||completed-checkpointedCompleted>=100||Stopwatch.GetElapsedTime(checkpointTime)>=TimeSpan.FromMinutes(5);
                    if(!due)return;
                    using var pauseBudget=pause?new CancellationTokenSource(TimeSpan.FromSeconds(10)):null;
                    var published=await PublishCheckpointAsync(db,state!,previous,thumbnails,vectors,Report,pauseBudget?.Token??cancellationToken,!pause);
                    state=published.State;previous=published.Manifest;checkpointedCompleted=completed;checkpointTime=Stopwatch.GetTimestamp();
                    Report(PreparationStage.Extracting,"",pause?"Completed files are searchable while preparation is paused.":"Completed files are searchable. Preparation continues.");
                }
                try
                {
                foreach (var absolute in EnumerateOriginals(scopes, (path, message) => {
                    failed++; AddIssue(db, path, "Discovering", message);
                    ArchiveDatabase.Execute(db, "UPDATE files SET seen=1 WHERE path=$path COLLATE NOCASE OR path LIKE $prefix ESCAPE '\\';", ("$path", path), ("$prefix", EscapeLike(path) + "/%"));
                }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ArchiveSpace.EnsureAvailable(RootPath);
                    string relative = PortablePaths.Relative(RootPath, absolute); discovered++;
                    Report(PreparationStage.Discovering, relative, "Preparing originals without changing them.");
                    try
                    {
                        PortablePaths.RejectLinks(RootPath, absolute);
                        var info = new FileInfo(absolute); long bytes = info.Length; string modified = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);
                        string? oldId = null, oldModified = null, oldHash = null, oldProcessor = null; long oldBytes = -1;
                        using (var command = db.CreateCommand())
                        {
                            command.CommandText = "SELECT id,bytes,modified,hash,processor_fingerprint FROM files WHERE path=$path COLLATE NOCASE;"; command.Parameters.AddWithValue("$path", relative);
                            using var reader = command.ExecuteReader(); if (reader.Read()) { oldId = reader.GetString(0); oldBytes = reader.GetInt64(1); oldModified = reader.GetString(2); oldHash = reader.GetString(3); oldProcessor = reader.GetString(4); }
                        }
                        bool alreadyCompleted = ArchiveDatabase.Scalar(db, "SELECT 1 FROM completed WHERE path=$path;", ("$path", relative)) is not null;
                        bool matches = oldId is not null && oldBytes == bytes && oldModified == modified && oldProcessor == extractionFingerprint;
                        string hash = request.VerifyContents ? await ArchiveStorage.HashFileAsync(absolute, cancellationToken) : "";
                        if (matches && request.VerifyContents) matches = oldHash == hash;
                        bool needsEmbeddings = embeddings.Available && ArchiveDatabase.Scalar(db, "SELECT 1 FROM segments WHERE file_id=$id AND vector_path IS NULL AND skip_embedding=0 AND length(text)>0 LIMIT 1;", ("$id", oldId)) is not null;
                        if (matches && !needsEmbeddings)
                        {
                            ArchiveDatabase.Execute(db, "UPDATE files SET seen=1 WHERE id=$id; INSERT OR IGNORE INTO completed(path) VALUES($path);", ("$id", oldId), ("$path", relative));
                            completed++; unchanged++; Report(PreparationStage.Extracting, relative, alreadyCompleted ? "Resumed a completed item." : "Unchanged item reused.");
                        }
                        else
                        {
                        var fileId = oldId ?? Guid.NewGuid().ToString("D");
                        long segmentsBeforeFile=segmentCount;
                        long thumbnailMark=thumbnails.Position,vectorMark=vectors.Position;
                        ArchiveDatabase.Execute(db, "BEGIN IMMEDIATE;");
                        try
                        {
                            ArchiveDatabase.Execute(db, "DELETE FROM search_fts WHERE file_id=$id; DELETE FROM segments WHERE file_id=$id; INSERT INTO files(id,path,kind,bytes,modified,hash,seen,processor_fingerprint) VALUES($id,$path,$kind,$bytes,$modified,$hash,1,$processor) ON CONFLICT(id) DO UPDATE SET path=excluded.path,kind=excluded.kind,bytes=excluded.bytes,modified=excluded.modified,hash=excluded.hash,seen=1,processor_fingerprint=excluded.processor_fingerprint;",
                                ("$id", fileId), ("$path", relative), ("$kind", (int)Classify(relative)), ("$bytes", bytes), ("$modified", modified), ("$hash", hash), ("$processor", extractionFingerprint));
                            int emitted = 0;
                            await foreach (var segment in processor.ExtractAsync(absolute, Classify(relative), request, cancellationToken).WithCancellation(cancellationToken))
                            {
                                if(emitted%64==0)ArchiveSpace.EnsureAvailable(RootPath,(segment.ThumbnailJpeg?.LongLength??0)+Dimension*2L+segment.Faces.Sum(f=>f.PortraitJpeg?.LongLength??0));
                                Report(PreparationStage.Extracting, relative, "Extracting searchable content and previews.");
                                var vector = segment.Embedding;
                                if (vector is null && !segment.SkipTextEmbedding && embeddings.Available && !string.IsNullOrWhiteSpace(segment.Text))
                                { Report(PreparationStage.Embedding, relative, "Creating local semantic embeddings."); vector = await embeddings.EmbedTextAsync(segment.Text.Length > 24000 ? segment.Text[..24000] : segment.Text, false, cancellationToken); }
                                var encoded = VectorEncoding.Encode(vector, Dimension);
                                BlobReference? vectorReference = encoded is null ? null : vectors.Append(encoded);
                                BlobReference? thumbnail = segment.ThumbnailJpeg is { Length: > 0 } ? thumbnails.Append(segment.ThumbnailJpeg) : null;
                                InsertSegment(db, fileId, relative, segment, thumbnail, vectorReference, thumbnails);
                                emitted++; segmentCount++;
                            }
                            if (emitted == 0) { InsertSegment(db, fileId, relative, new ExtractedSegment { Kind = SegmentKind.Text }, null, null, thumbnails); segmentCount++; }
                            info.Refresh();
                            if (!info.Exists || info.Length != bytes || info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture) != modified) throw new IOException("The original changed while it was being prepared; it will be retried.");
                            // Flush packed payloads before committing their references.
                            thumbnails.Flush(); vectors.Flush();
                            ArchiveDatabase.Execute(db, "INSERT OR REPLACE INTO completed(path) VALUES($path);", ("$path", relative));
                            ArchiveDatabase.Execute(db, "COMMIT;"); completed++;
                        }
                        catch
                        {
                            try { ArchiveDatabase.Execute(db,"ROLLBACK;"); }
                            finally
                            {
                                segmentCount=segmentsBeforeFile;
                                // Only this writable tail belongs to the interrupted transaction.
                                // Sealed packs from published snapshots are never opened here.
                                try { thumbnails.Truncate(thumbnailMark); }
                                catch(Exception cleanupError) when(cleanupError is IOException or UnauthorizedAccessException or InvalidOperationException) { }
                                try { vectors.Truncate(vectorMark); }
                                catch(Exception cleanupError) when(cleanupError is IOException or UnauthorizedAccessException or InvalidOperationException) { }
                            }
                            throw;
                        }
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException && error is not ArchiveSpaceException)
                    {
                        failed++; AddIssue(db, relative, "Extracting", error.Message);
                        // Preserve previously prepared content for a failed update, and report it explicitly.
                        ArchiveDatabase.Execute(db, "UPDATE files SET seen=1 WHERE path=$path COLLATE NOCASE;", ("$path", relative));
                        if (ArchiveDatabase.Scalar(db, "SELECT 1 FROM files WHERE path=$path COLLATE NOCASE;", ("$path", relative)) is null)
                        {
                            // Decoder failures must not hide the original's filename from offline search.
                            try
                            {
                                PortablePaths.RejectLinks(RootPath, absolute); var fallback = new FileInfo(absolute);
                                if (fallback.Exists)
                                {
                                    string fallbackId = Guid.NewGuid().ToString("D");
                                    ArchiveDatabase.Execute(db, "INSERT INTO files(id,path,kind,bytes,modified,seen) VALUES($id,$path,$kind,$bytes,$modified,1);", ("$id", fallbackId), ("$path", relative), ("$kind", (int)Classify(relative)), ("$bytes", fallback.Length), ("$modified", fallback.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)));
                                    InsertSegment(db, fallbackId, relative, new ExtractedSegment { Kind = SegmentKind.Text }, null, null, thumbnails);
                                    segmentCount++;
                                }
                            }
                            catch (Exception fallbackError) when (fallbackError is IOException or UnauthorizedAccessException) { }
                        }
                        Report(PreparationStage.Extracting, relative, "This item needs attention; other items continue.");
                    }
                    await CheckpointIfDueAsync();
                }
                cancellationToken.ThrowIfCancellationRequested();
                thumbnails.Flush(); vectors.Flush();
                }
                catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
                {
                    try{await CheckpointIfDueAsync(pause:true);}
                    catch(Exception checkpointError) when(checkpointError is IOException or UnauthorizedAccessException or SqliteException or OperationCanceledException)
                    {Report(PreparationStage.Paused,"","Completed files remain durable in staging; the last published search snapshot is preserved.");}
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            ArchiveDatabase.Execute(db, "DELETE FROM files WHERE seen=0; DELETE FROM search_fts WHERE file_id NOT IN (SELECT id FROM files); DELETE FROM completed;");
            await PublishSnapshotAsync(db, state, previous, Report, cancellationToken);
            Report(PreparationStage.Completed, "", failed == 0 ? "This drive is prepared for offline search." : $"Preparation completed with {failed} item(s) needing attention.");
            preparing = false;
            return await GetStatusAsync(cancellationToken);
        }
        catch (OperationCanceledException) { Report(PreparationStage.Paused, "", "Preparation is paused. Completed items will be reused on resume."); throw; }
        catch (Exception error) { Report(PreparationStage.Failed, "", error.Message); throw; }
        finally { preparing = false; preparationGate.Release(); }
    }

    private void InsertSegment(SqliteConnection db, string fileId, string relative, ExtractedSegment segment, BlobReference? thumbnail, BlobReference? vector, PackWriter thumbnails)
    {
        ArchiveDatabase.Execute(db, "INSERT INTO segments(file_id,kind,text,start_ms,end_ms,page,thumbnail,vector_path,vector_offset,vector_length,skip_embedding) VALUES($file,$kind,$text,$start,$end,$page,$thumb,$vp,$vo,$vl,$skip);",
            ("$file", fileId), ("$kind", (int)segment.Kind), ("$text", segment.Text), ("$start", segment.StartMs), ("$end", segment.EndMs), ("$page", segment.Page), ("$thumb", Json(thumbnail)), ("$vp", vector?.RelativePath), ("$vo", vector?.Offset), ("$vl", vector?.Length), ("$skip", segment.SkipTextEmbedding ? 1 : 0));
        long id = Convert.ToInt64(ArchiveDatabase.Scalar(db, "SELECT last_insert_rowid();"), CultureInfo.InvariantCulture);
        ArchiveDatabase.Execute(db, "INSERT INTO search_fts(file_id,segment_id,name,path,text) VALUES($file,$segment,$name,$path,$text);", ("$file", fileId), ("$segment", id), ("$name", Path.GetFileName(relative)), ("$path", relative), ("$text", segment.Text));
        foreach (var face in segment.Faces.Where(f => f.Quality >= 0.25 && f.Width > 0 && f.Height > 0))
        {
            var encoded = VectorEncoding.Encode(face.Embedding, face.Embedding.Length); if (encoded is null) continue;
            var portrait = face.PortraitJpeg is { Length: > 0 } ? thumbnails.Append(face.PortraitJpeg) : thumbnail;
            ArchiveDatabase.Execute(db, "INSERT INTO observations(id,segment_id,embedding,x,y,width,height,quality,portrait) VALUES($id,$segment,$embedding,$x,$y,$width,$height,$quality,$portrait);", ("$id", ReserveObservationId(db)), ("$segment", id), ("$embedding", encoded), ("$x", face.X), ("$y", face.Y), ("$width", face.Width), ("$height", face.Height), ("$quality", face.Quality), ("$portrait", Json(portrait)));
        }
    }

    private string[] NormalizeScopes(IReadOnlyList<string> requested)
    {
        var selected = (requested.Count == 0 ? new[] { RootPath } : requested).Select(p => Path.IsPathRooted(p) ? PortablePaths.Relative(RootPath, p) : p.Replace('\\', '/').Trim('/')).Select(p => string.IsNullOrEmpty(p) ? "." : p).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ToArray();
        var scopes = new List<string>();
        foreach (var relative in selected)
        {
            var path = relative == "." ? RootPath : PortablePaths.Resolve(RootPath, relative);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Select an existing archive folder.");
            PortablePaths.RejectLinks(RootPath, path);
            if (scopes.Any(parent => parent == "." || relative.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase))) continue;
            scopes.Add(relative);
        }
        return scopes.ToArray();
    }

    private IEnumerable<string> EnumerateOriginals(string[] scopes, Action<string, string> issue)
    {
        foreach (var scope in scopes)
        {
            var pending = new Stack<string>(); pending.Push(scope == "." ? RootPath : PortablePaths.Resolve(RootPath, scope));
            while (pending.TryPop(out var folder))
            {
                string[] children;
                try { PortablePaths.RejectLinks(RootPath, folder); children = Directory.GetFileSystemEntries(folder); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { issue(PortablePaths.Relative(RootPath, folder), error.Message); continue; }
                foreach (var child in children)
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(child); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { issue(PortablePaths.Relative(RootPath, child), error.Message); continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { issue(PortablePaths.Relative(RootPath, child), "Linked files and junctions were skipped."); continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var name = Path.GetFileName(child);
                        if (!(string.Equals(folder, RootPath, StringComparison.OrdinalIgnoreCase) && name.Equals("FindIt", StringComparison.OrdinalIgnoreCase)) && !name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) && !name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) pending.Push(child);
                    }
                    else if (!(string.Equals(folder, RootPath, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(child).Equals("FindIt.exe", StringComparison.OrdinalIgnoreCase))) yield return child;
                }
            }
        }
    }
    public static FileKind Classify(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".heic" or ".heif" or ".avif" or ".webp" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".dng" or ".raw" or ".cr2" or ".cr3" or ".nef" or ".arw" => FileKind.Image,
        ".mp4" or ".mov" or ".mkv" or ".avi" or ".m4v" or ".wmv" or ".webm" or ".mts" or ".m2ts" or ".mpg" or ".mpeg" => FileKind.Video,
        ".pdf" or ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".html" or ".htm" or ".log" or ".docx" or ".pptx" or ".xlsx" or ".rtf" or ".doc" or ".ppt" or ".xls" or ".odt" => FileKind.Document,
        ".mp3" or ".m4a" or ".wav" or ".flac" or ".ogg" or ".aac" or ".wma" => FileKind.Audio,
        _ => FileKind.Other
    };
    private static string? Json(BlobReference? reference) => reference is null ? null : JsonSerializer.Serialize(reference, WireJson.Options);
    private static BlobReference? Blob(string? json) => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<BlobReference>(json, WireJson.Options);
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    private static void AddIssue(SqliteConnection db, string path, string stage, string message) => ArchiveDatabase.Execute(db, "INSERT INTO issues(path,stage,message) VALUES($path,$stage,$message);", ("$path", path), ("$stage", stage), ("$message", message.Length > 2000 ? message[..2000] : message));
    private long nextObservationId, reservedObservationEnd;
    private long ReserveObservationId(SqliteConnection catalog)
    {
        if (nextObservationId < reservedObservationEnd) return ++nextObservationId;
        overlayGate.Wait();
        try
        {
            using var overlay = ArchiveDatabase.Open(storage.OverlayPath); ArchiveDatabase.InitializeOverlay(overlay);
            ArchiveDatabase.Execute(overlay, "BEGIN IMMEDIATE;");
            try
            {
                long sequence = long.Parse(ArchiveDatabase.Scalar(overlay, "SELECT value FROM settings WHERE key='observation_sequence';")!.ToString()!, CultureInfo.InvariantCulture);
                long existing = Convert.ToInt64(ArchiveDatabase.Scalar(catalog, "SELECT COALESCE(MAX(id),0) FROM observations;"), CultureInfo.InvariantCulture);
                nextObservationId = Math.Max(sequence, existing); reservedObservationEnd = checked(nextObservationId + 1024);
                ArchiveDatabase.Execute(overlay, "UPDATE settings SET value=$value WHERE key='observation_sequence'; COMMIT;", ("$value", reservedObservationEnd.ToString(CultureInfo.InvariantCulture)));
                return ++nextObservationId;
            }
            catch { ArchiveDatabase.Execute(overlay, "ROLLBACK;"); throw; }
        }
        finally { overlayGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        nativeIndex?.Dispose(); nativeDeltaIndex?.Dispose(); exactIndex?.Dispose();
        await embeddings.DisposeAsync(); await processor.DisposeAsync(); preparationGate.Dispose(); overlayGate.Dispose(); searchGate.Dispose();
    }

    private void BuildNativeIndex(string recordsPath, IReadOnlyList<string> packs, string destination, CancellationToken cancellationToken, IReadOnlySet<string>? excludedPacks = null)
    {
        using var index = new FindIt.Runtime.NativeVectorIndex(Dimension);
        var streams = new Dictionary<int, FileStream>();
        try
        {
            using var records = new FileStream(recordsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            var record = new byte[20]; var vectorBytes = new byte[Dimension * 2];
            while (records.Position < records.Length)
            {
                cancellationToken.ThrowIfCancellationRequested(); records.ReadExactly(record);
                long key = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(0, 8));
                int pack = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8, 4));
                long offset = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(12, 8));
                if (excludedPacks?.Contains(packs[pack]) == true) continue;
                if (!streams.TryGetValue(pack, out var stream))
                {
                    stream = new FileStream(PortablePaths.Resolve(RootPath, packs[pack], true), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                    streams.Add(pack, stream);
                }
                stream.Position = offset; stream.ReadExactly(vectorBytes);
                index.Add((ulong)key, VectorEncoding.Decode(vectorBytes));
            }
            index.Save(destination);
        }
        finally { foreach (var stream in streams.Values) stream.Dispose(); }
    }
}
