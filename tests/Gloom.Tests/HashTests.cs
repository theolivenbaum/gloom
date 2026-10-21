using System.IO.Hashing;
using System.Text;
using UID;

namespace Gloom.Tests;

public class HashTests
{
    [Fact]
    public void Uid128HashIsTheHashOfItsBytes()
    {
        var rng = new Random(42);
        Span<byte> bytes = stackalloc byte[16];

        for (int i = 0; i < 100_000; i++)
        {
            var key = new UID128((ulong)rng.NextInt64() ^ ((ulong)rng.Next() << 63), (ulong)rng.NextInt64());
            key.GetBytes(bytes);

            UInt128 expected = XxHash128.HashToUInt128(bytes);
            var     got      = BloomHash.Hash(key);

            Assert.Equal((ulong)(expected >> 64), got.High);
            Assert.Equal((ulong)expected, got.Low);
        }

        Assert.Equal(BloomHash.Hash(new byte[16]), BloomHash.Hash(default(UID128)));
        Assert.Equal(BloomHash.Hash(Enumerable.Repeat((byte)0xff, 16).ToArray()), BloomHash.Hash(new UID128(ulong.MaxValue, ulong.MaxValue)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("hello, 世界")]
    [InlineData("key-123456")]
    [InlineData("emoji 🎉 and ümlauts")]
    public void StringHashIsTheHashOfItsUtf8(string text)
    {
        Assert.Equal(BloomHash.Hash(Encoding.UTF8.GetBytes(text)), BloomHash.Hash(text.AsSpan()));
    }

    [Fact]
    public void LongStringHashIsTheHashOfItsUtf8()
    {
        foreach (int n in new[] { 255, 256, 257, 1000, 100_000 })
        {
            string text = string.Concat(Enumerable.Range(0, n).Select(i => (char)('a' + i % 26))) + "世界";
            Assert.Equal(BloomHash.Hash(Encoding.UTF8.GetBytes(text)), BloomHash.Hash(text.AsSpan()));
        }
    }

    [Fact]
    public void ReduceRangeStaysInRange()
    {
        var rng = new Random(1);
        foreach (ulong n in new ulong[] { 1, 2, 3, 7, 1000, 1UL << 32, (1UL << 50) + 3, ulong.MaxValue })
        {
            Assert.Equal(0UL, BloomHash.ReduceRange(0, n));
            Assert.Equal(n - 1, BloomHash.ReduceRange(ulong.MaxValue, n));

            for (int i = 0; i < 1000; i++) Assert.True(BloomHash.ReduceRange((ulong)rng.NextInt64() * 2, n) < n);
        }

        Assert.Equal(0UL, BloomHash.ReduceRange(12345, 0));
    }

    [Fact]
    public void ReduceRangeIsUniform()
    {
        const int buckets = 97;
        const int samples = 970_000;

        var counts = new int[buckets];
        for (int i = 0; i < samples; i++) counts[BloomHash.ReduceRange(BloomHash.Hash(BitConverter.GetBytes(i)).High, buckets)]++;

        double expected = samples / (double)buckets;
        double chi2     = counts.Sum(c => (c - expected) * (c - expected) / expected);

        // 96 degrees of freedom: p = 0.001 at ~140.
        Assert.True(chi2 < 140, $"chi² = {chi2}");
    }

    [Fact]
    public void FastModuloMatchesRemainder()
    {
        var rng = new Random(7);
        for (uint k = PrimePartition.MinK; k <= PrimePartition.MaxK; k++)
        {
            var partition = PrimePartition.For(k)!;

            for (int p = 0; p < partition.Probes.Length; p++)
            {
                var probe = partition.Probes[p];

                foreach (uint h in new[] { 0u, 1u, probe.Size - 1, probe.Size, uint.MaxValue, uint.MaxValue - 1 })
                {
                    Assert.Equal(probe.Offset + h % probe.Size, probe.BitPosition(h));
                }

                for (int i = 0; i < 100_000; i++)
                {
                    uint h = (uint)rng.NextInt64(0, 1L << 32);
                    Assert.Equal(probe.Offset + h % probe.Size, probe.BitPosition(h));
                }
            }
        }
    }
}

public class ShortStringHashTests
{
    [Fact]
    public void ShortStringPathsMatchTheUtf8Hash()
    {
        var rng = new Random(13);
        for (int length = 0; length <= 24; length++)
        {
            for (int trial = 0; trial < 2000; trial++)
            {
                var chars = new char[length];
                for (int i = 0; i < length; i++)
                {
                    chars[i] = (trial % 4) switch
                    {
                        0 => (char)rng.Next(0, 0x80),
                        1 => (char)rng.Next(0x20, 0x7F),
                        2 => (char)rng.Next(0, 0x800),     // two-byte UTF-8 mixed in
                        _ => i == rng.Next(length) ? 'é' : (char)rng.Next(0x20, 0x7F),
                    };
                }

                var text = new string(chars);
                Assert.Equal(BloomHash.Hash(Encoding.UTF8.GetBytes(text)), BloomHash.Hash(text.AsSpan()));
            }
        }
    }

    [Fact]
    public void ShortPathRejectsNonAscii()
    {
        Assert.False(BloomHash.Xxh3.TryHashShortAscii("abcé", out _));
        Assert.False(BloomHash.Xxh3.TryHashShortAscii("abcdefgh\u0080", out _));
        Assert.True(BloomHash.Xxh3.TryHashShortAscii("abcd\u007f", out _));
    }
}
