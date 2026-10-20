using System.Runtime.CompilerServices;

namespace Gloom;

/// <summary>
/// The partition of a 512-bit block into <see cref="K"/> pairwise-coprime segments used by the
/// one-hashing technique: probe <c>i</c> of a key sets bit <c>Offset[i] + h % Size[i]</c> of its
/// block. Port of <c>primePartitions</c>, <c>GetPrimePartition</c> and <c>ComputeOffsets</c>
/// (params.go).
/// </summary>
public sealed class PrimePartition
{
    /// <summary>The smallest supported number of partitions (hash functions).</summary>
    public const uint MinK = 3;

    /// <summary>
    /// The largest supported number of partitions: the 18 smallest distinct primes already sum
    /// to 501, leaving too little room for an 18th distinct coprime part.
    /// </summary>
    public const uint MaxK = 17;

    // Pairwise coprime, summing to exactly 512, as large and near-equal as possible.
    // FROZEN: persisted filters depend on these exact values and their order.
    private static readonly uint[][] s_sizes =
    [
        [167, 173, 172],                                                    // 3: 172 is even filler
        [109, 127, 137, 139],                                               // 4
        [97, 101, 103, 109, 102],                                           // 5: 102 is even filler
        [61, 79, 83, 89, 97, 103],                                          // 6
        [61, 67, 71, 79, 83, 89, 62],                                       // 7: 62 is even filler
        [37, 47, 53, 61, 67, 71, 79, 97],                                   // 8
        [41, 43, 47, 53, 59, 67, 71, 73, 58],                               // 9: 58 is even filler
        [31, 37, 41, 43, 47, 53, 59, 61, 67, 73],                           // 10
        [29, 31, 37, 41, 43, 44, 47, 53, 59, 61, 67],                       // 11: 44 is even filler
        [17, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 71],                   // 12
        [17, 19, 23, 29, 31, 37, 41, 43, 47, 52, 53, 59, 61],               // 13: 52 is even filler
        [11, 13, 17, 19, 23, 29, 31, 37, 41, 47, 53, 59, 61, 71],           // 14
        [11, 13, 17, 19, 23, 28, 29, 31, 37, 41, 43, 47, 53, 59, 61],       // 15: 28 is even filler
        [5, 7, 11, 13, 17, 19, 23, 27, 29, 31, 37, 41, 43, 47, 53, 109],    // 16: 27 = 3^3
        [3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 32, 37, 41, 43, 47, 53, 101], // 17: 32 = 2^5
    ];

    private static readonly PrimePartition[] s_partitions = CreateAll();

    private readonly uint[] _sizes;
    private readonly uint[] _offsets;

    /// <summary>One entry per probe, laid out contiguously for the hot loop.</summary>
    internal readonly Probe[] Probes;

    private PrimePartition(uint[] sizes)
    {
        _sizes   = sizes;
        _offsets = ComputeOffsets(sizes);
        Probes   = new Probe[sizes.Length];

        for (int i = 0; i < sizes.Length; i++)
        {
            Probes[i] = new Probe(sizes[i], _offsets[i]);
        }
    }

    /// <summary>The number of partitions, which is the number of bits set per key.</summary>
    public uint K => (uint)_sizes.Length;

    /// <summary>The partition sizes, in probe order.</summary>
    public ReadOnlySpan<uint> Sizes => _sizes;

    /// <summary>The bit offset of each partition within the block (the running sum of the sizes).</summary>
    public ReadOnlySpan<uint> Offsets => _offsets;

    /// <summary>Returns the partition for <paramref name="k"/>, or <c>null</c> when <paramref name="k"/> is unsupported.</summary>
    public static PrimePartition? For(uint k) => k is >= MinK and <= MaxK ? s_partitions[k - MinK] : null;

    /// <summary>Computes the cumulative bit offsets of a partition: <c>offset[i] = sum(sizes[0..i-1])</c>.</summary>
    public static uint[] ComputeOffsets(ReadOnlySpan<uint> sizes)
    {
        var  offsets    = new uint[sizes.Length];
        uint cumulative = 0;

        for (int i = 0; i < sizes.Length; i++)
        {
            offsets[i]  = cumulative;
            cumulative += sizes[i];
        }

        return offsets;
    }

    private static PrimePartition[] CreateAll()
    {
        var all = new PrimePartition[s_sizes.Length];
        for (int i = 0; i < all.Length; i++) all[i] = new PrimePartition(s_sizes[i]);
        return all;
    }

    /// <summary>
    /// One probe: the partition's size and offset, plus Lemire's fastmod multiplier
    /// <c>M = ceil(2^64 / size)</c>, so <c>h % size</c> is two multiplications instead of a
    /// hardware divide (exact for every 32-bit <c>h</c>).
    /// </summary>
    internal readonly struct Probe
    {
        public readonly ulong Multiplier;
        public readonly uint  Size;
        public readonly uint  Offset;

        public Probe(uint size, uint offset)
        {
            Multiplier = ulong.MaxValue / size + 1;
            Size       = size;
            Offset     = offset;
        }

        /// <summary>The bit position of this probe within the block for intra-block hash <paramref name="h"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint BitPosition(uint h) => Offset + (uint)Math.BigMul(Multiplier * h, Size, out _);
    }
}
