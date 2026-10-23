using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FindIt.Shared;

namespace FindIt.Client;

/// <summary>Supervises crash-isolated workers. No archive content is cached on the host.</summary>
public sealed class WorkerArchiveClient : IArchiveBackend
{
    private WorkerConnection _reader;
    private WorkerConnection? _preparer;
    private readonly SemaphoreSlim _preparationLock = new(1);
    private bool _disposed;
    public string RootPath { get; }

    private WorkerArchiveClient(string root, WorkerConnection reader) => (RootPath, _reader) = (root, reader);

    public static async Task<IArchiveBackend> CreateAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The archive drive is unavailable.");
        var connection = WorkerConnection.Start(root, "reader");
        var client = new WorkerArchiveClient(root, connection);
        try { await client.GetStatusAsync(cancellationToken).ConfigureAwait(false); return client; }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public Task<ArchiveStatus> GetStatusAsync(CancellationToken cancellationToken = default) => ReadAsync<ArchiveStatus>("archive.status", null, cancellationToken);
    public Task<WorkerDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default) => ReadAsync<WorkerDiagnostics>("worker.diagnostics", null, cancellationToken);
    public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default) => ReadAsync<SearchResponse>("archive.search", request, cancellationToken);
    public async Task<IReadOnlyList<PersonRecord>> GetPeopleAsync(CancellationToken cancellationToken = default) => await ReadAsync<List<PersonRecord>>("people.list", null, cancellationToken).ConfigureAwait(false);
    public async Task<IReadOnlyList<PersonObservation>> GetPersonObservationsAsync(Guid personId, CancellationToken cancellationToken = default) => await ReadAsync<List<PersonObservation>>("people.observations", new { personId }, cancellationToken).ConfigureAwait(false);
    public Task<PreviewResult> GetPreviewAsync(PreviewRequest request, CancellationToken cancellationToken = default) => ReadAsync<PreviewResult>("archive.preview", request, cancellationToken);
    public Task SaveRecentQueriesAsync(IReadOnlyList<string> queries, CancellationToken cancellationToken = default) => ReadAsync<bool>("preferences.recentQueries", new { queries }, cancellationToken);

    // A native crash ends only the reader process. Start a new reader and retry the request once,
    // rather than leaving every later search failing until the app is reopened. A reader that
    // keeps dying is not restarted more than once every ten seconds.
    private readonly SemaphoreSlim _readerGate = new(1);
    private long _readerRestartedAt;
    private async Task<T> ReadAsync<T>(string method, object? parameters, CancellationToken cancellationToken, bool retry = true)
    {
        var reader = _reader;
        try { return await reader.CallAsync<T>(method, parameters, null, cancellationToken).ConfigureAwait(false); }
        catch (Exception) when (retry && !reader.IsAlive && !cancellationToken.IsCancellationRequested && !_disposed)
        {
            if (!await RestartReaderAsync(reader).ConfigureAwait(false)) throw;
        }
        return await _reader.CallAsync<T>(method, parameters, null, cancellationToken).ConfigureAwait(false);
    }
    private async Task<bool> RestartReaderAsync(WorkerConnection failed)
    {
        await _readerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(failed, _reader)) return true;
            if (_disposed || !Directory.Exists(RootPath) || (_readerRestartedAt != 0 && Stopwatch.GetElapsedTime(_readerRestartedAt) < TimeSpan.FromSeconds(10))) return false;
            await failed.DisposeAsync().ConfigureAwait(false);
            _reader = WorkerConnection.Start(RootPath, "reader"); _readerRestartedAt = Stopwatch.GetTimestamp();
            return true;
        }
        finally { _readerGate.Release(); }
    }

    public async Task<PersonRecord> SavePersonAsync(PersonUpdateRequest request, CancellationToken cancellationToken = default)
    {
        // Enrollment loads face models only in a separate, short-lived preparation process.
        // Saving changes the drive, so a failed save is reported rather than repeated.
        if (request.ReferencePaths.Count == 0)
            return await ReadAsync<PersonRecord>("people.save", request, cancellationToken, retry: false).ConfigureAwait(false);
        await _preparationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var enrollment = WorkerConnection.Start(RootPath, "preparer");
            return await enrollment.CallAsync<PersonRecord>("people.save", request, null, cancellationToken).ConfigureAwait(false);
        }
        finally { _preparationLock.Release(); }
    }

    public async Task<ArchiveStatus> PrepareAsync(PreparationRequest request, IProgress<PreparationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _preparationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _preparer = WorkerConnection.Start(RootPath, "preparer");
            await _preparer.CallAsync<ArchiveStatus>("archive.prepare", request, progress, cancellationToken).ConfigureAwait(false);
            // Reopen after publication; a reader never mutates its pinned generation beneath a query.
            await _readerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _reader.DisposeAsync().ConfigureAwait(false);
                _reader = WorkerConnection.Start(RootPath, "reader");
            }
            finally { _readerGate.Release(); }
            return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_preparer is not null) await _preparer.DisposeAsync().ConfigureAwait(false);
            _preparer = null;
            _preparationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_preparer is not null) await _preparer.DisposeAsync().ConfigureAwait(false);
        await _reader.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class WorkerConnection : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeLock = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, IProgress<PreparationProgress>> _progress = new();
    private readonly Task _readTask;
    private readonly Task _errorTask;
    private string _lastError = "";
    private bool _disposed;
    /// <summary>False once the process has exited or its protocol stream ended.</summary>
    public bool IsAlive => !_disposed && !_readTask.IsCompleted && !_process.HasExited;

    private WorkerConnection(Process process)
    {
        _process = process;
        _readTask = ReadLoopAsync();
        _errorTask = DrainErrorsAsync();
    }

    public static WorkerConnection Start(string root, string role)
    {
        var worker = FindWorker();
        var start = new ProcessStartInfo(worker)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--root"); start.ArgumentList.Add(root);
        start.ArgumentList.Add("--role"); start.ArgumentList.Add(role);
        start.Environment["DOTNET_NOLOGO"] = "1";
        return new WorkerConnection(Process.Start(start) ?? throw new IOException("Unable to start the FindIt worker."));
    }

    private static string FindWorker()
    {
        var configured = Environment.GetEnvironmentVariable("FINDIT_WORKER_PATH");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var adjacent = Path.Combine(AppContext.BaseDirectory, "FindIt.Worker.exe");
        if (File.Exists(adjacent)) return adjacent;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(directory.FullName, "src", "FindIt.Worker", "bin", config, "net10.0", "FindIt.Worker.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException("FindIt.Worker.exe is missing. Build the solution or restore the complete portable application folder.");
    }

    public async Task<T> CallAsync<T>(string method, object? parameters, IProgress<PreparationProgress>? progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        if (progress is not null) _progress[id] = progress;
        CancellationTokenRegistration registration = default;
        try
        {
            await FramedTransport.WriteAsync(_process.StandardInput.BaseStream, new RpcRequest("2.0", id, method, parameters), _writeLock, _lifetime.Token).ConfigureAwait(false);
            // Register only after sending: a cancel frame must never overtake its request.
            registration = cancellationToken.Register(() => { _ = CancelAsync(id); });
            // Wait for the worker to checkpoint cancellation rather than killing a writer mid-transaction.
            var result = await completion.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result.Deserialize<T>(WireJson.Options) ?? throw new InvalidDataException("Empty worker response.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        finally { registration.Dispose(); _pending.TryRemove(id, out _); _progress.TryRemove(id, out _); }
    }

    private async Task CancelAsync(string id)
    {
        try
        {
            await FramedTransport.WriteAsync(_process.StandardInput.BaseStream, new RpcNotification("2.0", "rpc.cancel", new RpcCancellation(id)), _writeLock, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var frame = await FramedTransport.ReadAsync(_process.StandardOutput.BaseStream, _lifetime.Token).ConfigureAwait(false);
                if (frame is null) break;
                var value = frame.RootElement;
                if (value.TryGetProperty("method", out var method) && method.GetString() == "preparation.progress")
                {
                    var update = value.GetProperty("params").Deserialize<RpcProgress>(WireJson.Options);
                    if (update is not null && _progress.TryGetValue(update.RequestId, out var progress)) progress.Report(update.Progress);
                    continue;
                }
                if (!value.TryGetProperty("id", out var idValue) || idValue.GetString() is not { } id || !_pending.TryGetValue(id, out var completion)) continue;
                if (value.TryGetProperty("error", out var error))
                {
                    var rpcError = error.Deserialize<RpcError>(WireJson.Options);
                    if (rpcError?.Code == -32800) completion.TrySetCanceled();
                    else completion.TrySetException(new IOException(rpcError?.Message ?? "The worker could not complete the request."));
                }
                else completion.TrySetResult(value.GetProperty("result").Clone());
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidDataException or ObjectDisposedException) { failure = ex; }
        finally
        {
            var message = string.IsNullOrWhiteSpace(_lastError) ? "The archive worker stopped. Reconnect the drive and reopen the archive." : _lastError;
            foreach (var completion in _pending.Values) completion.TrySetException(failure ?? new IOException(message));
        }
    }

    private async Task DrainErrorsAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
                _lastError = line.Length > 500 ? line[..500] : line;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _process.StandardInput.Close(); } catch (IOException) { }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        finally
        {
            _lifetime.Cancel();
            try { await Task.WhenAll(_readTask, _errorTask).ConfigureAwait(false); } catch (OperationCanceledException) { }
            _process.Dispose();
            _lifetime.Dispose();
        }
    }
}
