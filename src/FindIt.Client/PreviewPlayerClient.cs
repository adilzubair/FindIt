using FindIt.Shared;

namespace FindIt.Client;

/// <summary>Video decoding lives in a separate process, rendering into a UI-owned child HWND.</summary>
public sealed class PreviewPlayerClient : IAsyncDisposable
{
    private readonly string _root;
    private readonly nint _window;
    private WorkerConnection? _worker;
    public PreviewPlayerClient(string rootPath, nint windowHandle) => (_root, _window) = (Path.GetFullPath(rootPath), windowHandle);
    private WorkerConnection Worker => _worker ??= WorkerConnection.Start(_root, "preview");
    public Task<PlaybackState> PlayAsync(string absolutePath, long startMs = 0, CancellationToken cancellationToken = default) => Worker.CallAsync<PlaybackState>("playback.play", new PlaybackRequest(absolutePath, _window.ToInt64(), startMs), null, cancellationToken);
    /// <summary>Loads the selected moment paused; decoding never starts audible playback.</summary>
    public Task<PlaybackState> OpenAsync(string absolutePath, long startMs = 0, CancellationToken cancellationToken = default) => Worker.CallAsync<PlaybackState>("playback.play", new PlaybackRequest(absolutePath, _window.ToInt64(), startMs, AutoPlay: false), null, cancellationToken);
    public Task<PlaybackState> PauseAsync(CancellationToken cancellationToken = default) => Worker.CallAsync<PlaybackState>("playback.pause", null, null, cancellationToken);
    public Task<PlaybackState> StopAsync(CancellationToken cancellationToken = default) => Worker.CallAsync<PlaybackState>("playback.stop", null, null, cancellationToken);
    public Task<PlaybackState> SeekAsync(long milliseconds, CancellationToken cancellationToken = default) => Worker.CallAsync<PlaybackState>("playback.seek", new { milliseconds }, null, cancellationToken);
    public Task<PlaybackState> SetVolumeAsync(int volume, CancellationToken cancellationToken = default) => Worker.CallAsync<PlaybackState>("playback.volume", new { volume }, null, cancellationToken);
    public Task<PlaybackState> GetStateAsync(CancellationToken cancellationToken = default) => Worker.CallAsync<PlaybackState>("playback.state", null, null, cancellationToken);
    public async ValueTask DisposeAsync()
    {
        if (_worker is not null) await _worker.DisposeAsync().ConfigureAwait(false);
        _worker = null;
    }
}
