using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using UID;

namespace Gloom;

/// <summary>
/// A classic bloom filter of <see cref="Capacity"/> bits and <see cref="K"/> probes per key, keyed
/// by <see cref="UID128"/>. Port of <c>BloomFilter</c> from bits-and-blooms/bloom
/// (<c>.reference/bloom</c>), not bit-compatible with it.
/// </summary>
/// <remarks>
/// <para>
/// A key is taken to be hash-like already (a random <see cref="UID128.New"/>, or a hash such as
/// <c>Hashes.Hash128</c>), so it is not hashed again: its halves drive Kirsch–Mitzenmacher double
/// hashing, probe <c>i</c> landing on bit <c>⌊(High + i·Low) · m / 2^64⌋</c> (a multiply-shift
/// reduction, no divide). A key that is not uniformly distributed — a counter, a structured id —
/// must be hashed first, or the false-positive rate is not what <see cref="WithEstimates"/> promises.
/// </para>
/// <para>
/// Unlike <see cref="BloomFilter"/>, the k probes land on k different cache lines; prefer
/// <see cref="BloomFilter"/> when lookup latency matters more than bits per key. Not thread-safe.
/// </para>
/// </remarks>
public sealed unsafe class Bloom : IEquatable<Bloom>
{
    private const int  CACHE_LINE_SIZE = 64;
    private const byte FORMAT_MAGIC    = (byte)'B';
    private const byte FORMAT_VERSION  = 1;

    /// <summary>Format byte, version byte, k (uint32), m (uint64).</summary>
    public const int HeaderSize = 14;

    /// <summary>The trailing CRC-32C.</summary>
    public const int ChecksumSize = 4;

    /// <summary>The largest supported number of bits, the limit of one <c>ulong[]</c>.</summary>
    public const ulong MaxBits = (0x7FFFFFC7UL - 8) * 64;

    // Keeps the pinned allocation alive; _words points into it, aligned to a cache line.
    private readonly ulong[] _storage;
    private readonly ulong*  _words;
    private readonly ulong   _wordCount;
    private readonly ulong   _m;
    private readonly uint    _k;
    private readonly bool    _gather;

