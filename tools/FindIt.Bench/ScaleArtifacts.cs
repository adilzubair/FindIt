using System.Security.Cryptography;
using System.Text.Json;

namespace FindIt.Bench;

public sealed record ScaleDataset(int Schema, string Owner, int Count, int Seed, string Distribution, int Dimensions,
    int CommittedRows = 0, string? Sha256 = null);
public sealed record ScaleIndex(string DatasetSha256, string NativeSha256, long Bytes, string Sha256, double BuildMs,
    int BuildConcurrency = 1, int BuildBatchRows = 1);
public sealed record ScaleTruth(string DatasetSha256, int QueryCount, int QuerySeed, long ScannedRows,
    double ElapsedMs, ExactNeighbor[][][] Neighbors, int QueryOffset = 0, string? QueryFingerprint = null);

public static class ScaleArtifacts
{
    public const string Owner = "FindIt synthetic scale benchmark v1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string DatasetDirectory(string root, int count, int seed, string distribution)
    {
        root = Path.GetFullPath(root);
        // A benchmark cannot treat a drive/library as disposable storage. Only explicitly named artifacts trees qualify.
        if (!root.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => x.Equals("artifacts", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Scale storage must be beneath an artifacts directory, for example artifacts/scale. Archive roots are not allowed.");
        EnsureNoLinks(root);
        string directory = Path.Combine(root, $"scale-v1-{distribution}-{count}-{seed}");
        if (Directory.Exists(directory) && !File.Exists(Path.Combine(directory, "dataset.json")))
            throw new InvalidOperationException("Existing directory has no FindIt benchmark ownership marker; refusing to modify it.");
        Directory.CreateDirectory(directory);
        EnsureNoLinks(directory);
        return directory;
    }

    public static ScaleDataset Validate(string directory)
    {
        directory = Path.GetFullPath(directory);
        EnsureNoLinks(directory);
        var state = Read<ScaleDataset>(Path.Combine(directory, "dataset.json"));
        if (state.Owner != Owner || state.Schema != 1 || state.Dimensions != ScaleAlgorithms.Dimensions ||
            state.Count < 100 || state.Count > 2_000_000 || state.CommittedRows < 0 || state.CommittedRows > state.Count ||
            !ScaleAlgorithms.Distributions.Contains(state.Distribution)) throw new InvalidDataException("Invalid benchmark ownership or dataset schema.");
        return state;
    }

    public static void EnsureNoLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Benchmark artifacts cannot use directory links.");
    }

    public static void AtomicWrite<T>(string path, T value)
    {
        EnsureNoLinks(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".writing";
        RejectLinkedFile(path); RejectLinkedFile(temporary);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, value, Json); file.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    public static void RejectLinkedFile(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Benchmark artifacts cannot use file links.");
    }
    public static T Read<T>(string path)
    {
        RejectLinkedFile(path);
        using var file = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(file, Json) ?? throw new InvalidDataException($"Invalid benchmark metadata: {path}");
    }
    public static string Hash(string path)
    {
        RejectLinkedFile(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
}
