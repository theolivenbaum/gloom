using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using UID;

namespace Gloom.Tests;

public class BloomKeyTests
{
    private static UID128 Xxh3(ReadOnlySpan<byte> bytes)
    {
        UInt128 h = XxHash128.HashToUInt128(bytes);
        return new UID128((ulong)(h >> 64), (ulong)h);
    }

    private static byte[] Le(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }

    private static byte[] Le(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }

    [Fact]
    public void FixedWidthOverloadsAreTheHashOfTheirBytes()
    {
        var rng = new Random(21);
        for (int i = 0; i < 20_000; i++)
        {
            ulong u = (ulong)rng.NextInt64() ^ ((ulong)rng.Next(2) << 63);
            if (i < 100) u = (ulong)i;

            Assert.Equal(Xxh3(Le(u)), BloomKey.From(u));
            Assert.Equal(Xxh3(Le(u)), BloomKey.From((long)u));
            Assert.Equal(Xxh3(Le(u)), BloomKey.From(new UID64(u)));
            Assert.Equal(Xxh3(Le((uint)u)), BloomKey.From((uint)u));
            Assert.Equal(Xxh3(Le((uint)u)), BloomKey.From((int)u));

            var wide = new UInt128(u * 31, u);
            Assert.Equal(Xxh3([.. Le((ulong)wide), .. Le((ulong)(wide >> 64))]), BloomKey.From(wide));
            Assert.Equal(BloomKey.From(wide), BloomKey.From((Int128)wide));

            var guid = new Guid(Le(u).Concat(Le(~u)).ToArray());
            Assert.Equal(Xxh3(guid.ToByteArray()), BloomKey.From(guid));

            var uid   = new UID128(u, ~u);
            var bytes = new byte[16];
            uid.GetBytes(bytes);
            Assert.Equal(Xxh3(bytes), BloomKey.From(uid));

            double d = BitConverter.UInt64BitsToDouble(u);
            Assert.Equal(Xxh3(Le(u)), BloomKey.From(d));
        }
    }

    [Fact]
    public void TextAndBytesAreXxh3()
    {
        foreach (string s in new[] { "", "a", "key-123", "a longer key than sixteen chars", "ümlaut 世界" })
        {
            Assert.Equal(Xxh3(Encoding.UTF8.GetBytes(s)), BloomKey.From(s));
            Assert.Equal(Xxh3(Encoding.UTF8.GetBytes(s)), BloomKey.From(s.AsSpan()));
            Assert.Equal(Xxh3(Encoding.UTF8.GetBytes(s)), BloomKey.From(Encoding.UTF8.GetBytes(s)));
        }
    }

    [Fact]
    public void SequentialIntegersMakeAGoodBloom()
    {
        // Sequential ids are the case BloomKey exists for: used raw as UID128s they would not be hash-like.
        const int n = 200_000;
        var f = Bloom.WithEstimates(n, 0.01);

        for (int i = 0; i < n; i++) f.Add(BloomKey.From(i));
        for (int i = 0; i < n; i++) Assert.True(f.MayContain(BloomKey.From(i)));

        int fp = 0;
        for (int i = n; i < n + 200_000; i++) fp += f.MayContain(BloomKey.From(i)) ? 1 : 0;

        Assert.True(fp / 200_000.0 < 0.0125, $"{fp}");
    }
}
