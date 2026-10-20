using System.Runtime.CompilerServices;
using UID;

namespace Gloom;

/// <summary>
/// A bloom filter using cache-line blocked one-hashing: every key touches exactly one 512-bit
/// block, and its <see cref="K"/> bits come from one 32-bit hash taken modulo <see cref="K"/>
/// pairwise-coprime partition sizes. Port of <c>Filter</c> (bloom.go).
/// </summary>
/// <remarks>
/// Not thread-safe: synchronize concurrent writers yourself, or use
/// <see cref="AtomicBloomFilter"/> / <see cref="ShardedAtomicBloomFilter"/>. This is the only
/// serializable filter (<see cref="Serialize()"/>, <see cref="Deserialize"/>); the format is the
/// Go library's, so filters move between the two.
/// </remarks>
public sealed unsafe partial class BloomFilter : IBloomFilter
{
    private const int BATCH = 16;

    private readonly BlockArray               _blocks;
    private readonly PrimePartition.Probe[]   _probes;
    private readonly ulong                    _numBlocks;
    private readonly uint                     _k;
    private          ulong                    _count;

    /// <summary>Creates a filter sized for <paramref name="expectedItems"/> at <paramref name="falsePositiveRate"/> (see <see cref="BloomParameters.Optimal"/>).</summary>
    public BloomFilter(ulong expectedItems, double falsePositiveRate)
        : this(BloomParameters.Optimal(expectedItems, falsePositiveRate)) { }

    private BloomFilter((ulong NumBlocks, uint K, double _) p) : this(p.NumBlocks, p.K, 0) { }

    private BloomFilter(ulong numBlocks, uint k, ulong count)
    {
        numBlocks = FilterArguments.NormalizeNumBlocks(numBlocks);
        var partition = FilterArguments.PartitionOrDefault(ref k);

        _blocks    = new BlockArray(numBlocks);
        _probes    = partition.Probes;
        _numBlocks = numBlocks;
        _k         = k;
        _count     = count;
    }

    /// <summary>
    /// Creates a filter with <paramref name="numBlocks"/> 512-bit blocks and <paramref name="k"/>
    /// hash functions. Zero blocks becomes one; an unsupported <paramref name="k"/> becomes 7.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="numBlocks"/> exceeds <see cref="MaxNumBlocks"/>.</exception>
    public static BloomFilter WithParameters(ulong numBlocks, uint k) => new(numBlocks, k, 0);

    /// <summary>The largest supported block count.</summary>
    public static ulong MaxNumBlocks => BlockArray.MAX_NUM_BLOCKS;

    /// <inheritdoc/>
    public ulong Capacity => _numBlocks * BloomParameters.BlockBits;

    /// <inheritdoc/>
    public uint K => _k;

    /// <inheritdoc/>
    public ulong Count => _count;

    /// <inheritdoc/>
    public ulong NumBlocks => _numBlocks;

    internal BlockArray Blocks => _blocks;

    /// <inheritdoc/>
    public void Add(ReadOnlySpan<byte> data) => AddHash(BloomHash.Hash(data));

    /// <inheritdoc/>
    public void Add(ReadOnlySpan<char> text) => AddHash(BloomHash.Hash(text));

    /// <summary>Adds the UTF-8 encoding of <paramref name="text"/>.</summary>
    public void Add(string text) => AddHash(BloomHash.Hash(text.AsSpan()));

    /// <inheritdoc/>
    public void Add(UID128 key) => AddHash(BloomHash.Hash(key));

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddHash(UID128 hash)
    {
        BlockArray.SetBits(_blocks.Block(BloomHash.ReduceRange(hash.High, _numBlocks)), _probes, BloomHash.FoldIntra(hash.Low));
        _count++;
    }

    /// <summary>Adds every key in <paramref name="keys"/>, prefetching blocks ahead of the probes.</summary>
    public void Add(ReadOnlySpan<UID128> keys) => AddBatch(keys, hashed: false);

    /// <summary>Adds every precomputed hash in <paramref name="hashes"/>, prefetching blocks ahead of the probes.</summary>
    public void AddHashes(ReadOnlySpan<UID128> hashes) => AddBatch(hashes, hashed: true);

    /// <inheritdoc/>
    public bool MayContain(ReadOnlySpan<byte> data) => MayContainHash(BloomHash.Hash(data));

