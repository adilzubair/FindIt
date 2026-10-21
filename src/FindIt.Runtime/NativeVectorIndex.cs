using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace FindIt.Runtime;

public sealed record VectorNeighbor(ulong Key, float Distance);

/// <summary>USearch HNSW with fp16 nodes. Views borrow a managed Windows file mapping, including Unicode paths.</summary>
public sealed unsafe class NativeVectorIndex : IDisposable
{
    private IntPtr handle;
    private readonly object gate = new();
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private byte* pointer;
    private readonly int dimensions;
    private bool readOnly;
    private bool incompleteBatch;
    public int Dimensions => dimensions;
    public long Count
    {
        get { lock(gate) { EnsureOpen();var count=usearch_size(handle,out var error);Check(error);return checked((long)count); } }
    }
    public bool Contains(ulong key)
    {
        lock(gate){EnsureOpen();bool found=usearch_contains(handle,key,out var error);Check(error);return found;}
    }
    /// <summary>Retrieves stored FP16 values as FP32 without an approximate graph traversal.</summary>
    public float[]? Get(ulong key)
    {
        lock(gate)
        {
            EnsureOpen();var vector=new float[dimensions];
            fixed(float* data=vector)
            {var found=usearch_get(handle,key,1,(IntPtr)data,1,out var error);Check(error);return found==0?null:vector;}
        }
    }
    public static bool Available
    {
        get
        {
            try { var p=new RuntimePaths(AppContext.BaseDirectory); return NativeMethods.Present(p,"libusearch_c"); }
            catch { return false; }
        }
    }
    public NativeVectorIndex(int dimensions)
    {
        if(dimensions<=0||dimensions>65536) throw new ArgumentOutOfRangeException(nameof(dimensions));
        this.dimensions=dimensions;
        _=new RuntimePaths(AppContext.BaseDirectory);
        // Pinned usearch.h: metric_cos=1, scalar_f32=1, scalar_f16=3.
        var options=new InitOptions { Metric=1, Quantization=3,Dimensions=(nuint)dimensions,Connectivity=16,ExpansionAdd=128,ExpansionSearch=128 };
        handle=usearch_init(ref options,out var error); Check(error);
        if(handle==IntPtr.Zero) throw new InvalidOperationException("USearch initialization failed.");
    }
    public void Add(ulong key,ReadOnlySpan<float> vector)
    {
        Validate(vector);
        lock(gate)
        {
            EnsureOpen(); if(readOnly) throw new InvalidOperationException("An immutable generation cannot be edited.");
            var size=usearch_size(handle,out var error);Check(error);
            var capacity=usearch_capacity(handle,out error);Check(error);
            if(size==capacity){usearch_reserve(handle,Math.Max(1024,capacity*2),out error);Check(error);}
            fixed(float* data=vector) { usearch_add(handle,key,(IntPtr)data,1,out error);Check(error); }
        }
    }
    /// <summary>Inserts up to 1024 consecutive new keys after reserving the complete build target.
    /// All workers join before returning; callers may cancel only between complete batches.
    /// The caller must leave the supplied memory unchanged until this method returns.</summary>
    public void AddBatch(ulong firstKey,ReadOnlyMemory<float> vectors,int maximumConcurrency,long reserveCapacity)
    {
        if(vectors.Length==0||vectors.Length%dimensions!=0||vectors.Length/dimensions>1024)throw new ArgumentException("A batch must contain 1–1024 complete vectors.",nameof(vectors));
        if(maximumConcurrency<1||maximumConcurrency>8)throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        if(reserveCapacity<0)throw new ArgumentOutOfRangeException(nameof(reserveCapacity));
        int count=vectors.Length/dimensions;
        _=checked(firstKey+(ulong)(count-1));
        foreach(float value in vectors.Span)if(!float.IsFinite(value))throw new ArgumentException("Vectors must be finite.",nameof(vectors));
        lock(gate)
        {
            EnsureOpen();if(readOnly)throw new InvalidOperationException("An immutable generation cannot be edited.");
            long before=checked((long)usearch_size(handle,out var error));Check(error);
            long required=checked(before+count);
            // Duplicate-key detection is performed serially before mutation. Native duplicate lookup+insert is not atomic.
            for(int i=0;i<count;i++)
            {bool exists=usearch_contains(handle,firstKey+(ulong)i,out error);Check(error);if(exists)throw new ArgumentException("Batch keys must be new and unique.",nameof(firstKey));}
            usearch_change_threads_add(handle,(nuint)maximumConcurrency,out error);Check(error);
            long capacity=checked((long)usearch_capacity(handle,out error));Check(error);
            long target=Math.Max(required,reserveCapacity);
            if(capacity<target){usearch_reserve(handle,checked((nuint)target),out error);Check(error);}
            using var pinned=vectors.Pin();
            nint address=(nint)pinned.Pointer;
            try
            {
                // Direct ABI calls avoid re-entering the outer managed lock. Each call owns its error pointer.
                Parallel.For(0,count,new ParallelOptions{MaxDegreeOfParallelism=maximumConcurrency},i=>
                {
                    usearch_add(handle,firstKey+(ulong)i,address+checked(i*dimensions*sizeof(float)),1,out var workerError);Check(workerError);
                });
                long after=checked((long)usearch_size(handle,out error));Check(error);
                if(after!=required)throw new InvalidOperationException("The completed native batch has an unexpected count.");
            }
            catch
            {
                // A partially completed batch is not a canonical prefix. It can never be searched, saved or continued.
                incompleteBatch=true;throw;
            }
        }
    }
    public void Save(string path)
    {
        lock(gate)
        {
            EnsureOpen();var length=usearch_serialized_length(handle,out var error);Check(error);
            using var file=new FileStream(path,FileMode.Create,FileAccess.ReadWrite,FileShare.None);
            file.SetLength(checked((long)length));
            using var mm=MemoryMappedFile.CreateFromFile(file,null,0,MemoryMappedFileAccess.ReadWrite,HandleInheritability.None,true);
            using var accessor=mm.CreateViewAccessor(0,0,MemoryMappedFileAccess.ReadWrite);
            byte* address=null;accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref address);
            try { usearch_save_buffer(handle,(IntPtr)(address+accessor.PointerOffset),length,out error);Check(error);accessor.Flush();file.Flush(true); }
            finally { accessor.SafeMemoryMappedViewHandle.ReleasePointer(); }
        }
    }
    public static NativeVectorIndex Open(string path,bool view=true) => OpenCore(path,view,readOnly:true);

    /// <summary>Copies a saved index into privately owned native memory for continued insertion.
    /// The source file is mapped read-only only while copying, then closed; it is never edited.</summary>
    public static NativeVectorIndex OpenForUpdate(string path) => OpenCore(path,view:false,readOnly:false);

    private static NativeVectorIndex OpenCore(string path,bool view,bool readOnly)
    {
        _=new RuntimePaths(AppContext.BaseDirectory);
        var mm=MemoryMappedFile.CreateFromFile(path,FileMode.Open,null,0,MemoryMappedFileAccess.Read);
        var accessor=mm.CreateViewAccessor(0,0,MemoryMappedFileAccess.Read);
        byte* pointer=null;accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        NativeVectorIndex? result=null;
        try
        {
            usearch_metadata_buffer((IntPtr)(pointer+accessor.PointerOffset),(nuint)accessor.Capacity,out var options,out var error);Check(error);
            result=new NativeVectorIndex(checked((int)options.Dimensions));
            if(view)
            {
                usearch_view_buffer(result.handle,(IntPtr)(pointer+accessor.PointerOffset),(nuint)accessor.Capacity,out error);Check(error);
                result.mapping=mm;result.view=accessor;result.pointer=pointer;result.readOnly=true;
            }
            else
            {
                usearch_load_buffer(result.handle,(IntPtr)(pointer+accessor.PointerOffset),(nuint)accessor.Capacity,out error);Check(error);
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();accessor.Dispose();mm.Dispose();result.readOnly=readOnly;
            }
            return result;
        }
        catch { result?.Dispose();accessor.SafeMemoryMappedViewHandle.ReleasePointer();accessor.Dispose();mm.Dispose();throw; }
    }
    public IReadOnlyList<VectorNeighbor> Search(ReadOnlySpan<float> query,int limit,IReadOnlySet<ulong>? allowedIds=null)
    {
        Validate(query);limit=Math.Clamp(limit,1,16384);
        lock(gate)
        {
            EnsureOpen();var keys=new ulong[limit];var distances=new float[limit];nuint count;
            fixed(float* q=query)
            {
                if(allowedIds is null) count=usearch_search(handle,(IntPtr)q,1,(nuint)limit,keys,distances,out var error1).WithCheck(error1);
                else
                {
                    Filter callback=(key,state)=>allowedIds.Contains(key)?1:0;
                    count=usearch_filtered_search(handle,(IntPtr)q,1,(nuint)limit,callback,IntPtr.Zero,keys,distances,out var error2).WithCheck(error2);
                    GC.KeepAlive(callback);
                }
            }
            var results=new VectorNeighbor[(int)count];for(var i=0;i<results.Length;i++)results[i]=new(keys[i],distances[i]);return results;
        }
    }
    private void Validate(ReadOnlySpan<float> vector){if(vector.Length!=dimensions)throw new ArgumentException($"Expected {dimensions} dimensions.");foreach(var v in vector)if(!float.IsFinite(v))throw new ArgumentException("Vectors must be finite.");}
    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(handle==IntPtr.Zero,this);
        if(incompleteBatch)throw new InvalidOperationException("The native graph contains an incomplete batch. Dispose it and reopen the last durable checkpoint.");
    }
    internal static void Check(IntPtr error){if(error!=IntPtr.Zero)throw new InvalidOperationException(Marshal.PtrToStringUTF8(error));}
    public void Dispose()
    {
        lock(gate)
        {
            if(handle!=IntPtr.Zero){usearch_free(handle,out _);handle=IntPtr.Zero;}
            if(view is not null){view.SafeMemoryMappedViewHandle.ReleasePointer();pointer=null;view.Dispose();view=null;}
            mapping?.Dispose();mapping=null;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct InitOptions
    {public int Metric;public IntPtr MetricFunction;public int Quantization;public nuint Dimensions,Connectivity,ExpansionAdd,ExpansionSearch;[MarshalAs(UnmanagedType.I1)] public bool Multi;}
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Filter(ulong key,IntPtr state);
    private const string Dll="libusearch_c";
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr usearch_init(ref InitOptions options,out IntPtr error);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void usearch_free(IntPtr h,out IntPtr e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern nuint usearch_size(IntPtr h,out IntPtr e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern nuint usearch_capacity(IntPtr h,out IntPtr e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void usearch_reserve(IntPtr h,nuint n,out IntPtr e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void usearch_change_threads_add(IntPtr h,nuint n,out IntPtr e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)][return:MarshalAs(UnmanagedType.I1)]private static extern bool usearch_contains(IntPtr h,ulong key,out IntPtr e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern nuint usearch_get(IntPtr h,ulong key,nuint count,IntPtr vector,int kind,out IntPtr e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void usearch_add(IntPtr h,ulong key,IntPtr vector,int kind,out IntPtr e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern nuint usearch_serialized_length(IntPtr h,out IntPtr e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void usearch_save_buffer(IntPtr h,IntPtr data,nuint size,out IntPtr e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void usearch_metadata_buffer(IntPtr data,nuint size,out InitOptions options,out IntPtr e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void usearch_view_buffer(IntPtr h,IntPtr data,nuint size,out IntPtr e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void usearch_load_buffer(IntPtr h,IntPtr data,nuint size,out IntPtr e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern nuint usearch_search(IntPtr h,IntPtr vector,int kind,nuint count,[Out]ulong[] keys,[Out]float[] distances,out IntPtr e);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern nuint usearch_filtered_search(IntPtr h,IntPtr vector,int kind,nuint count,Filter filter,IntPtr state,[Out]ulong[] keys,[Out]float[] distances,out IntPtr e);
}
internal static class NativeCountExtensions {internal static nuint WithCheck(this nuint count,IntPtr error){NativeVectorIndex.Check(error);return count;}}
