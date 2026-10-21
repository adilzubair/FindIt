using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using FindIt.Shared;
using FindIt.Core;

namespace FindIt.Worker;

internal sealed class PlaybackHost : IDisposable
{
    private readonly string _root;
    private readonly object _gate = new();
    private nint _instance;
    private nint _player;
    private bool _paused;
    private static string? _libraryPath;
    private static bool _resolverInstalled;

    public PlaybackHost(string root) => _root = Path.GetFullPath(root);

    public PlaybackState Play(PlaybackRequest request)
    {
        lock (_gate)
        {
            var path = Path.GetFullPath(request.Path);
            var relative = Path.GetRelativePath(_root, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new UnauthorizedAccessException("Playback is limited to this archive's originals.");
            path = PortablePaths.Resolve(_root, relative);
            if (!File.Exists(path)) throw new FileNotFoundException("The original is unavailable. Reconnect the archive drive.");
            EnsureLoaded();
            Native.libvlc_media_player_stop(_player);
            var media = Native.libvlc_media_new_path(_instance, path);
            if (media == 0) throw new IOException("The media could not be opened.");
            try
            {
                if (request.StartMs > 0)
                    Native.libvlc_media_add_option(media, ":start-time=" + (request.StartMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture));
                Native.libvlc_media_player_set_media(_player, media);
                if (request.WindowHandle != 0) Native.libvlc_media_player_set_hwnd(_player, (nint)request.WindowHandle);
                if (Native.libvlc_media_player_play(_player) != 0) throw new IOException("Playback failed. The file may be damaged or unsupported.");
                _paused = false;
                return State();
            }
            finally { Native.libvlc_media_release(media); }
        }
    }

    public PlaybackState Pause()
    {
        lock (_gate) { if (_player != 0) { _paused = !_paused; Native.libvlc_media_player_set_pause(_player, _paused ? 1 : 0); } return State(); }
    }
    public PlaybackState Stop() { lock (_gate) { if (_player != 0) Native.libvlc_media_player_stop(_player); return State(); } }
    public PlaybackState Seek(long time) { lock (_gate) { if (_player != 0) Native.libvlc_media_player_set_time(_player, Math.Max(0, time)); return State(); } }
    public PlaybackState Volume(int value) { lock (_gate) { if (_player != 0) Native.libvlc_audio_set_volume(_player, Math.Clamp(value, 0, 100)); return State(); } }
    public PlaybackState State() { lock (_gate) return _player == 0 ? new(false, 0, 0) : new(Native.libvlc_media_player_is_playing(_player) != 0, Math.Max(0, Native.libvlc_media_player_get_time(_player)), Math.Max(0, Native.libvlc_media_player_get_length(_player))); }

    private void EnsureLoaded()
    {
        if (_instance != 0) return;
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtime", "vlc"),
            Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"),
            Path.Combine(_root, "FindIt", "runtime", "vlc"),
            Path.Combine(_root, ".tools", "runtime", "vlc")
        };
        var folder = candidates.FirstOrDefault(p => File.Exists(Path.Combine(p, "libvlc.dll")))
            ?? throw new FileNotFoundException("The bundled video runtime is missing. Restore the complete FindIt portable package.");
        _libraryPath = Path.Combine(folder, "libvlc.dll");
        SetDllDirectory(folder);
        if (!_resolverInstalled)
        {
            NativeLibrary.SetDllImportResolver(typeof(PlaybackHost).Assembly, (name, assembly, searchPath) => name == "findit_vlc" ? NativeLibrary.Load(_libraryPath) : 0);
            _resolverInstalled = true;
        }
        var options = new[] { "--no-video-title-show", "--no-osd", "--no-metadata-network-access", "--no-lua", "--no-stats", "--plugin-path=" + Path.Combine(folder, "plugins") };
        var strings = options.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var argv = Marshal.AllocHGlobal(nint.Size * strings.Length);
        try
        {
            Marshal.Copy(strings, 0, argv, strings.Length);
            _instance = Native.libvlc_new(strings.Length, argv);
        }
        finally { foreach (var value in strings) Marshal.FreeCoTaskMem(value); Marshal.FreeHGlobal(argv); }
        if (_instance == 0) throw new IOException("The bundled video runtime could not initialize.");
        _player = Native.libvlc_media_player_new(_instance);
        if (_player == 0) { Native.libvlc_release(_instance); _instance = 0; throw new IOException("Unable to create the video player."); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_player != 0) { Native.libvlc_media_player_stop(_player); Native.libvlc_media_player_release(_player); _player = 0; }
            if (_instance != 0) { Native.libvlc_release(_instance); _instance = 0; }
        }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);

    private static class Native
    {
        private const string Dll = "findit_vlc";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint libvlc_new(int argc, nint argv);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_release(nint instance);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint libvlc_media_new_path(nint instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_add_option(nint media, [MarshalAs(UnmanagedType.LPUTF8Str)] string option);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_release(nint media);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint libvlc_media_player_new(nint instance);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_release(nint player);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_set_media(nint player, nint media);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_set_hwnd(nint player, nint handle);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_media_player_play(nint player);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_stop(nint player);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_set_pause(nint player, int pause);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_media_player_is_playing(nint player);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern long libvlc_media_player_get_time(nint player);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern long libvlc_media_player_get_length(nint player);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_set_time(nint player, long time);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_audio_set_volume(nint player, int volume);
    }
}
