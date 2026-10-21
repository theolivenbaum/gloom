using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using UID;

namespace Gloom;

/// <summary>
/// Builds the hash-like <see cref="UID128"/> keys <see cref="Bloom"/> expects from other values:
/// each is the xxh3-128 hash (seed 0) of the value's little-endian bytes — of its UTF-8 bytes for
/// text. Fixed-width values go through xxh3's short-input paths inlined for their length, so no
/// buffer is involved (measured: 1.5 ns for an <see cref="int"/>, 3.6–3.7 ns for a
/// <see cref="Guid"/> or <see cref="UID128"/>, 6.4 ns for a short ASCII string).
/// </summary>
/// <remarks>
/// A <see cref="UID128"/> that is already uniformly distributed (<see cref="UID128.New"/>, a hash)
/// can go to <see cref="Bloom"/> as is; <see cref="From(UID128)"/> is for one that is not — a UID
/// with fixed or sequential bits. Every overload equals
/// <see cref="From(ReadOnlySpan{byte})"/> of the value's bytes, so a key built from an
/// <see cref="int"/> and one built from its four bytes are the same key.
/// </remarks>
public static class BloomKey
{
    /// <summary>The key of <paramref name="data"/>.</summary>
    public static UID128 From(ReadOnlySpan<byte> data) => BloomHash.Hash(data);

    /// <summary>The key of the UTF-8 encoding of <paramref name="text"/>.</summary>
    public static UID128 From(ReadOnlySpan<char> text) => BloomHash.Hash(text);

    /// <summary>The key of the UTF-8 encoding of <paramref name="text"/>.</summary>
    public static UID128 From(string text) => BloomHash.Hash(text.AsSpan());

    /// <summary>The key of the 16 bytes of <paramref name="value"/> (High then Low, little-endian), for a UID that is not hash-like itself.</summary>
    public static UID128 From(UID128 value) => BloomHash.Hash(value);

    /// <summary>The key of the 8 bytes of <paramref name="value"/>.</summary>
    public static UID128 From(UID64 value) => From(value.Value);

    /// <summary>The key of the 16 bytes of <paramref name="value"/> (<see cref="Guid.TryWriteBytes(Span{byte})"/> order).</summary>
    public static UID128 From(Guid value)
    {
        // On little-endian hardware a Guid's memory is its TryWriteBytes order: read it in place
        // (writing the bytes out and reading them back as words stalls on store forwarding).
        if (BitConverter.IsLittleEndian)
        {
            ref byte raw = ref Unsafe.As<Guid, byte>(ref value);
            return BloomHash.Xxh3.Hash9To16(Unsafe.ReadUnaligned<ulong>(ref raw), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref raw, 8)), 16);
        }

        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        return BloomHash.Xxh3.Hash9To16(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]), 16);
    }

    /// <summary>The key of the 8 little-endian bytes of <paramref name="value"/>.</summary>
    public static UID128 From(ulong value) => BloomHash.Xxh3.Hash4To8((uint)value, (uint)(value >> 32), 8);

    /// <summary>The key of the 8 little-endian bytes of <paramref name="value"/>.</summary>
    public static UID128 From(long value) => From((ulong)value);

    /// <summary>The key of the 4 little-endian bytes of <paramref name="value"/>.</summary>
    public static UID128 From(uint value) => BloomHash.Xxh3.Hash4To8(value, value, 4);

    /// <summary>The key of the 4 little-endian bytes of <paramref name="value"/>.</summary>
    public static UID128 From(int value) => From((uint)value);

    /// <summary>The key of the 16 little-endian bytes of <paramref name="value"/> (low half first).</summary>
    public static UID128 From(UInt128 value) => BloomHash.Xxh3.Hash9To16((ulong)value, (ulong)(value >> 64), 16);

    /// <summary>The key of the 16 little-endian bytes of <paramref name="value"/> (low half first).</summary>
    public static UID128 From(Int128 value) => From((UInt128)value);

    /// <summary>The key of the 8 little-endian bytes of <paramref name="value"/>'s IEEE-754 representation (-0.0 and 0.0 differ).</summary>
    public static UID128 From(double value) => From(BitConverter.DoubleToUInt64Bits(value));
}
