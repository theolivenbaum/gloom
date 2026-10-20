using System.Buffers.Binary;
using System.Text;

namespace Gloom.Tests;

public class SerializationTests
{
    private static BloomFilter Filled(ulong items, double fp, int keys)
    {
        var f = new BloomFilter(items, fp);
        for (int i = 0; i < keys; i++) f.Add(Encoding.ASCII.GetBytes($"key-{i}"));
        return f;
    }

    private static BloomFilterFormatError Rejects(byte[] data)
    {
        Assert.False(BloomFilter.TryDeserialize(data, out var filter, out var error, out string? message));
        Assert.Null(filter);
        Assert.NotNull(message);

        var ex = Assert.Throws<BloomFilterFormatException>(() => BloomFilter.Deserialize(data));
        Assert.Equal(error, ex.Error);
        return error;
    }

    private static void FixChecksum(byte[] data)
    {
        int payload = data.Length - BloomFilter.ChecksumSize;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(payload), BloomFilter.Crc32C(data.AsSpan(0, payload)));
    }

    [Fact]
    public void RoundTripsEmptyAndFilled()
    {
        foreach (int keys in new[] { 0, 1, 1000, 10_000 })
        {
            var f    = Filled(10_000, 0.01, keys);
            var copy = BloomFilter.Deserialize(f.Serialize());

            Assert.Equal(f.NumBlocks, copy.NumBlocks);
            Assert.Equal(f.K, copy.K);
            Assert.Equal(f.Count, copy.Count);
            Assert.Equal(f.Serialize(), copy.Serialize());

            for (int i = 0; i < keys; i++) Assert.True(copy.MayContain(Encoding.ASCII.GetBytes($"key-{i}")));
        }
    }

    [Fact]
    public void RoundTripsEveryKAndSize()
    {
        for (uint k = PrimePartition.MinK; k <= PrimePartition.MaxK; k++)
        {
            foreach (ulong blocks in new ulong[] { 1, 2, 7, 64, 1000 })
            {
                var f = BloomFilter.WithParameters(blocks, k);
                for (int i = 0; i < 500; i++) f.Add(Encoding.ASCII.GetBytes($"k{k}-{i}"));

                var bytes = f.Serialize();
                Assert.Equal(f.SerializedSize, bytes.Length);
                Assert.Equal(bytes, BloomFilter.Deserialize(bytes).Serialize());
            }
        }
    }

    [Fact]
    public void LayoutIsTheDocumentedOne()
    {
        var f = BloomFilter.WithParameters(2, 7);
        f.Add("a"u8);
        f.Add("b"u8);

        byte[] data = f.Serialize();

        Assert.Equal(21 + 2 * 64 + 4, data.Length);
        Assert.Equal(1, data[0]);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1)));
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(5)));
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(13)));

        var words = f.Blocks.AsSpan();
        for (int i = 0; i < words.Length; i++) Assert.Equal(words[i], BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(21 + i * 8)));

        Assert.Equal(BloomFilter.Crc32C(data.AsSpan(0, data.Length - 4)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4)));
    }

    [Fact]
    public void Crc32CMatchesTheStandardCheckValue()
    {
        Assert.Equal(0xE3069283u, BloomFilter.Crc32C("123456789"u8));
        Assert.Equal(0u, BloomFilter.Crc32C([]));
    }

    [Fact]
    public void SerializeIntoSpan()
    {
        var f      = Filled(1000, 0.01, 100);
        var buffer = new byte[f.SerializedSize + 10];

        Assert.Equal(f.SerializedSize, f.Serialize(buffer));
        Assert.Equal(f.Serialize(), buffer.AsSpan(0, (int)f.SerializedSize).ToArray());
        Assert.Throws<ArgumentException>(() => f.Serialize(new byte[10]));
    }

    [Fact]
    public void CanAddAfterDeserializing()
    {
        var copy = BloomFilter.Deserialize(Filled(1000, 0.01, 100).Serialize());
        copy.Add("new"u8);

        Assert.True(copy.MayContain("new"u8));
        Assert.Equal(101UL, copy.Count);
    }

    [Fact]
    public void RejectsShortData()
    {
        Assert.Equal(BloomFilterFormatError.InvalidData, Rejects([]));
        Assert.Equal(BloomFilterFormatError.InvalidData, Rejects(new byte[24]));
    }

    [Fact]
    public void RejectsUnsupportedVersion()
    {
        var data = Filled(100, 0.01, 10).Serialize();
        data[0]  = 2;
        Assert.Equal(BloomFilterFormatError.UnsupportedVersion, Rejects(data));
    }

    [Fact]
    public void RejectsInvalidK()
    {
        foreach (uint k in new uint[] { 0, 2, 18, uint.MaxValue })
        {
            var data = Filled(100, 0.01, 10).Serialize();
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1), k);
            FixChecksum(data);
            Assert.Equal(BloomFilterFormatError.InvalidK, Rejects(data));
        }
    }

    [Fact]
    public void RejectsBadBlockCounts()
    {
        foreach (ulong blocks in new ulong[] { 0, 3, 1UL << 50, ulong.MaxValue })
        {
            var data = Filled(100, 0.01, 10).Serialize();
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(5), blocks);
            FixChecksum(data);
            Assert.Equal(BloomFilterFormatError.InvalidData, Rejects(data));
        }
    }

    [Fact]
    public void RejectsLengthMismatch()
    {
        var data = Filled(100, 0.01, 10).Serialize();
        Assert.Equal(BloomFilterFormatError.InvalidData, Rejects(data[..^1]));
        Assert.Equal(BloomFilterFormatError.InvalidData, Rejects([.. data, 0]));

        // A legacy blob without the checksum trailer is a length mismatch.
        Assert.Equal(BloomFilterFormatError.InvalidData, Rejects(data[..^4]));
    }

    [Fact]
    public void ChecksumDetectsEverySingleBitFlip()
    {
        var data = BloomFilter.WithParameters(1, 7).Serialize();

        for (int bit = 0; bit < data.Length * 8; bit++)
        {
            var corrupted = (byte[])data.Clone();
            corrupted[bit / 8] ^= (byte)(1 << (bit % 8));

            // A flip in the header may fail an earlier check; a flip anywhere must fail some check.
            Assert.False(BloomFilter.TryDeserialize(corrupted, out _, out _, out _), $"bit {bit} flip was accepted");
        }

        var blockFlip = (byte[])data.Clone();
        blockFlip[30] ^= 0x10;
        Assert.Equal(BloomFilterFormatError.ChecksumMismatch, Rejects(blockFlip));
    }

    [Fact]
    public void FuzzedInputNeverCrashes()
    {
        var rng = new Random(11);
        for (int i = 0; i < 20_000; i++)
        {
            var data = new byte[rng.Next(0, 200)];
            rng.NextBytes(data);
            if (data.Length > 0 && rng.Next(2) == 0) data[0] = 1;

            BloomFilter.TryDeserialize(data, out _, out _, out _);
        }
    }
}

public class Crc32CTests
{
    private static uint Reference(ReadOnlySpan<byte> data)
    {
        uint crc = ~0u;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78 : crc >> 1;
        }
        return ~crc;
    }

    [Fact]
    public void InterleavedStreamsMatchTheBitwiseDefinition()
    {
        var rng = new Random(9);
        foreach (int n in new[] { 0, 1, 7, 8, 9, 4095, 12287, 12288, 12289, 24576 + 13, 100_000 })
        {
            var data = new byte[n];
            rng.NextBytes(data);
            Assert.Equal(Reference(data), BloomFilter.Crc32C(data));
            Assert.Equal(Reference(data.AsSpan(n / 3)), BloomFilter.Crc32C(data.AsSpan(n / 3)));
        }
    }
}
