using System.Buffers.Binary;
using System.Text.Json;

namespace FindIt.Shared;

/// <summary>Length-prefixed UTF-8 JSON. Stdout is reserved for this protocol.</summary>
public static class FramedTransport
{
    public const int MaximumFrameBytes = 8 * 1024 * 1024;

    public static async Task<JsonDocument?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("Invalid worker frame length.");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 });
    }

    public static async Task WriteAsync<T>(Stream stream, T message, SemaphoreSlim writeLock, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, WireJson.Options);
        if (payload.Length > MaximumFrameBytes) throw new InvalidDataException("Worker response exceeds the frame limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { writeLock.Release(); }
    }
}

public sealed record RpcRequest(string Jsonrpc, string Id, string Method, object? Params);
public sealed record RpcSuccess(string Jsonrpc, string Id, object? Result);
public sealed record RpcFailure(string Jsonrpc, string Id, RpcError Error);
public sealed record RpcError(int Code, string Message);
public sealed record RpcNotification(string Jsonrpc, string Method, object Params);
public sealed record RpcProgress(string RequestId, PreparationProgress Progress);
public sealed record RpcCancellation(string RequestId);
