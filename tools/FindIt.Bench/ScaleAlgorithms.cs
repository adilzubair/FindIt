using System.Buffers.Binary;
using System.Numerics;

namespace FindIt.Bench;

// Kept separate from the runner so correctness of the reference search is testable without native binaries.
public static class ScaleAlgorithms
{
    public const int Dimensions = 512;
    public const int BytesPerVector = Dimensions * 2;
    public const int TopK = 50;
    public const int ClusterCount = 256;
    public static readonly string[] Distributions = ["isotropic-512", "clustered-512"];
    public static readonly int[] FilterModuli = [1, 10, 100];

    /// <summary>SplitMix64 is specified here, avoiding System.Random version or process state dependencies.</summary>
    public static float[] Vector(int seed, ulong row, string distribution, bool query = false)
    {
        if (!Distributions.Contains(distribution)) throw new ArgumentException("Unknown distribution.", nameof(distribution));
        ulong state = unchecked((ulong)(uint)seed * 0x9e3779b97f4a7c15UL + row * 0xd1b54a32d192ed03UL + (query ? 0xa0761d6478bd642fUL : 0xe7037ed1a0b428dbUL));
        var result = new float[Dimensions];
        ulong centroid = unchecked((ulong)(uint)seed ^ (row % ClusterCount) * 0x94d049bb133111ebUL);
        double norm = 0;
        for (int d = 0; d < result.Length; d++)
        {
            float noise = Next(ref state);
            // Held-out queries share only a centroid with corpus vectors, never the noise or vector itself.
            result[d] = distribution == "clustered-512" ? Next(ref centroid) + .45f * noise : noise;
            norm += (double)result[d] * result[d];
        }
        double scale = 1 / Math.Sqrt(norm);
        for (int d = 0; d < result.Length; d++) result[d] = (float)(Half)(result[d] * scale);
        return result;
    }

    private static float Next(ref ulong state)
    {
        ulong z = unchecked(state += 0x9e3779b97f4a7c15UL);
        z = unchecked((z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94d049bb133111ebUL);
        z ^= z >> 31;
        return ((z >> 40) / 8388608f) - 1f;
    }

    public static void Encode(ReadOnlySpan<float> vector, Span<byte> target)
    {
        if (vector.Length != Dimensions || target.Length != BytesPerVector) throw new ArgumentException("Invalid vector dimensions.");
        for (int d = 0; d < Dimensions; d++) BinaryPrimitives.WriteInt16LittleEndian(target.Slice(d * 2, 2), BitConverter.HalfToInt16Bits((Half)vector[d]));
    }

    public static void Decode(ReadOnlySpan<byte> source, Span<float> vector)
    {
        if (vector.Length != Dimensions || source.Length != BytesPerVector) throw new ArgumentException("Invalid vector dimensions.");
        for (int d = 0; d < Dimensions; d++) vector[d] = (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(source.Slice(d * 2, 2)));
    }

    public static bool Accept(ulong key, int modulus) => modulus == 1 || key % (ulong)modulus == (modulus == 10 ? 3UL : 17UL);
    public static long EligibleCount(long count, int modulus)
    {
        long first = modulus == 1 ? 0 : modulus == 10 ? 3 : 17;
        return count <= first ? 0 : 1 + (count - 1 - first) / modulus;
    }

    public static double Norm(float[] vector)
    {
        double sum = 0;
        foreach (float value in vector) sum += (double)value * value;
        return Math.Sqrt(sum);
    }

    public static double Cosine(float[] left, float[] right, double leftNorm, double rightNorm)
    {
        // Exhaustive scoring, SIMD float products and double accumulation. Every eligible corpus row is scored.
        double dot = 0;
        int d = 0;
        for (; d <= left.Length - Vector<float>.Count; d += Vector<float>.Count)
            dot += System.Numerics.Vector.Dot(new Vector<float>(left, d), new Vector<float>(right, d));
        for (; d < left.Length; d++) dot += (double)left[d] * right[d];
        return dot / (leftNorm * rightNorm);
    }

    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    }
}

public sealed record ExactNeighbor(ulong Key, double Score);

public sealed class ExactTopK
{
    private readonly int count;
    private readonly PriorityQueue<ExactNeighbor, (double Score, long ReverseKey)> queue = new();
    public ExactTopK(int count = ScaleAlgorithms.TopK) { this.count = count; }
    public void Add(ulong key, double score)
    {
        var priority = (score, -checked((long)key));
        if (queue.Count < count) queue.Enqueue(new(key, score), priority);
        else if (queue.TryPeek(out _, out var minimum) && priority.CompareTo(minimum) > 0)
        {
            queue.Dequeue(); queue.Enqueue(new(key, score), priority);
        }
    }
    public ExactNeighbor[] Results() => queue.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Score).ThenBy(x => x.Key).ToArray();
}
