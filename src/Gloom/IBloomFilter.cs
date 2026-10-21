using UID;

namespace Gloom;

/// <summary>
/// The members shared by <see cref="BloomFilter"/>, <see cref="AtomicBloomFilter"/> and
/// <see cref="ShardedAtomicBloomFilter"/>, for code generic over the filter type.
/// </summary>
/// <remarks>
/// Calling through the interface is a virtual call and cannot be inlined, so prefer the concrete
/// types on hot paths. Serialization is not part of the interface: only <see cref="BloomFilter"/>
/// supports it.
/// </remarks>
public interface IBloomFilter
{
    /// <summary>Adds the key <paramref name="data"/>.</summary>
    void Add(ReadOnlySpan<byte> data);

    /// <summary>Adds the UTF-8 encoding of <paramref name="text"/>.</summary>
    void Add(ReadOnlySpan<char> text);

    /// <summary>Adds the 16 bytes of <paramref name="key"/>.</summary>
    void Add(UID128 key);

    /// <summary>Adds a key by its precomputed xxh3-128 hash (see <see cref="BloomHash"/>).</summary>
    void AddHash(UID128 hash);

    /// <summary>Reports whether <paramref name="data"/> may be in the filter. False positives are possible; false negatives are not.</summary>
    bool MayContain(ReadOnlySpan<byte> data);

    /// <summary>Reports whether the UTF-8 encoding of <paramref name="text"/> may be in the filter.</summary>
    bool MayContain(ReadOnlySpan<char> text);

    /// <summary>Reports whether <paramref name="key"/> may be in the filter.</summary>
    bool MayContain(UID128 key);

    /// <summary>Reports whether a key with the precomputed hash <paramref name="hash"/> may be in the filter.</summary>
    bool MayContainHash(UID128 hash);

    /// <summary>The capacity in bits.</summary>
    ulong Capacity { get; }

    /// <summary>The number of hash functions (bits set per key).</summary>
    uint K { get; }

    /// <summary>The approximate number of items added.</summary>
    ulong Count { get; }

    /// <summary>The total number of 512-bit blocks.</summary>
    ulong NumBlocks { get; }

    /// <summary>The fraction of set bits, by scanning the whole bit array.</summary>
    double EstimatedFillRatio();

    /// <summary>The fraction of set bits, estimated from <paramref name="sampleBlocks"/> evenly spaced blocks.</summary>
    double SampledFillRatio(int sampleBlocks);

    /// <summary>The estimated current false-positive rate.</summary>
    double EstimatedFalsePositiveRate();
}
