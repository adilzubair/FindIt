using System.Text.Json;
using System.Text.Json.Serialization;

namespace FindIt.Shared;

public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

public enum FileKind { Image, Video, Document, Audio, Other }
public enum SegmentKind { Image, VideoFrame, VideoClip, DocumentPage, Text, Transcript, Audio }
public enum PreparationStage { Discovering, Extracting, Embedding, RecognizingPeople, Transcribing, Publishing, Completed, Paused, Failed }
public enum MatchCategory { Exact, Semantic, PossiblePersonAction, Browse }

public sealed record BlobReference(string RelativePath, long Offset, int Length, int Width = 0, int Height = 0);
public sealed record KindCount(FileKind Kind, long Count);
public sealed record FolderEntry(string RelativePath, long Count);
public sealed record ArchiveIssue(string RelativePath, string Stage, string Message);

public sealed record ArchiveStatus
{
    public string RootPath { get; init; } = "";
    public string DisplayName { get; init; } = "Your archive";
    public Guid LibraryId { get; init; }
    public long GenerationId { get; init; }
    public long FileCount { get; init; }
    public long SegmentCount { get; init; }
    public long PeopleCount { get; init; }
    public long TotalBytes { get; init; }
    public DateTimeOffset? PreparedUtc { get; init; }
    public bool Connected { get; init; } = true;
    public bool IsPreparing { get; init; }
    public bool SemanticAvailable { get; init; }
    public string StatusDetail { get; init; } = "Choose folders to prepare this drive.";
    public IReadOnlyList<KindCount> Kinds { get; init; } = [];
    public IReadOnlyList<FolderEntry> Folders { get; init; } = [];
    public IReadOnlyList<ArchiveIssue> Issues { get; init; } = [];
    public IReadOnlyList<RuntimeCapability> Capabilities { get; init; } = [];
}

public sealed record RuntimeCapability(string Id, string Name, bool Available, string Detail);

public sealed record SearchFilters
{
    public FileKind? Kind { get; init; }
    public string? Folder { get; init; }
    public DateTimeOffset? AfterUtc { get; init; }
    public DateTimeOffset? BeforeUtc { get; init; }
    public IReadOnlyList<Guid> PersonIds { get; init; } = [];
    public bool ExactOnly { get; init; }
}

public sealed record SearchRequest
{
    public string Query { get; init; } = "";
    public SearchFilters Filters { get; init; } = new();
    public int Limit { get; init; } = 256;
    public string? Cursor { get; init; }
    public long? GenerationId { get; init; }
}

public sealed record SearchMoment
{
    public long SegmentId { get; init; }
    public SegmentKind Kind { get; init; }
    public long? StartMs { get; init; }
    public long? EndMs { get; init; }
    public int? Page { get; init; }
    public string Label { get; init; } = "";
    public string Text { get; init; } = "";
    public BlobReference? Thumbnail { get; init; }
}

public sealed record SearchResult
{
    public Guid FileId { get; init; }
    public string RelativePath { get; init; } = "";
    public string Name { get; init; } = "";
    public FileKind Kind { get; init; }
    public long Bytes { get; init; }
    public DateTimeOffset ModifiedUtc { get; init; }
    public BlobReference? Thumbnail { get; init; }
    public string Snippet { get; init; } = "";
    public MatchCategory MatchCategory { get; init; }
    public double Score { get; init; }
    public IReadOnlyList<SearchMoment> Moments { get; init; } = [];
    public IReadOnlyList<string> MatchingPeople { get; init; } = [];
}

public sealed record SearchResponse
{
    public long GenerationId { get; init; }
    public IReadOnlyList<SearchResult> Results { get; init; } = [];
    public string? NextCursor { get; init; }
    public long? TotalCount { get; init; }
    public bool SemanticUsed { get; init; }
    public string Notice { get; init; } = "";
    public double ElapsedMilliseconds { get; init; }
    public IReadOnlyList<PersonChip> People { get; init; } = [];
}

public sealed record PersonChip(Guid PersonId, string Name);

public sealed record PreparationRequest
{
    public IReadOnlyList<string> Folders { get; init; } = [];
    public bool VerifyContents { get; init; }
    public IReadOnlyList<string> OcrLanguages { get; init; } = ["eng"];
}

