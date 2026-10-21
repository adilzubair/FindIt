using System.Buffers.Binary;
using System.Text;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public class TransportTests
{
    [TestMethod]
    public async Task FramingHandlesFragmentedUnicodeMessagesAndCleanEof()
    {
        using var storage = new MemoryStream();
        using var writes = new SemaphoreSlim(1);
        await FramedTransport.WriteAsync(storage, new RpcRequest("2.0", "request", "search", new { query = "رحلة Sam 🌲" }), writes);
        storage.Position = 0;
        using var fragmented = new FragmentedStream(storage);
        using var result = await FramedTransport.ReadAsync(fragmented);
        Assert.IsNotNull(result);
        Assert.AreEqual("رحلة Sam 🌲", result.RootElement.GetProperty("params").GetProperty("query").GetString());
        Assert.IsNull(await FramedTransport.ReadAsync(fragmented));
    }

    [TestMethod]
    public async Task OversizedAndTruncatedMessagesAreRejected()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, FramedTransport.MaximumFrameBytes + 1);
        using var oversized = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => FramedTransport.ReadAsync(oversized));
        BinaryPrimitives.WriteInt32LittleEndian(header, 1024);
        using var truncated = new MemoryStream(header);
        await Assert.ThrowsAsync<EndOfStreamException>(() => FramedTransport.ReadAsync(truncated));
    }

    [TestMethod]
    public async Task ConcurrentFramesNeverInterleave()
    {
        using var storage = new MemoryStream(); using var writes = new SemaphoreSlim(1);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => FramedTransport.WriteAsync(storage, new { id = i, text = new string('x', 500) }, writes)));
        storage.Position = 0; var seen = new HashSet<int>();
        while (await FramedTransport.ReadAsync(storage) is { } frame) { using (frame) seen.Add(frame.RootElement.GetProperty("id").GetInt32()); }
        Assert.AreEqual(100, seen.Count);
    }

    private sealed class FragmentedStream(Stream inner) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 1));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
