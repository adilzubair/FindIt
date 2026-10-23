using System.Collections.Concurrent;
using System.Text.Json;
using FindIt.Core;
using FindIt.Runtime;
using FindIt.Shared;

namespace FindIt.Worker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var root = Argument(args, "--root");
        var role = Argument(args, "--role") ?? "reader";
        if (root is null || role is not ("reader" or "preparer" or "preview"))
        {
            Console.Error.WriteLine("Usage: FindIt.Worker --root <archive drive root> --role reader|preparer|preview");
            return 2;
        }
        try
        {
            await using var service = new ArchiveService(root, RuntimeFactory.CreateEmbeddingProvider(root), RuntimeFactory.CreateContentProcessor(root));
            using var playback = role == "preview" ? new PlaybackHost(root) : null;
            if (role == "reader") service.StartSemanticWarmUp();
            await RunAsync(service, role, playback).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FindIt worker: {ex.Message}");
            return 1;
        }
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static async Task RunAsync(IArchiveBackend backend, string role, PlaybackHost? playback)
    {
        using var lifetime = new CancellationTokenSource();
        using var writes = new SemaphoreSlim(1);
        var requests = new ConcurrentDictionary<string, CancellationTokenSource>();
        var tasks = new ConcurrentDictionary<string, Task>();
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();

        async Task HandleAsync(string id, string method, JsonElement parameters, CancellationTokenSource cancellation)
        {
            try
            {
                var token = cancellation.Token;
                object? result = method switch
                {
                    "archive.status" => await backend.GetStatusAsync(token).ConfigureAwait(false),
                    "worker.diagnostics" => Diagnostics(),
                    "archive.search" => await backend.SearchAsync(Deserialize<SearchRequest>(parameters), token).ConfigureAwait(false),
                    "archive.preview" => await backend.GetPreviewAsync(Deserialize<PreviewRequest>(parameters), token).ConfigureAwait(false),
                    "people.list" => await backend.GetPeopleAsync(token).ConfigureAwait(false),
                    "people.observations" => await backend.GetPersonObservationsAsync(parameters.GetProperty("personId").GetGuid(), token).ConfigureAwait(false),
                    "people.save" => await SavePersonAsync(parameters, token).ConfigureAwait(false),
                    "preferences.recentQueries" => await SaveRecentQueriesAsync(parameters, token).ConfigureAwait(false),
                    "playback.play" when playback is not null => playback.Play(Deserialize<PlaybackRequest>(parameters), token),
                    "playback.pause" when playback is not null => playback.Pause(),
                    "playback.stop" when playback is not null => playback.Stop(),
                    "playback.seek" when playback is not null => playback.Seek(parameters.GetProperty("milliseconds").GetInt64()),
                    "playback.volume" when playback is not null => playback.Volume(parameters.GetProperty("volume").GetInt32()),
                    "playback.state" when playback is not null => playback.State(),
                    "archive.prepare" when role == "preparer" => await backend.PrepareAsync(Deserialize<PreparationRequest>(parameters), new InlineProgress(update =>
                    {
                        // A synchronous write preserves ordering: final result cannot overtake the last progress frame.
                        FramedTransport.WriteAsync(output, new RpcNotification("2.0", "preparation.progress", new RpcProgress(id, update)), writes, lifetime.Token).GetAwaiter().GetResult();
                    }), token).ConfigureAwait(false),
                    "archive.prepare" => throw new InvalidOperationException("Preparation requires an explicit preparer worker."),
                    _ => throw new MissingMethodException("Unknown worker method.")
                };
                await FramedTransport.WriteAsync(output, new RpcSuccess("2.0", id, result), writes, lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var code = ex is OperationCanceledException ? -32800 : ex is MissingMethodException ? -32601 : -32000;
                try { await FramedTransport.WriteAsync(output, new RpcFailure("2.0", id, new RpcError(code, ex is OperationCanceledException ? "Preparation paused. Completed work has been retained." : ex.Message)), writes, lifetime.Token).ConfigureAwait(false); }
                catch (Exception sendError) when (sendError is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            finally { requests.TryRemove(id, out _); cancellation.Dispose(); tasks.TryRemove(id, out _); }
        }

        async Task<bool> SaveRecentQueriesAsync(JsonElement parameters, CancellationToken token)
        {
            await backend.SaveRecentQueriesAsync(parameters.GetProperty("queries").Deserialize<string[]>(WireJson.Options) ?? [], token).ConfigureAwait(false);
            return true;
        }

        async Task<PersonRecord> SavePersonAsync(JsonElement parameters, CancellationToken token)
        {
            var request = Deserialize<PersonUpdateRequest>(parameters);
            if (request.ReferencePaths.Count > 0 && role != "preparer") throw new InvalidOperationException("Reference enrollment requires a preparer worker.");
            return await backend.SavePersonAsync(request, token).ConfigureAwait(false);
        }

        try
        {
            while (true)
            {
                using var frame = await FramedTransport.ReadAsync(input, lifetime.Token).ConfigureAwait(false);
                if (frame is null) break;
                var envelope = frame.RootElement;
                if (!envelope.TryGetProperty("method", out var methodValue)) continue;
                var method = methodValue.GetString() ?? "";
                var parameters = envelope.TryGetProperty("params", out var parameterValue) ? parameterValue.Clone() : default;
                if (method == "rpc.cancel")
                {
                    if (parameters.TryGetProperty("requestId", out var target) && requests.TryGetValue(target.GetString() ?? "", out var pending)) pending.Cancel();
                    continue;
                }
                if (!envelope.TryGetProperty("id", out var idValue) || idValue.GetString() is not { } id || id.Length > 100) continue;
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                if (!requests.TryAdd(id, cancellation)) { cancellation.Dispose(); continue; }
                // Keep the input loop free to receive cancellation while extraction/inference is running.
                tasks[id] = Task.Run(() => HandleAsync(id, method, parameters, cancellation));
            }
        }
        finally
        {
            lifetime.Cancel();
            foreach (var request in requests.Values) request.Cancel();
            await Task.WhenAll(tasks.Values).ConfigureAwait(false);
        }
    }

    private static T Deserialize<T>(JsonElement element) => element.Deserialize<T>(WireJson.Options) ?? throw new InvalidDataException("Missing request parameters.");
    private static WorkerDiagnostics Diagnostics()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return new(process.Id, process.PrivateMemorySize64, process.WorkingSet64, process.PeakWorkingSet64, process.TotalProcessorTime.TotalMilliseconds);
    }
    private sealed class InlineProgress(Action<PreparationProgress> report) : IProgress<PreparationProgress>
    {
        public void Report(PreparationProgress value) => report(value);
    }
}
