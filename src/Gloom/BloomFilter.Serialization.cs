using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Gloom;

/// <summary>Why a serialized filter was rejected.</summary>
public enum BloomFilterFormatError
{
    /// <summary>The data is malformed: too short, the wrong length, or an impossible header field.</summary>
    InvalidData,

    /// <summary>The format version is not one this library reads.</summary>
    UnsupportedVersion,

    /// <summary>The header names a number of hash functions with no partition.</summary>
    InvalidK,

    /// <summary>The CRC-32C trailer does not match: the data was corrupted.</summary>
    ChecksumMismatch,
}

/// <summary>Thrown by <see cref="BloomFilter.Deserialize"/> for data that is not a valid serialized filter.</summary>
public sealed class BloomFilterFormatException : FormatException
{
    /// <summary>Creates the exception.</summary>
    public BloomFilterFormatException(BloomFilterFormatError error, string message) : base(message) => Error = error;

    /// <summary>Why the data was rejected.</summary>
    public BloomFilterFormatError Error { get; }
}

public sealed partial class BloomFilter
{
    /// <summary>The serialization format version written by <see cref="Serialize()"/>.</summary>
    public const byte SerializeVersion = 1;

    /// <summary>Version (1) + K (4) + NumBlocks (8) + Count (8).</summary>
    public const int HeaderSize = 21;

    /// <summary>The trailing CRC-32C (Castagnoli).</summary>
    public const int ChecksumSize = 4;

    /// <summary>The number of bytes <see cref="Serialize()"/> produces for this filter.</summary>
    public long SerializedSize => HeaderSize + (long)_blocks.WordCount * sizeof(ulong) + ChecksumSize;

    /// <summary>
    /// Serializes the filter. The format, all little-endian, is the Go library's
    /// <c>MarshalBinary</c>: version (1 byte), k (uint32), numBlocks (uint64), count (uint64), the
    /// block words (uint64 each), then a CRC-32C over everything before it (uint32).
    /// </summary>
    public byte[] Serialize()
    {
        var buffer = GC.AllocateUninitializedArray<byte>(checked((int)SerializedSize));
        Serialize(buffer);
        return buffer;
    }

    /// <summary>Serializes the filter into <paramref name="destination"/>, which must hold <see cref="SerializedSize"/> bytes; returns the bytes written.</summary>
    public int Serialize(Span<byte> destination)
    {
        int total = checked((int)SerializedSize);
        if (destination.Length < total) throw new ArgumentException($"The destination needs {total} bytes.", nameof(destination));

        destination[0] = SerializeVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[1..], _k);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[5..], _numBlocks);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[13..], _count);

        Span<byte> data = destination.Slice(HeaderSize, total - HeaderSize - ChecksumSize);

        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(_blocks.AsSpan()).CopyTo(data);
        }
        else
        {
            BinaryPrimitives.ReverseEndianness(_blocks.AsSpan(), MemoryMarshal.Cast<byte, ulong>(data));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination[(total - ChecksumSize)..], Crc32C(destination[..(total - ChecksumSize)]));
        return total;
    }

    /// <summary>Deserializes a filter written by <see cref="Serialize()"/> or by the Go library's <c>MarshalBinary</c>.</summary>
    /// <exception cref="BloomFilterFormatException">The data is not a valid serialized filter; nothing is loaded.</exception>
    public static BloomFilter Deserialize(ReadOnlySpan<byte> data)
    {
        if (!TryDeserialize(data, out var filter, out var error, out string? message)) throw new BloomFilterFormatException(error, message!);
        return filter!;
    }

    /// <summary>Deserializes a filter, reporting why the data was rejected instead of throwing.</summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> data, out BloomFilter? filter, out BloomFilterFormatError error, out string? message)
    {
        filter = null;

        if (data.Length < HeaderSize + ChecksumSize)
        {
            return Fail(BloomFilterFormatError.InvalidData, $"gloom: invalid serialized data: data too short (got {data.Length} bytes, need at least {HeaderSize + ChecksumSize})", out error, out message);
        }

        if (data[0] != SerializeVersion)
        {
            return Fail(BloomFilterFormatError.UnsupportedVersion, $"gloom: unsupported serialization version: got version {data[0]}, expected {SerializeVersion}", out error, out message);
        }

        uint  k         = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
        ulong numBlocks = BinaryPrimitives.ReadUInt64LittleEndian(data[5..]);
        ulong count     = BinaryPrimitives.ReadUInt64LittleEndian(data[13..]);

        if (PrimePartition.For(k) is null)
        {
            return Fail(BloomFilterFormatError.InvalidK, $"gloom: invalid k value in serialized data: k={k} is not supported (valid range: {PrimePartition.MinK}-{PrimePartition.MaxK})", out error, out message);
        }

        if (numBlocks == 0)
        {
            return Fail(BloomFilterFormatError.InvalidData, "gloom: invalid serialized data: numBlocks cannot be zero", out error, out message);
        }

        if (numBlocks > BlockArray.MAX_NUM_BLOCKS)
        {
            return Fail(BloomFilterFormatError.InvalidData, $"gloom: invalid serialized data: numBlocks too large ({numBlocks})", out error, out message);
        }

        ulong expectedLength = HeaderSize + numBlocks * BloomParameters.BlockWords * sizeof(ulong) + ChecksumSize;

        if ((ulong)data.Length != expectedLength)
        {
            return Fail(BloomFilterFormatError.InvalidData, $"gloom: invalid serialized data: data length mismatch (got {data.Length} bytes, expected {expectedLength})", out error, out message);
        }

        int  payload = data.Length - ChecksumSize;
        uint want    = BinaryPrimitives.ReadUInt32LittleEndian(data[payload..]);
        uint got     = Crc32C(data[..payload]);

        if (got != want)
        {
            return Fail(BloomFilterFormatError.ChecksumMismatch, $"gloom: checksum mismatch (data corrupted): computed 0x{got:x8}, stored 0x{want:x8}", out error, out message);
        }

        var result               = new BloomFilter(numBlocks, k, count);
        ReadOnlySpan<byte> words = data[HeaderSize..payload];

        if (BitConverter.IsLittleEndian)
        {
            words.CopyTo(MemoryMarshal.AsBytes(result._blocks.AsSpan()));
        }
        else
        {
            BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<byte, ulong>(words), result._blocks.AsSpan());
        }

        filter  = result;
        error   = default;
        message = null;
        return true;
    }

    private static bool Fail(BloomFilterFormatError kind, string text, out BloomFilterFormatError error, out string? message)
    {
        error   = kind;
        message = text;
        return false;
    }

    /// <summary>
    /// CRC-32C (Castagnoli, reflected, init and final xor 0xFFFFFFFF), the checksum Go's
    /// <c>crc32.MakeTable(crc32.Castagnoli)</c> computes.
    /// </summary>
    internal static uint Crc32C(ReadOnlySpan<byte> data) => ~Crc32CRaw.Update(~0u, data);
}