public sealed record PreparationProgress
{
    public Guid JobId { get; init; }
    public PreparationStage Stage { get; init; }
    public long Discovered { get; init; }
    public long Completed { get; init; }
    public long Unchanged { get; init; }
    public long Failed { get; init; }
    public long Segments { get; init; }
    public string CurrentPath { get; init; } = "";
    public string Message { get; init; } = "";
    public double? Fraction { get; init; }
}

public sealed record PersonRecord
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public long ObservationCount { get; init; }
    public BlobReference? Portrait { get; init; }
    public bool Confirmed { get; init; }
}

public sealed record PersonUpdateRequest
{
    public Guid? PersonId { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public IReadOnlyList<string> ReferencePaths { get; init; } = [];
    public IReadOnlyList<long> ConfirmObservationIds { get; init; } = [];
    public IReadOnlyList<long> RejectObservationIds { get; init; } = [];
    public Guid? MergeIntoPersonId { get; init; }
}

public sealed record PersonObservation
{
    public long Id { get; init; }
    public Guid FileId { get; init; }
    public string RelativePath { get; init; } = "";
    public BlobReference? Thumbnail { get; init; }
    public long? StartMs { get; init; }
    public double Quality { get; init; }
    public bool Confirmed { get; init; }
    public bool Rejected { get; init; }
}

public sealed record PreviewRequest(Guid FileId, long? SegmentId = null);
public sealed record PlaybackRequest(string Path, long WindowHandle, long StartMs = 0, bool AutoPlay = true);
public sealed record PlaybackState(bool Playing, long PositionMs, long DurationMs);
public sealed record WorkerDiagnostics(int ProcessId, long PrivateBytes, long WorkingSetBytes, long PeakWorkingSetBytes, double CpuMilliseconds);
public sealed record PreviewResult
{
    public Guid FileId { get; init; }
    public string OriginalPath { get; init; } = "";
    public FileKind Kind { get; init; }
    public string? PreviewPath { get; init; }
    public string Text { get; init; } = "";
    public long? StartMs { get; init; }
    public int? Page { get; init; }
    public IReadOnlyList<SearchMoment> Moments { get; init; } = [];
}

public interface IArchiveBackend : IAsyncDisposable
{
    string RootPath { get; }
    Task<ArchiveStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
    Task<ArchiveStatus> PrepareAsync(PreparationRequest request, IProgress<PreparationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PersonRecord>> GetPeopleAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PersonObservation>> GetPersonObservationsAsync(Guid personId, CancellationToken cancellationToken = default);
    Task<PersonRecord> SavePersonAsync(PersonUpdateRequest request, CancellationToken cancellationToken = default);
    Task<PreviewResult> GetPreviewAsync(PreviewRequest request, CancellationToken cancellationToken = default);
}

public sealed record ExtractedSegment
{
    public bool SkipTextEmbedding { get; init; }
    public SegmentKind Kind { get; init; }
    public string Text { get; init; } = "";
    public long? StartMs { get; init; }
    public long? EndMs { get; init; }
    public int? Page { get; init; }
    public byte[]? ThumbnailJpeg { get; init; }
    public float[]? Embedding { get; init; }
    public IReadOnlyList<DetectedFace> Faces { get; init; } = [];
}

public sealed record DetectedFace(float[] Embedding, float X, float Y, float Width, float Height, double Quality, byte[]? PortraitJpeg = null);

public interface IEmbeddingProvider : IAsyncDisposable
{
    bool Available { get; }
    string Fingerprint { get; }
    Task<float[]?> EmbedTextAsync(string text, bool query, CancellationToken cancellationToken = default);
}

public interface IContentProcessor : IAsyncDisposable
{
    IReadOnlyList<RuntimeCapability> Capabilities { get; }
    string FaceFingerprint { get; }
    IAsyncEnumerable<ExtractedSegment> ExtractAsync(string absolutePath, FileKind kind, PreparationRequest options, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DetectedFace>> DetectReferenceFacesAsync(string absolutePath, CancellationToken cancellationToken = default);
}