    /// <summary>Creates a filter of <paramref name="m"/> bits and <paramref name="k"/> probes per key (both at least 1).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="m"/> exceeds <see cref="MaxBits"/>.</exception>
    public Bloom(ulong m, uint k)
    {
        m = Math.Max(m, 1);
        k = Math.Max(k, 1);

        if (m > MaxBits) throw new ArgumentOutOfRangeException(nameof(m), m, $"A filter holds at most {MaxBits} bits.");

        _m         = m;
        _k         = k;
        _wordCount = (m + 63) / 64;
        _gather    = CanGather(m);
        _storage   = GC.AllocateArray<ulong>((int)_wordCount + CACHE_LINE_SIZE / sizeof(ulong), pinned: true);

        ulong* raw      = (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_storage));
        nuint  misalign = (nuint)raw % CACHE_LINE_SIZE;
        _words          = misalign == 0 ? raw : (ulong*)((byte*)raw + (CACHE_LINE_SIZE - misalign));
    }

    /// <summary>Creates a filter sized for <paramref name="n"/> items at false-positive rate <paramref name="falsePositiveRate"/> (see <see cref="EstimateParameters"/>).</summary>
    public static Bloom WithEstimates(ulong n, double falsePositiveRate)
    {
        var (m, k) = EstimateParameters(n, falsePositiveRate);
        return new Bloom(m, k);
    }

    /// <summary>Creates a filter over a copy of <paramref name="data"/>: <c>data.Length · 64</c> bits, <paramref name="k"/> probes.</summary>
    public static Bloom From(ReadOnlySpan<ulong> data, uint k) => From(data, (ulong)data.Length * 64, k);

    /// <summary>Creates a filter of <paramref name="m"/> bits over a copy of <paramref name="data"/>, which must hold at least <c>⌈m/64⌉</c> words.</summary>
    public static Bloom From(ReadOnlySpan<ulong> data, ulong m, uint k)
    {
        var f = new Bloom(m, k);
        if ((ulong)data.Length < f._wordCount) throw new ArgumentException($"{m} bits need {f._wordCount} words.", nameof(data));

        data[..(int)f._wordCount].CopyTo(f.Words);
        f.ClearTail();
        return f;
    }

    /// <summary>
    /// The bit count and probe count for <paramref name="n"/> items at <paramref name="p"/>:
    /// <c>m = ⌈-n·ln p / ln²2⌉</c>, <c>k = ⌈ln 2 · m / n⌉</c>.
    /// </summary>
    public static (ulong M, uint K) EstimateParameters(ulong n, double p)
    {
        n = Math.Max(n, 1);
        if (!(p > 0 && p < 1)) throw new ArgumentOutOfRangeException(nameof(p), p, "The false-positive rate must be in (0, 1).");

        ulong m = (ulong)Math.Ceiling(-1 * (double)n * Math.Log(p) / (Math.Log(2) * Math.Log(2)));
        uint  k = (uint)Math.Ceiling(Math.Log(2) * m / n);
        return (m, k);
    }

    /// <summary>The analytic false-positive rate of <paramref name="m"/> bits and <paramref name="k"/> probes after <paramref name="n"/> insertions: <c>(1 - e^(-kn/m))^k</c>.</summary>
    public static double TheoreticalFalsePositiveRate(ulong m, uint k, ulong n)
        => n == 0 ? 0 : Math.Pow(1 - Math.Exp(-(double)k * n / m), k);

    /// <summary>
    /// Measures the false-positive rate of a fresh filter of <paramref name="m"/> bits and
    /// <paramref name="k"/> probes holding <paramref name="n"/> random keys, over
    /// <paramref name="rounds"/> other random keys. Slow; meant for validating parameters.
    /// </summary>
    public static double EstimateFalsePositiveRate(ulong m, uint k, ulong n, int rounds = 100_000)
    {
        var f = new Bloom(m, k);
        for (ulong i = 0; i < n; i++) f.Add(SampleKey(i));

        int fp = 0;
        for (int i = 0; i < rounds; i++)
        {
            if (f.MayContain(SampleKey(n + 1 + (ulong)i))) fp++;
        }

        return fp / (double)rounds;
    }

    /// <summary>A deterministic hash-like key: SplitMix64 of <c>2i</c> and <c>2i+1</c>.</summary>
    internal static UID128 SampleKey(ulong i) => new(SplitMix64(2 * i), SplitMix64(2 * i + 1));

    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x  = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x  = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>The number of bits, <c>m</c>.</summary>
    public ulong Capacity => _m;

    /// <summary>The number of probes (hash functions) per key.</summary>
    public uint K => _k;

    /// <summary>The bit array, <c>⌈m/64⌉</c> words; bits at and above <c>m</c> are always zero.</summary>
    public Span<ulong> Words => new(_words, (int)_wordCount);

    /// <summary>The raw 64-bit hash of probe <paramref name="i"/> of <paramref name="key"/>, before the reduction to <c>[0, m)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Location(UID128 key, uint i) => key.High + i * key.Low;

    /// <summary>The raw probe hashes of <paramref name="key"/> for <paramref name="k"/> probes (see <see cref="MayContainLocations"/>).</summary>
    public static ulong[] Locations(UID128 key, uint k)
    {
        var locations = new ulong[k];
        for (uint i = 0; i < k; i++) locations[i] = Location(key, i);
        return locations;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong Bit(ulong location) => Math.BigMul(location, _m, out _);

    /// <summary>Adds <paramref name="key"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(UID128 key)
    {
        ulong x = key.High, step = key.Low;
        for (uint i = 0; i < _k; i++, x += step)
        {
            ulong bit = Bit(x);
            _words[bit >> 6] |= 1UL << (int)bit;
        }
    }

    /// <summary>Reports whether <paramref name="key"/> may be in the filter. False positives are possible; false negatives are not.</summary>
    /// <remarks>
    /// With AVX-512, eight probes are computed and gathered at once and tested without a branch per
    /// probe: a scalar early exit mispredicts on about half the lookups that miss, which costs more
    /// than the probes it skips (measured: 14.7 ns scalar, 6.8 ns gathered, for a miss on a 1M-key
    /// filter; a hit costs the same either way).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MayContain(UID128 key) => _gather ? MayContainGather(key) : MayContainScalar(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool MayContainScalar(UID128 key)
    {
        ulong x = key.High, step = key.Low;
        for (uint i = 0; i < _k; i++, x += step)
        {
            ulong bit = Bit(x);
            if ((_words[bit >> 6] & (1UL << (int)bit)) == 0) return false;
        }

        return true;
    }

    /// <summary>Whether <see cref="MayContainGather"/> can run: AVX-512F/DQ and AVX2, and <c>m &lt; 2^32</c> (32×32-bit products).</summary>
    internal static bool CanGather(ulong m) => Avx512F.IsSupported && Avx512DQ.IsSupported && Avx2.IsSupported && m <= uint.MaxValue;

    /// <summary>
    /// The probes eight at a time: positions from 32×32-bit partial products, words by two AVX2
    /// gathers, one compare per eight probes.
    /// </summary>
    internal bool MayContainGather(UID128 key)
    {
        Vector512<ulong> step  = Vector512.Create(key.Low * 8);
        Vector512<ulong> x     = Vector512.Create(key.High) + Vector512.Create(0UL, 1, 2, 3, 4, 5, 6, 7) * key.Low;
        Vector512<ulong> low6  = Vector512.Create(63UL);

        for (uint i = 0; i < _k; i += 8, x += step)
        {
            Vector512<ulong> bit   = MulHigh(x, _m);
            Vector512<long>  index = (bit >>> 6).AsInt64();
            Vector512<ulong> mask  = Avx512F.ShiftLeftLogicalVariable(Vector512<ulong>.One, bit & low6);
            Vector512<ulong> words = Vector512.Create(Avx2.GatherVector256(_words, index.GetLower(), 8), Avx2.GatherVector256(_words, index.GetUpper(), 8));

            ulong active = _k - i >= 8 ? 0xFFUL : (1UL << (int)(_k - i)) - 1;
            if ((Vector512.Equals(words & mask, mask).ExtractMostSignificantBits() & active) != active) return false;
        }

        return true;
    }

    /// <summary>The high 64 bits of <c>x·m</c> per lane, for <c>m &lt; 2^32</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<ulong> MulHigh(Vector512<ulong> x, ulong m)
    {
        Vector512<uint>  mv = Vector512.Create(m).AsUInt32();
        Vector512<ulong> lo = Avx512F.Multiply(x.AsUInt32(), mv);                   // xlo·m
        Vector512<ulong> hi = Avx512F.Multiply((x >>> 32).AsUInt32(), mv);          // xhi·m
        return (hi + (lo >>> 32)) >>> 32;
    }

    /// <summary>Tests <paramref name="key"/> and adds it, setting every bit whether or not it was present. Returns the test result.</summary>
    public bool TestAndAdd(UID128 key)
    {
        bool  present = true;
        ulong x = key.High, step = key.Low;

        for (uint i = 0; i < _k; i++, x += step)
        {
            ulong  bit  = Bit(x);
            ulong* word = _words + (bit >> 6);
            ulong  mask = 1UL << (int)bit;

            if ((*word & mask) == 0) present = false;
            *word |= mask;
        }

        return present;
    }

    /// <summary>Tests <paramref name="key"/> and adds it only where bits are missing. Returns the test result.</summary>
    public bool TestOrAdd(UID128 key)
    {
        bool  present = true;
        ulong x = key.High, step = key.Low;

        for (uint i = 0; i < _k; i++, x += step)
        {
            ulong  bit  = Bit(x);
            ulong* word = _words + (bit >> 6);
            ulong  mask = 1UL << (int)bit;

            if ((*word & mask) == 0)
            {
                present = false;
                *word  |= mask;
            }
        }

        return present;
    }

    /// <summary>Reports whether every raw probe hash in <paramref name="locations"/> (see <see cref="Locations"/>) is set.</summary>
    public bool MayContainLocations(ReadOnlySpan<ulong> locations)
    {
        foreach (ulong location in locations)
        {
            ulong bit = Bit(location);
            if ((_words[bit >> 6] & (1UL << (int)bit)) == 0) return false;
        }

        return true;
    }

    /// <summary>Adds every key in <paramref name="keys"/>.</summary>
    /// <remarks>
    /// No prefetching: the k probes of one key are already independent loads, and prefetching a
    /// window of keys ahead measured slower than this loop, on a 1M-key filter and on a 77 MB one.
    /// </remarks>
    public void Add(ReadOnlySpan<UID128> keys)
    {
        foreach (var key in keys) Add(key);
    }

    /// <summary>Tests every key in <paramref name="keys"/> into <paramref name="results"/> and returns how many may be present.</summary>
    public int MayContain(ReadOnlySpan<UID128> keys, Span<bool> results)
    {
        if (results.Length < keys.Length) throw new ArgumentException("The results span is shorter than the keys.", nameof(results));

        int found = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            bool hit   = MayContain(keys[i]);
            results[i] = hit;
            found     += hit ? 1 : 0;
        }

        return found;
    }

    /// <summary>ORs <paramref name="other"/> into this filter.</summary>
    /// <exception cref="ArgumentException">The filters differ in <see cref="Capacity"/> or <see cref="K"/>.</exception>
    public void Merge(Bloom other)
    {
        if (other._m != _m) throw new ArgumentException($"m's don't match: {_m} != {other._m}", nameof(other));
        if (other._k != _k) throw new ArgumentException($"k's don't match: {_k} != {other._k}", nameof(other));

        ulong* a = _words, b = other._words;
        ulong  i = 0;

        if (Vector512.IsHardwareAccelerated)
        {
            for (; i + 8 <= _wordCount; i += 8) Vector512.Store(Vector512.Load(a + i) | Vector512.Load(b + i), a + i);
        }

        for (; i < _wordCount; i++) a[i] |= b[i];
    }

    /// <summary>A copy of the filter.</summary>
    public Bloom Copy()
    {
        var copy = new Bloom(_m, _k);
        Words.CopyTo(copy.Words);
        return copy;
    }

    /// <summary>Removes every key.</summary>
    public void Clear() => Words.Clear();

    /// <summary>The number of set bits.</summary>
    public ulong PopCount() => BlockArray.PopCount(_words, _wordCount);

    /// <summary>
    /// Estimates how many distinct keys were added from the set-bit count:
    /// <c>-m/k · ln(1 - X/m)</c>, rounded (Swamidass &amp; Baldi).
    /// </summary>
    public ulong ApproximatedSize()
    {
        double x = PopCount();
        if (x >= _m) return ulong.MaxValue;

        return (ulong)Math.Floor(-(double)_m / _k * Math.Log(1 - x / _m) + 0.5);
    }

    /// <inheritdoc/>
    public bool Equals(Bloom? other) => other is not null && other._m == _m && other._k == _k && Words.SequenceEqual(other.Words);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Bloom other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_m, _k, PopCount());

    /// <summary>The number of bytes <see cref="Serialize()"/> produces.</summary>
    public long SerializedSize => HeaderSize + (long)_wordCount * sizeof(ulong) + ChecksumSize;

    /// <summary>
    /// Serializes the filter: <c>'B'</c>, version 1, k (uint32), m (uint64), the words (uint64
    /// each), then a CRC-32C over everything before it — all little-endian.
    /// </summary>
    public byte[] Serialize()
    {
        var buffer = GC.AllocateUninitializedArray<byte>(checked((int)SerializedSize));
        Serialize(buffer);
        return buffer;
    }

    /// <summary>Serializes into <paramref name="destination"/>, which must hold <see cref="SerializedSize"/> bytes; returns the bytes written.</summary>
    public int Serialize(Span<byte> destination)
    {
        int total = checked((int)SerializedSize);
        if (destination.Length < total) throw new ArgumentException($"The destination needs {total} bytes.", nameof(destination));

        destination[0] = FORMAT_MAGIC;
        destination[1] = FORMAT_VERSION;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[2..], _k);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[6..], _m);

        Span<byte> data = destination.Slice(HeaderSize, total - HeaderSize - ChecksumSize);
        if (BitConverter.IsLittleEndian) MemoryMarshal.AsBytes(Words).CopyTo(data);
        else                             BinaryPrimitives.ReverseEndianness(Words, MemoryMarshal.Cast<byte, ulong>(data));

        BinaryPrimitives.WriteUInt32LittleEndian(destination[(total - ChecksumSize)..], BloomFilter.Crc32C(destination[..(total - ChecksumSize)]));
        return total;
    }

    /// <summary>Deserializes a filter written by <see cref="Serialize()"/>.</summary>
    /// <exception cref="BloomFilterFormatException">The data is not a valid serialized <see cref="Bloom"/>.</exception>
    public static Bloom Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + ChecksumSize) throw Invalid(BloomFilterFormatError.InvalidData, $"data too short (got {data.Length} bytes)");
        if (data[0] != FORMAT_MAGIC)                 throw Invalid(BloomFilterFormatError.InvalidData, "not a serialized Bloom");
        if (data[1] != FORMAT_VERSION)               throw Invalid(BloomFilterFormatError.UnsupportedVersion, $"got version {data[1]}, expected {FORMAT_VERSION}");

        uint  k = BinaryPrimitives.ReadUInt32LittleEndian(data[2..]);
        ulong m = BinaryPrimitives.ReadUInt64LittleEndian(data[6..]);

        if (k == 0)                 throw Invalid(BloomFilterFormatError.InvalidK, "k cannot be zero");
        if (m == 0 || m > MaxBits)  throw Invalid(BloomFilterFormatError.InvalidData, $"m out of range ({m})");

        ulong expected = HeaderSize + (m + 63) / 64 * sizeof(ulong) + ChecksumSize;
        if ((ulong)data.Length != expected) throw Invalid(BloomFilterFormatError.InvalidData, $"data length mismatch (got {data.Length} bytes, expected {expected})");

        int  payload = data.Length - ChecksumSize;
        uint want    = BinaryPrimitives.ReadUInt32LittleEndian(data[payload..]);
        uint got     = BloomFilter.Crc32C(data[..payload]);
        if (got != want) throw Invalid(BloomFilterFormatError.ChecksumMismatch, $"computed 0x{got:x8}, stored 0x{want:x8}");

        var f                    = new Bloom(m, k);
        ReadOnlySpan<byte> words = data[HeaderSize..payload];

        if (BitConverter.IsLittleEndian) words.CopyTo(MemoryMarshal.AsBytes(f.Words));
        else                             BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<byte, ulong>(words), f.Words);

        if (f.TailHasBits()) throw Invalid(BloomFilterFormatError.InvalidData, "bits set beyond m");
        return f;
    }

    private static BloomFilterFormatException Invalid(BloomFilterFormatError error, string detail) => new(error, "gloom: invalid serialized Bloom: " + detail);

    private ulong TailMask => _m % 64 == 0 ? 0 : ~0UL << (int)(_m % 64);

    private void ClearTail() => _words[_wordCount - 1] &= ~TailMask;

    private bool TailHasBits() => (_words[_wordCount - 1] & TailMask) != 0;
}