/// <summary>
/// The raw CRC-32C state update (no init or final xor) on the SSE4.2 / ARMv8 CRC instruction
/// (<see cref="BitOperations.Crc32C(uint, ulong)"/>). The instruction has a latency of three
/// cycles and a throughput of one, so long inputs run three independent chains over adjacent
/// stripes and merge them: a state followed by <c>n</c> more bytes is the state times
/// <c>x^(8n)</c> mod P, XOR the CRC of those bytes from zero.
/// </summary>
internal static class Crc32CRaw
{
    private const int  STRIPE = 4096;
    private const uint POLY   = 0x82F63B78; // reflected Castagnoli polynomial

    // x^(8·STRIPE) mod P: what multiplying a state by advances it past one stripe.
    private static readonly uint s_shiftStripe = ShiftConstant(STRIPE);

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        while (data.Length >= 3 * STRIPE)
        {
            ReadOnlySpan<ulong> a = MemoryMarshal.Cast<byte, ulong>(data[..STRIPE]);
            ReadOnlySpan<ulong> b = MemoryMarshal.Cast<byte, ulong>(data.Slice(STRIPE, STRIPE));
            ReadOnlySpan<ulong> c = MemoryMarshal.Cast<byte, ulong>(data.Slice(2 * STRIPE, STRIPE));

            uint ca = crc, cb = 0, cc = 0;

            for (int i = 0; i < a.Length; i++)
            {
                ca = BitOperations.Crc32C(ca, Le(a[i]));
                cb = BitOperations.Crc32C(cb, Le(b[i]));
                cc = BitOperations.Crc32C(cc, Le(c[i]));
            }

            crc  = MultiplyModP(MultiplyModP(ca, s_shiftStripe) ^ cb, s_shiftStripe) ^ cc;
            data = data[(3 * STRIPE)..];
        }

        ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(data);
        for (int i = 0; i < words.Length; i++) crc = BitOperations.Crc32C(crc, Le(words[i]));

        for (int i = words.Length * sizeof(ulong); i < data.Length; i++) crc = BitOperations.Crc32C(crc, data[i]);

        return crc;
    }

    private static ulong Le(ulong w) => BitConverter.IsLittleEndian ? w : BinaryPrimitives.ReverseEndianness(w);

    /// <summary><c>a·b mod P</c> in the reflected representation (zlib's <c>multmodp</c>).</summary>
    internal static uint MultiplyModP(uint a, uint b)
    {
        uint m = 1u << 31, p = 0;

        while (true)
        {
            if ((a & m) != 0)
            {
                p ^= b;
                if ((a & (m - 1)) == 0) return p;
            }

            m >>= 1;
            b  = (b & 1) != 0 ? (b >> 1) ^ POLY : b >> 1;
        }
    }

    /// <summary><c>x^(8n) mod P</c>: the state 1 (0x80000000 reflected) advanced over <paramref name="n"/> zero bytes.</summary>
    private static uint ShiftConstant(int n)
    {
        uint crc = 1u << 31;
        for (int i = 0; i < n; i++) crc = BitOperations.Crc32C(crc, (byte)0);
        return crc;
    }
}
