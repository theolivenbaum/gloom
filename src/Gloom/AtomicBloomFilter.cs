using System.Runtime.CompilerServices;
using UID;

namespace Gloom;

/// <summary>
/// A thread-safe bloom filter with the layout of <see cref="BloomFilter"/>: bits are set with
/// lock-free atomic ORs, and the item count is striped across cache lines so concurrent writers
/// scale. Port of <c>AtomicFilter</c> (bloom.go).
/// </summary>
/// <remarks>
/// <see cref="Add(ReadOnlySpan{byte})"/> and <see cref="MayContain(ReadOnlySpan{byte})"/> may be
/// called concurrently from any number of threads. Deliberately not serializable: an add sets
/// <see cref="K"/> bits in separate words, so a snapshot taken while writers run can capture an
/// item half-added and a count that disagrees with the bits, which would load as a filter with
/// false negatives. Stop the writers and copy into a <see cref="BloomFilter"/> instead.
/// </remarks>
public sealed unsafe class AtomicBloomFilter : IBloomFilter
{
    private const int BATCH = 16;

    private readonly BlockArray             _blocks;
    private readonly PrimePartition.Probe[] _probes;
    private readonly ulong                  _numBlocks;
    private readonly uint                   _k;
    private readonly StripedCounter         _count;

    /// <summary>Creates a filter sized for <paramref name="expectedItems"/> at <paramref name="falsePositiveRate"/> (see <see cref="BloomParameters.Optimal"/>).</summary>
    public AtomicBloomFilter(ulong expectedItems, double falsePositiveRate)
        : this(BloomParameters.Optimal(expectedItems, falsePositiveRate)) { }

    private AtomicBloomFilter((ulong NumBlocks, uint K, double _) p) : this(p.NumBlocks, p.K) { }

    private AtomicBloomFilter(ulong numBlocks, uint k)
    {
        numBlocks     = FilterArguments.NormalizeNumBlocks(numBlocks);
        var partition = FilterArguments.PartitionOrDefault(ref k);

        _blocks    = new BlockArray(numBlocks);
        _probes    = partition.Probes;
        _numBlocks = numBlocks;
        _k         = k;
        _count     = new StripedCounter();
    }

    /// <summary>
    /// Creates a filter with <paramref name="numBlocks"/> 512-bit blocks and <paramref name="k"/>
    /// hash functions. Zero blocks becomes one; an unsupported <paramref name="k"/> becomes 7.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="numBlocks"/> exceeds <see cref="BloomFilter.MaxNumBlocks"/>.</exception>
    public static AtomicBloomFilter WithParameters(ulong numBlocks, uint k) => new(numBlocks, k);

    /// <inheritdoc/>
    public ulong Capacity => _numBlocks * BloomParameters.BlockBits;

    /// <inheritdoc/>
    public uint K => _k;

    /// <inheritdoc/>
    public ulong Count => _count.Load();

    /// <inheritdoc/>
    public ulong NumBlocks => _numBlocks;

    internal BlockArray Blocks => _blocks;

    internal StripedCounter Counter => _count;

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
    public void AddHash(UID128 hash) => AddAt(BloomHash.ReduceRange(hash.High, _numBlocks), BloomHash.FoldIntra(hash.Low));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddAt(ulong block, uint intra)
    {
        BlockArray.SetBitsAtomic(_blocks.Block(block), _probes, intra);

        // Striped by block index so concurrent writers spread over the counter's cache lines.
        _count.Increment(block);
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
    public bool MayContainHash(UID128 hash) => MayContainAt(BloomHash.ReduceRange(hash.High, _numBlocks), BloomHash.FoldIntra(hash.Low));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool MayContainAt(ulong block, uint intra) => BlockArray.TestBitsAtomic(_blocks.Block(block), _probes, intra);

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

            for (int j = 0; j < n; j++) AddAt(blocks[j], intra[j]);
        }
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
                bool hit           = MayContainAt(blocks[j], intra[j]);
                results[start + j] = hit;
                found             += hit ? 1 : 0;
            }
        }

        return found;
    }

    internal ulong SetBitCount() => _blocks.PopCount();

    /// <inheritdoc/>
    /// <remarks>Scans the whole bit array; prefer <see cref="SampledFillRatio"/> for frequent monitoring of large filters.</remarks>
    public double EstimatedFillRatio() => SetBitCount() / (double)Capacity;

    /// <inheritdoc/>
    public double SampledFillRatio(int sampleBlocks) => FilterArguments.SampledFillRatio(_blocks, sampleBlocks);

    /// <inheritdoc/>
    public double EstimatedFalsePositiveRate() => BloomParameters.EstimateFalsePositiveRate(_numBlocks, _k, _count.Load());
}
