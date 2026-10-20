using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text.Unicode;
using UID;

namespace Gloom;

/// <summary>
/// The hash pipeline that maps a key to a block and the k bit positions inside it. Port of
/// hash.go.
/// </summary>
/// <remarks>
/// <para>
/// Every key is hashed once with xxh3's 128-bit variant (seed 0), carried as a
/// <see cref="UID128"/>: <see cref="UID128.High"/> is xxh3's high 64 bits and selects the block
/// (and, for sharded filters, the shard); <see cref="UID128.Low"/> is folded into the 32-bit
/// intra-block hash fed to the prime-modulo probes.
/// </para>
/// <para>
/// The mapping is the Go library's bit for bit, so filters serialized by either library load in
/// the other. A string key is hashed as its UTF-8 bytes (what Go's <c>AddString</c> hashes); a
/// <see cref="UID128"/> key is hashed as its 16 bytes in <see cref="UID128.GetBytes(Span{byte})"/>
/// order.
/// </para>
/// </remarks>
public static class BloomHash
{
    // Strings up to this many chars are transcoded to UTF-8 on the stack.
    private const int STACK_CHARS = 256;

    // Up to this many chars, an all-ASCII string is narrowed by hand.
    private const int SHORT_ASCII = 32;

    /// <summary>Returns the xxh3-128 hash of <paramref name="data"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UID128 Hash(ReadOnlySpan<byte> data)
    {
        UInt128 h = XxHash128.HashToUInt128(data);
        return new UID128((ulong)(h >> 64), (ulong)h);
    }

    /// <summary>Returns the xxh3-128 hash of the UTF-8 encoding of <paramref name="text"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UID128 Hash(ReadOnlySpan<char> text)
    {
        if (BitConverter.IsLittleEndian && text.Length is >= 4 and <= 16 && Xxh3.TryHashShortAscii(text, out UID128 hash)) return hash;

        if (text.Length <= STACK_CHARS)
        {
            Span<byte> utf8 = stackalloc byte[STACK_CHARS * 3];
            return Hash(utf8[..ToUtf8(text, utf8)]);
        }

        return HashLong(text);
    }

    /// <summary>
    /// Transcodes to UTF-8. Short ASCII keys (the common case) are narrowed in a plain loop, which
    /// for a dozen characters costs less than the general transcoder's setup.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ToUtf8(ReadOnlySpan<char> text, Span<byte> utf8)
    {
        if (text.Length <= SHORT_ASCII)
        {
            int i = 0;
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 0x80) break;
                utf8[i] = (byte)c;
            }

