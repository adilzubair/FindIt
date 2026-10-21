using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace FindIt.Core;

internal static class VectorEncoding
{
    public static byte[]? Encode(float[]? vector, int dimension = 512)
    {
        if (vector is null || vector.Length < dimension) return null;
        double sum = 0;
        for (int i = 0; i < dimension; i++)
        {
            if (!float.IsFinite(vector[i])) throw new InvalidDataException("The embedding provider returned non-finite values.");
            sum += (double)vector[i] * vector[i];
        }
        if (sum <= 1e-20) throw new InvalidDataException("The embedding provider returned a zero vector.");
        var scale = 1.0 / Math.Sqrt(sum);
        var bytes = new byte[dimension * 2];
        for (int i = 0; i < dimension; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), BitConverter.HalfToInt16Bits((Half)(vector[i] * scale)));
        return bytes;
    }

    public static float[] Decode(ReadOnlySpan<byte> bytes)
    {
        var result = new float[bytes.Length / 2];
        for (int i = 0; i < result.Length; i++) result[i] = (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2)));
        return result;
    }
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return -1;
        double dot = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; aa += (double)a[i] * a[i]; bb += (double)b[i] * b[i]; }
        return aa > 0 && bb > 0 ? dot / Math.Sqrt(aa * bb) : -1;
    }
}

/// <summary>Truthful exact baseline; native ANN can replace candidate generation without changing canonical FP16 packs.</summary>
internal sealed unsafe class ExactVectorIndex : IDisposable
{
    private readonly List<MappedPack> packs = [];
    private readonly MappedPack records;
    private readonly int dimension;
    public long Count { get; }
    public ExactVectorIndex(string root, string indexPath, IReadOnlyList<string> packPaths, int dimension)
    {
        this.dimension = dimension;
        records = new MappedPack(indexPath);
        if (records.Length % 20 != 0) { records.Dispose(); throw new InvalidDataException("Invalid vector index size."); }
        Count = records.Length / 20;
        try { foreach (var path in packPaths) packs.Add(new MappedPack(PortablePaths.Resolve(root, path, true))); }
        catch { Dispose(); throw; }
    }

    public IReadOnlyList<(long Id, double Score)> Search(float[] query, int limit, Func<long, bool>? accept, CancellationToken cancellationToken)
    {
        var encoded = VectorEncoding.Encode(query, dimension) ?? throw new InvalidDataException("Query embedding has the wrong dimension.");
        var normalized = VectorEncoding.Decode(encoded);
        double queryNorm = normalized.Sum(v => (double)v * v);
        var queue = new PriorityQueue<(long Id, double Score), double>();
        for (long row = 0; row < Count; row++)
        {
            if ((row & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var record = records.Pointer + row * 20;
            long id = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(record, 8));
            if (accept is not null && !accept(id)) continue;
            int packId = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(record + 8, 4));
            long offset = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(record + 12, 8));
            if ((uint)packId >= packs.Count || offset < 0 || offset > packs[packId].Length - dimension * 2) throw new InvalidDataException("Invalid vector pack reference.");
            var pointer = packs[packId].Pointer + offset;
            double dot = 0, norm = 0;
            for (int d = 0; d < dimension; d++)
            {
                var v = (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(new ReadOnlySpan<byte>(pointer + d * 2, 2)));
                dot += v * normalized[d]; norm += (double)v * v;
            }
            double score = norm > 0 ? dot / Math.Sqrt(norm * queryNorm) : -1;
            if (!double.IsFinite(score)) continue;
            if (queue.Count < limit) queue.Enqueue((id, score), score);
            else if (queue.TryPeek(out _, out double minimum) && score > minimum) { queue.Dequeue(); queue.Enqueue((id, score), score); }
        }
        return queue.UnorderedItems.Select(p => p.Element).OrderByDescending(p => p.Score).ToArray();
    }
    public void Dispose() { records.Dispose(); foreach (var pack in packs) pack.Dispose(); packs.Clear(); }

    private sealed class MappedPack : IDisposable
    {
        private readonly MemoryMappedFile? file;
        private readonly MemoryMappedViewAccessor? view;
        private byte* pointer;
        public byte* Pointer => pointer;
        public long Length { get; }
        public MappedPack(string path)
        {
            Length = new FileInfo(path).Length;
            if (Length == 0) return;
            file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* basePointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePointer);
            pointer = basePointer + view.PointerOffset;
        }
        public void Dispose()
        {
            if (pointer != null) { view!.SafeMemoryMappedViewHandle.ReleasePointer(); pointer = null; }
            view?.Dispose(); file?.Dispose();
        }
    }
}
