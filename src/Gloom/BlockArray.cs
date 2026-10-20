using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Gloom;

/// <summary>
/// A cache-line aligned array of 512-bit blocks, plus the probe kernels every filter shares.
/// The words live in an array on the pinned object heap, so <see cref="Words"/> is a stable
/// pointer for the array's lifetime: probes index it without bounds checks (a block index is
/// always below <see cref="NumBlocks"/> by construction of <see cref="BloomHash.ReduceRange"/>).
/// </summary>
internal sealed unsafe class BlockArray
{
    private const int CACHE_LINE_SIZE = 64;

    /// <summary>
    /// The largest block count a filter can hold: the backing <c>ulong[]</c> is limited to
    /// <see cref="Array.MaxLength"/> elements, ~17 GB of blocks.
    /// </summary>
    public const ulong MAX_NUM_BLOCKS = (0x7FFFFFC7UL - BloomParameters.BlockWords) / BloomParameters.BlockWords;

    // Keeps the pinned allocation alive; Words points into it.
    private readonly ulong[] _storage;

    public readonly ulong* Words;
    public readonly ulong  NumBlocks;

    public BlockArray(ulong numBlocks)
    {
        if (numBlocks == 0 || numBlocks > MAX_NUM_BLOCKS)
        {
            throw new ArgumentOutOfRangeException(nameof(numBlocks), numBlocks, $"The number of blocks must be between 1 and {MAX_NUM_BLOCKS}.");
        }

        NumBlocks = numBlocks;
        _storage  = GC.AllocateArray<ulong>((int)(numBlocks * BloomParameters.BlockWords) + BloomParameters.BlockWords, pinned: true);

        ulong* raw     = (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_storage));
        nuint  misalign = (nuint)raw % CACHE_LINE_SIZE;
        Words          = misalign == 0 ? raw : (ulong*)((byte*)raw + (CACHE_LINE_SIZE - misalign));
    }

    public ulong WordCount => NumBlocks * BloomParameters.BlockWords;

    /// <summary>The words as a span (for serialization; one span covers at most 2^31 words, which the block limit guarantees).</summary>
    public Span<ulong> AsSpan() => new(Words, (int)WordCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong* Block(ulong blockIndex) => Words + blockIndex * BloomParameters.BlockWords;

    /// <summary>Hints the CPU to pull the block into L1 ahead of a probe.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prefetch(ulong blockIndex)
    {
        if (Sse.IsSupported) Sse.Prefetch0(Block(blockIndex));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetBits(ulong* block, PrimePartition.Probe[] probes, uint h)
    {
        ref PrimePartition.Probe probe = ref MemoryMarshal.GetArrayDataReference(probes);

        for (int i = 0; i < probes.Length; i++)
        {
            uint bit = Unsafe.Add(ref probe, i).BitPosition(h);
            block[bit >> 6] |= 1UL << (int)bit;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TestBits(ulong* block, PrimePartition.Probe[] probes, uint h)
    {
        ref PrimePartition.Probe probe = ref MemoryMarshal.GetArrayDataReference(probes);

        for (int i = 0; i < probes.Length; i++)
        {
            uint bit = Unsafe.Add(ref probe, i).BitPosition(h);
            if ((block[bit >> 6] & (1UL << (int)bit)) == 0) return false;
        }

        return true;
    }

    /// <summary>
    /// Sets the probe bits with atomic ORs. A bit already set is left alone without the locked
    /// instruction: bits are only ever set, so a plain read that sees it set is final, and a
    /// re-added key then costs no cache-line ownership transfer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetBitsAtomic(ulong* block, PrimePartition.Probe[] probes, uint h)
    {
        ref PrimePartition.Probe probe = ref MemoryMarshal.GetArrayDataReference(probes);

        for (int i = 0; i < probes.Length; i++)
        {
            uint   bit  = Unsafe.Add(ref probe, i).BitPosition(h);
            ulong* word = block + (bit >> 6);
            ulong  mask = 1UL << (int)bit;

            if ((Volatile.Read(ref *word) & mask) == 0) Interlocked.Or(ref *word, mask);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TestBitsAtomic(ulong* block, PrimePartition.Probe[] probes, uint h)
    {
        ref PrimePartition.Probe probe = ref MemoryMarshal.GetArrayDataReference(probes);

        for (int i = 0; i < probes.Length; i++)
        {
            uint bit = Unsafe.Add(ref probe, i).BitPosition(h);
            if ((Volatile.Read(ref block[bit >> 6]) & (1UL << (int)bit)) == 0) return false;
        }

        return true;
    }

    /// <summary>The number of set bits in the whole array.</summary>
    public ulong PopCount() => PopCount(Words, WordCount);

    /// <summary>The number of set bits in one block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong PopCountBlock(ulong blockIndex)
    {
        ulong* b = Block(blockIndex);

        if (Avx512BW.IsSupported) return Vector512.Sum(PopCountLanes(Vector512.LoadAligned((byte*)b)));

        return (ulong)(BitOperations.PopCount(b[0]) + BitOperations.PopCount(b[1]) + BitOperations.PopCount(b[2]) + BitOperations.PopCount(b[3])
                     + BitOperations.PopCount(b[4]) + BitOperations.PopCount(b[5]) + BitOperations.PopCount(b[6]) + BitOperations.PopCount(b[7]));
    }

    /// <summary>
    /// Counts set bits over whole blocks: with AVX-512BW, Muła's nibble lookup (VPSHUFB) summed per
    /// 64-bit lane with VPSADBW, a block per vector and two accumulators; otherwise four independent
    /// scalar POPCNT chains. (.NET exposes no VPOPCNTQ.)
    /// </summary>
    private static ulong PopCount(ulong* p, ulong words)
    {
        ulong i = 0;

        if (Avx512BW.IsSupported)
        {
            Vector512<ulong> acc0 = Vector512<ulong>.Zero;
            Vector512<ulong> acc1 = Vector512<ulong>.Zero;

            for (; i + 16 <= words; i += 16)
            {
                acc0 += PopCountLanes(Vector512.LoadAligned((byte*)(p + i)));
                acc1 += PopCountLanes(Vector512.LoadAligned((byte*)(p + i + 8)));
            }

            for (; i + 8 <= words; i += 8) acc0 += PopCountLanes(Vector512.LoadAligned((byte*)(p + i)));

            return Vector512.Sum(acc0 + acc1);
        }

        ulong c0 = 0, c1 = 0, c2 = 0, c3 = 0;

        for (; i + 4 <= words; i += 4)
        {
            c0 += (ulong)BitOperations.PopCount(p[i]);
            c1 += (ulong)BitOperations.PopCount(p[i + 1]);
            c2 += (ulong)BitOperations.PopCount(p[i + 2]);
            c3 += (ulong)BitOperations.PopCount(p[i + 3]);
        }

        return c0 + c1 + c2 + c3;
    }

    /// <summary>The set-bit count of each 64-bit lane of <paramref name="v"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<ulong> PopCountLanes(Vector512<byte> v)
    {
        Vector512<byte> lut = Vector512.Create((byte)0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4,
                                                     0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4,
                                                     0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4,
                                                     0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4);
        Vector512<byte> nibble = Vector512.Create((byte)0x0F);

        Vector512<byte> low    = v & nibble;
        Vector512<byte> high   = (v.AsUInt16() >>> 4).AsByte() & nibble;
        Vector512<byte> counts = Avx512BW.Shuffle(lut, low) + Avx512BW.Shuffle(lut, high);

        return Avx512BW.SumAbsoluteDifferences(counts, Vector512<byte>.Zero).AsUInt64();
    }
}