    /// <inheritdoc/>
    public bool MayContain(ReadOnlySpan<char> text) => MayContainHash(BloomHash.Hash(text));

    /// <summary>Reports whether the UTF-8 encoding of <paramref name="text"/> may be in the filter.</summary>
    public bool MayContain(string text) => MayContainHash(BloomHash.Hash(text.AsSpan()));

    /// <inheritdoc/>
    public bool MayContain(UID128 key) => MayContainHash(BloomHash.Hash(key));

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MayContainHash(UID128 hash)
        => BlockArray.TestBits(_blocks.Block(BloomHash.ReduceRange(hash.High, _numBlocks)), _probes, BloomHash.FoldIntra(hash.Low));

    /// <summary>
    /// Tests every key in <paramref name="keys"/>, writing each answer to <paramref name="results"/>,
    /// and returns how many may be present. Blocks are prefetched ahead of the probes.
    /// </summary>
    public int MayContain(ReadOnlySpan<UID128> keys, Span<bool> results) => TestBatch(keys, results, hashed: false);

    /// <summary>As <see cref="MayContain(ReadOnlySpan{UID128}, Span{bool})"/>, for precomputed hashes.</summary>
    public int MayContainHashes(ReadOnlySpan<UID128> hashes, Span<bool> results) => TestBatch(hashes, results, hashed: true);

    private void AddBatch(ReadOnlySpan<UID128> keys, bool hashed)
    {
        Span<ulong> blocks = stackalloc ulong[BATCH];
        Span<uint>  intra  = stackalloc uint[BATCH];

        for (int start = 0; start < keys.Length; start += BATCH)
        {
            int n = Math.Min(BATCH, keys.Length - start);

            for (int j = 0; j < n; j++)
            {
                UID128 h  = hashed ? keys[start + j] : BloomHash.Hash(keys[start + j]);
                blocks[j] = BloomHash.ReduceRange(h.High, _numBlocks);
                intra[j]  = BloomHash.FoldIntra(h.Low);
                _blocks.Prefetch(blocks[j]);
            }

            for (int j = 0; j < n; j++) BlockArray.SetBits(_blocks.Block(blocks[j]), _probes, intra[j]);
        }

        _count += (ulong)keys.Length;
    }

    private int TestBatch(ReadOnlySpan<UID128> keys, Span<bool> results, bool hashed)
    {
        if (results.Length < keys.Length) throw new ArgumentException("The results span is shorter than the keys.", nameof(results));

        Span<ulong> blocks = stackalloc ulong[BATCH];
        Span<uint>  intra  = stackalloc uint[BATCH];
        int         found  = 0;

        for (int start = 0; start < keys.Length; start += BATCH)
        {
            int n = Math.Min(BATCH, keys.Length - start);

            for (int j = 0; j < n; j++)
            {
                UID128 h  = hashed ? keys[start + j] : BloomHash.Hash(keys[start + j]);
                blocks[j] = BloomHash.ReduceRange(h.High, _numBlocks);
                intra[j]  = BloomHash.FoldIntra(h.Low);
                _blocks.Prefetch(blocks[j]);
            }

            for (int j = 0; j < n; j++)
            {
                bool hit           = BlockArray.TestBits(_blocks.Block(blocks[j]), _probes, intra[j]);
                results[start + j] = hit;
                found             += hit ? 1 : 0;
            }
        }

        return found;
    }

    /// <inheritdoc/>
    /// <remarks>Scans the whole bit array; prefer <see cref="SampledFillRatio"/> for frequent monitoring of large filters.</remarks>
    public double EstimatedFillRatio() => _blocks.PopCount() / (double)Capacity;

    /// <inheritdoc/>
    /// <remarks>
    /// <paramref name="sampleBlocks"/> values &lt;= 0 or not below the block count fall back to a
    /// full scan. A few thousand blocks estimate the ratio to well under 1% relative error at any size.
    /// </remarks>
    public double SampledFillRatio(int sampleBlocks) => FilterArguments.SampledFillRatio(_blocks, sampleBlocks);

    /// <inheritdoc/>
    public double EstimatedFalsePositiveRate() => BloomParameters.EstimateFalsePositiveRate(_numBlocks, _k, _count);
}
