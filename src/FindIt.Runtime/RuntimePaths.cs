using System.Reflection;
using System.Runtime.InteropServices;
using FindIt.Shared;

namespace FindIt.Runtime;

public static class RuntimeFactory
{
    public static IEmbeddingProvider CreateEmbeddingProvider(string rootPath) => new NativeEmbeddingProvider(new RuntimePaths(rootPath));
    public static IContentProcessor CreateContentProcessor(string rootPath) => new ContentProcessor(new RuntimePaths(rootPath));
}

public sealed class RuntimePaths
{
    private readonly string[] roots;
    public string RootPath { get; }
    public RuntimePaths(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        RootPath=root;
        roots = [Path.Combine(root, "FindIt"), AppContext.BaseDirectory, Path.Combine(root, ".findit"), root];
        NativeMethods.Configure(this);
    }
    public string? Model(string name) => Find("models", name) ?? Find("runtime/models", name);
    public string? Tool(string name) => Find("runtime/tools", name) ?? Find("tools", name);
    public string? Native(string name) => Find("runtime/native", name) ?? Find("native", name);
    public string? Tessdata => roots.SelectMany(r => new[] { Path.Combine(r, "runtime/tessdata"), Path.Combine(r,"tessdata") }).FirstOrDefault(Directory.Exists);
    private string? Find(string sub, string name)
    {
        foreach (var r in roots)
        {
            var candidate = Path.Combine(r, sub, name);
            if (File.Exists(candidate)) return candidate;
        }
        // Development builds discover the repository without using the process working directory.
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, sub, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

internal static class NativeMethods
{
    private static RuntimePaths? paths;
    private static readonly object Gate = new();
    private static readonly List<IntPtr> Dependencies = [];
    private static readonly HashSet<string> LoadedDependencies = new(StringComparer.OrdinalIgnoreCase);
    static NativeMethods() => NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
    internal static void Configure(RuntimePaths value) { lock(Gate) paths ??= value; }
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? flags)
    {
        lock(Gate)
        {
            var file = paths?.Native(name + ".dll");
            if(file is null) return IntPtr.Zero;
            var folder = Path.GetDirectoryName(file)!;
            // LoadLibraryEx uses this DLL's directory for transitive dependencies; no PATH/system mutation.
            var handle = LoadLibraryEx(file, IntPtr.Zero, 0x100 | 0x1000);
            return handle;
        }
    }
    internal static bool Present(RuntimePaths paths, string dll) => paths.Native(dll + ".dll") is not null;
    internal static void EnsureVisualDependencies(RuntimePaths value)
    {
        lock(Gate)
        {
            foreach(var name in new[]{"vcruntime140.dll","vcruntime140_1.dll","msvcp140.dll","msvcp140_1.dll","msvcp140_2.dll","OpenCvSharpExtern.dll","leptonica-1.82.0.dll","tesseract50.dll"})
            {
                if(value.Native(name)is not { } path)continue;
                if(LoadedDependencies.Contains(path))continue;
                var handle=LoadLibraryEx(path,IntPtr.Zero,0x100|0x1000);
                if(handle==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),$"Cannot load bundled {name}.");
                Dependencies.Add(handle);
                LoadedDependencies.Add(path);
            }
        }
    }
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr LoadLibraryEx(string file,IntPtr reserved,uint flags);
    [DllImport("findit_native", CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr fi_create([MarshalAs(UnmanagedType.LPUTF8Str)] string text,[MarshalAs(UnmanagedType.LPUTF8Str)] string? media,[MarshalAs(UnmanagedType.LPUTF8Str)] string backends,int threads,int gpu,out IntPtr error);
    [DllImport("findit_native", CallingConvention=CallingConvention.Cdecl)] internal static extern void fi_close(IntPtr handle);
    [DllImport("findit_native", CallingConvention=CallingConvention.Cdecl)] internal static extern void fi_clear_media_cache(IntPtr handle);
    [DllImport("findit_native", CallingConvention=CallingConvention.Cdecl)] internal static extern void fi_free_error(IntPtr error);
    [DllImport("findit_native", CallingConvention=CallingConvention.Cdecl)] internal static extern int fi_gpu_available([MarshalAs(UnmanagedType.LPUTF8Str)] string backends);
    [DllImport("findit_native", CallingConvention=CallingConvention.Cdecl)] internal static extern int fi_embed_text(IntPtr handle,[MarshalAs(UnmanagedType.LPUTF8Str)] string text,int query,[Out] float[] result,int capacity,IntPtr cancel,out IntPtr error);
    [DllImport("findit_native", CallingConvention=CallingConvention.Cdecl)] internal static extern int fi_embed_media(IntPtr handle,[MarshalAs(UnmanagedType.LPUTF8Str)] string path,int image,[Out] float[] result,int capacity,IntPtr cancel,out IntPtr error);
    [DllImport("findit_native", CallingConvention=CallingConvention.Cdecl)] internal static extern int fi_embed_media_buffer(IntPtr handle,byte[] data,nuint length,int image,[Out] float[] result,int capacity,IntPtr cancel,out IntPtr error);
    [DllImport("findit_native", CallingConvention=CallingConvention.Cdecl)] internal static extern int fi_embed_frame_buffers(IntPtr handle,IntPtr[] frames,nuint[] lengths,int count,[Out] float[] result,int capacity,IntPtr cancel,out IntPtr error);
    [DllImport("findit_native", CallingConvention=CallingConvention.Cdecl)] internal static extern int fi_embed_frames(IntPtr handle,[MarshalAs(UnmanagedType.LPUTF8Str)] string paths,[Out] float[] result,int capacity,IntPtr cancel,out IntPtr error);
    internal static void Check(IntPtr error)
    {
        if(error == IntPtr.Zero) return;
        var message = Marshal.PtrToStringUTF8(error) ?? "Native inference failed.";
        fi_free_error(error);
        throw new InvalidOperationException(message);
    }
}
