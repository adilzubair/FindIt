using FindIt.Shared;

namespace FindIt.Core;

public static class BlobStore
{
    public static async Task<byte[]> ReadAsync(string rootPath, BlobReference reference, CancellationToken cancellationToken = default)
    {
        if (reference.Offset < 0 || reference.Length < 0 || reference.Length > 64 * 1024 * 1024) throw new IOException("Invalid preview reference.");
        var path = PortablePaths.Resolve(rootPath, reference.RelativePath, allowMetadata: true);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (reference.Offset > stream.Length - reference.Length) throw new IOException("Preview data is incomplete.");
        stream.Position = reference.Offset;
        var bytes = new byte[reference.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }
}

internal sealed class PackWriter : IDisposable
{
    private FileStream? stream;
    private readonly string root;
    public string RelativePath { get; private set; } = "";
    public long Position => stream?.Position ?? throw new InvalidOperationException("The published pack is sealed.");
    public PackWriter(string root, string relative)
    {
        this.root = root;
        Rotate(relative);
    }
    public void Rotate(string relative)
    {
        Seal();
        RelativePath = relative;
        var path = PortablePaths.Resolve(root, relative, true, false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan);
        stream.Seek(0, SeekOrigin.End);
    }
    public BlobReference Append(ReadOnlySpan<byte> bytes)
    {
        if (stream is null) throw new InvalidOperationException("The published pack is sealed.");
        var reference = new BlobReference(RelativePath, stream.Position, bytes.Length);
        stream.Write(bytes);
        return reference;
    }
    public void Flush() => stream?.Flush(true);
    public void Truncate(long position)
    {
        if(stream is null)throw new InvalidOperationException("The published pack is sealed.");
        if(position<0||position>stream.Position)throw new ArgumentOutOfRangeException(nameof(position));
        stream.SetLength(position);stream.Position=position;stream.Flush(true);
    }
    public void Seal() { if (stream is null) return; stream.Flush(true); stream.Dispose(); stream = null; }
    public void Dispose() => Seal();
}