            if (i == text.Length) return i;
        }

        Utf8.FromUtf16(text, utf8, out _, out int written);
        return written;
    }

    private static UID128 HashLong(ReadOnlySpan<char> text)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(checked(text.Length * 3));
        try
        {
            Utf8.FromUtf16(text, rented, out _, out int written);
            return Hash(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Returns the xxh3-128 hash of the 16 bytes of <paramref name="key"/> (High then Low, each
    /// little-endian — the <see cref="UID128.GetBytes(Span{byte})"/> layout). This is xxh3's
    /// 9-to-16-byte path inlined for a fixed length, so it needs no buffer and no length dispatch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UID128 Hash(UID128 key) => Xxh3.Hash16(key.High, key.Low);

    /// <summary>
    /// Maps a uniform 64-bit value into <c>[0, n)</c> with the multiply-shift reduction
    /// <c>floor(h·n / 2^64)</c>: no divide and no modulo bias at any <paramref name="n"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReduceRange(ulong h, ulong n) => Math.BigMul(h, n, out _);

    /// <summary>Folds the low half of the hash into the 32-bit intra-block hash, mixing both lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint FoldIntra(ulong low) => (uint)low ^ (uint)(low >> 32);

    /// <summary>
    /// xxh3 constants and the fixed-length 16-byte path (<c>XXH3_len_9to16_128b</c>, seed 0).
    /// Pinned against <see cref="XxHash128"/> by the tests.
    /// </summary>
    internal static class Xxh3
    {
        private const ulong PRIME32_2 = 0x85EBCA77UL;
        private const ulong PRIME64_1 = 0x9E3779B185EBCA87UL;
        private const ulong PRIME64_2 = 0xC2B2AE3D27D4EB4FUL;

        // (secret[32..40] ^ secret[40..48]) and (secret[48..56] ^ secret[56..64]) of the default secret.
        private const ulong BITFLIP_LOW  = 0x78E5C0CC4EE679CBUL ^ 0x2172FFCC7DD05A82UL;
        private const ulong BITFLIP_HIGH = 0x8E2443F7744608B8UL ^ 0x4C263A81E69035E0UL;

        // (secret[16..24] ^ secret[24..32]) of the default secret, for the 4-to-8-byte path.
        private const ulong BITFLIP_4TO8 = 0xDB979083E96DD4DEUL ^ 0x1F67B3B7A4A44072UL;
        private const ulong PRIME_MX2    = 0x9FB21C651E98DF25UL;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static UID128 Hash16(ulong high, ulong low) => Hash9To16(high, low, 16);

        /// <summary>
        /// <c>XXH3_len_9to16_128b</c>: <paramref name="first"/> is bytes [0, 8) of the input,
        /// <paramref name="last"/> bytes [len-8, len).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UID128 Hash9To16(ulong first, ulong last, int length)
        {
            ulong m128High = Math.BigMul(first ^ last ^ BITFLIP_LOW, PRIME64_1, out ulong m128Low);
            m128Low       += (ulong)(length - 1) << 54;
            last          ^= BITFLIP_HIGH;
            m128High      += last + (uint)last * (PRIME32_2 - 1);
            m128Low       ^= BinaryPrimitives.ReverseEndianness(m128High);

            ulong h128High = Math.BigMul(m128Low, PRIME64_2, out ulong h128Low);
            h128High      += m128High * PRIME64_2;

            return new UID128(Avalanche(h128High), Avalanche(h128Low));
        }

        /// <summary><c>XXH3_len_4to8_128b</c>: bytes [0, 4) and [len-4, len) of the input.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UID128 Hash4To8(uint first, uint last, int length)
        {
            ulong keyed    = (first + ((ulong)last << 32)) ^ BITFLIP_4TO8;
            ulong m128High = Math.BigMul(keyed, PRIME64_1 + ((ulong)length << 2), out ulong m128Low);

            m128High += m128Low << 1;
            m128Low  ^= m128High >> 3;
            m128Low  ^= m128Low >> 35;
            m128Low  *= PRIME_MX2;
            m128Low  ^= m128Low >> 28;

            return new UID128(Avalanche(m128High), m128Low);
        }

        /// <summary>
        /// Hashes a 4-to-16-char string as UTF-8 without materialising the bytes, when it is all
        /// ASCII: the chars are narrowed in registers and fed to xxh3's short-input paths. Writing
        /// the bytes and reading them back as words would stall on store-to-load forwarding,
        /// which costs more than the hash itself.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryHashShortAscii(ReadOnlySpan<char> text, out UID128 hash)
        {
            ref ushort chars  = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));
            int        length = text.Length;

            if (length >= 8)
            {
                Vector128<ushort> head = Vector128.LoadUnsafe(ref chars);
                Vector128<ushort> tail = Vector128.LoadUnsafe(ref chars, (nuint)(length - 8));

                if (((head | tail) & Vector128.Create((ushort)0xFF80)) != Vector128<ushort>.Zero)
                {
                    hash = default;
                    return false;
                }

                ulong first = Vector128.Narrow(head, head).AsUInt64().ToScalar();
                ulong last  = Vector128.Narrow(tail, tail).AsUInt64().ToScalar();

                hash = length == 8 ? Hash4To8((uint)first, (uint)(first >> 32), 8) : Hash9To16(first, last, length);
                return true;
            }

            ulong a = Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref chars));
            ulong b = Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref chars, length - 4)));

            if (((a | b) & 0xFF80FF80FF80FF80UL) != 0)
            {
                hash = default;
                return false;
            }

            hash = Hash4To8(Pack4(a), Pack4(b), length);
            return true;
        }

        /// <summary>Four 16-bit ASCII chars (little-endian lanes) to their four bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Pack4(ulong w)
            => (uint)((w & 0xFF) | ((w >> 8) & 0xFF00) | ((w >> 16) & 0xFF0000) | ((w >> 24) & 0xFF000000));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Avalanche(ulong h)
        {
            h ^= h >> 37;
            h *= 0x165667919E3779F9UL;
            h ^= h >> 32;
            return h;
        }
    }
}
