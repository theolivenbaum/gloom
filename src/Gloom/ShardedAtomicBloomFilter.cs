using System.Numerics;
using System.Runtime.CompilerServices;
using UID;

namespace Gloom;

/// <summary>
/// A thread-safe bloom filter split into a power-of-two number of independent
/// <see cref="AtomicBloomFilter"/> shards, for the highest write concurrency. A key's shard is
/// the low bits of its hash's <see cref="UID128.High"/> half; its block within the shard is the
/// multiply-shift reduction of the same half, which is dominated by its high bits, so the two
/// choices are effectively independent. Port of <c>ShardedAtomicFilter</c> (bloom.go).
/// </summary>
/// <remarks>Not serializable, for the reason given on <see cref="AtomicBloomFilter"/>.</remarks>
public sealed class ShardedAtomicBloomFilter : IBloomFilter
{
    private readonly AtomicBloomFilter[] _shards;
    private readonly ulong               _mask;

    /// <summary>
    /// Creates a filter of <paramref name="numShards"/> shards (rounded up to a power of two),
    /// the capacity divided evenly between them.
    /// </summary>
    public ShardedAtomicBloomFilter(ulong expectedItems, double falsePositiveRate, ulong numShards)
    {
        numShards          = NextPowerOf2(numShards);
        ulong itemsPerShard = expectedItems / numShards + (expectedItems % numShards == 0 ? 0UL : 1UL);

        _shards = new AtomicBloomFilter[numShards];
        for (int i = 0; i < _shards.Length; i++) _shards[i] = new AtomicBloomFilter(itemsPerShard, falsePositiveRate);

        _mask = numShards - 1;
    }

    /// <summary>Creates a filter with one shard per processor, at least four.</summary>
    public ShardedAtomicBloomFilter(ulong expectedItems, double falsePositiveRate)
        : this(expectedItems, falsePositiveRate, (ulong)Math.Max(Environment.ProcessorCount, 4)) { }

    /// <summary>The number of shards.</summary>
    public ulong NumShards => (ulong)_shards.Length;

    /// <inheritdoc/>
    public ulong Capacity
    {
        get
        {
            ulong total = 0;
            foreach (var shard in _shards) total += shard.Capacity;
            return total;
        }
    }

    /// <inheritdoc/>
    public uint K => _shards[0].K;

    /// <inheritdoc/>
    public ulong Count
    {
        get
        {
            ulong total = 0;
            foreach (var shard in _shards) total += shard.Count;
            return total;
        }
    }

    /// <inheritdoc/>
    public ulong NumBlocks
    {
        get
        {
            ulong total = 0;
            foreach (var shard in _shards) total += shard.NumBlocks;
            return total;
        }
    }

    internal ReadOnlySpan<AtomicBloomFilter> Shards => _shards;

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
        var shard = _shards[(int)(hash.High & _mask)];
        shard.AddAt(BloomHash.ReduceRange(hash.High, shard.NumBlocks), BloomHash.FoldIntra(hash.Low));
    }

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
    {
        var shard = _shards[(int)(hash.High & _mask)];
        return shard.MayContainAt(BloomHash.ReduceRange(hash.High, shard.NumBlocks), BloomHash.FoldIntra(hash.Low));
    }

    /// <inheritdoc/>
    /// <remarks>Scans every shard's whole bit array; prefer <see cref="SampledFillRatio"/> for frequent monitoring.</remarks>
    public double EstimatedFillRatio()
    {
        ulong totalBits = 0, setBits = 0;

        foreach (var shard in _shards)
        {
            totalBits += shard.Capacity;
            setBits   += shard.SetBitCount();
        }

        return setBits / (double)totalBits;
    }

    /// <inheritdoc/>
    /// <remarks>The sample budget is divided evenly across shards (at least one block each); &lt;= 0 scans everything.</remarks>
    public double SampledFillRatio(int sampleBlocks)
    {
        if (sampleBlocks <= 0) return EstimatedFillRatio();

        int    perShard = Math.Max(sampleBlocks / _shards.Length, 1);
        double sum      = 0;

        foreach (var shard in _shards) sum += shard.SampledFillRatio(perShard);

        return sum / _shards.Length;
    }

    /// <inheritdoc/>
    /// <remarks>The average of the shards' estimates.</remarks>
    public double EstimatedFalsePositiveRate()
    {
        double sum = 0;
        foreach (var shard in _shards) sum += shard.EstimatedFalsePositiveRate();
        return sum / _shards.Length;
    }

    /// <summary>The smallest power of two &gt;= <paramref name="n"/> (1 for 0, 2^63 above it).</summary>
    internal static ulong NextPowerOf2(ulong n)
    {
        if (n == 0) return 1;
        if (n > 1UL << 63) return 1UL << 63;
        return BitOperations.RoundUpToPowerOf2(n);
    }
}
