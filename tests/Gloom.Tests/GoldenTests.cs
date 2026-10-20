using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gloom.Tests;

/// <summary>
/// The Go library's golden files (.reference/testdata/golden), checked against this port: every
/// filter the Go code serialized loads here, answers identically, and rebuilding its recipe here
/// produces the same bytes. Port of golden_test.go.
/// </summary>
public class GoldenTests
{
    internal static readonly string GoldenDir = Path.Combine(AppContext.BaseDirectory, "golden");

    private sealed record Manifest(
        string Description,
        byte   FormatVersion,
        string Sha256,
        ulong  NumBlocks,
        uint   K,
        ulong  Count,
        int    NumKeys,
        string Method,
        bool   EdgeKeys,
        int    NumProbes,
        string ProbeResults);

    private static readonly JsonSerializerOptions s_json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // FROZEN in the Go library: the golden files depend on this exact list and order.
    internal static readonly byte[][] EdgeKeys =
    [
        [],
        [0x00],
        [0xff],
        "a"u8.ToArray(),
        "ab"u8.ToArray(),
        "abc"u8.ToArray(),
        "abcd"u8.ToArray(),
        Repeat(0x00, 8),
        Repeat(0xff, 9),
        "0123456789abcdef"u8.ToArray(),
        "0123456789abcdefg"u8.ToArray(),
        Repeat((byte)'x', 128),
        Repeat((byte)'y', 129),
        Repeat((byte)'z', 240),
        Repeat((byte)'w', 241),
        Encoding.UTF8.GetBytes("hello, 世界"),
    ];

    private static byte[] Repeat(byte b, int n) => Enumerable.Repeat(b, n).ToArray();

    /// <summary>"&lt;prefix&gt;-&lt;i&gt;:" followed by (i*7)%1031 bytes cycling through every value (goldenKey).</summary>
    internal static byte[] GoldenKey(string prefix, int i)
    {
        var key = new List<byte>(Encoding.ASCII.GetBytes($"{prefix}-{i}:"));
        for (int j = 0; j < i * 7 % 1031; j++) key.Add((byte)j);
        return key.ToArray();
    }

    private static List<byte[]> Members(bool edgeKeys, int numKeys)
    {
        var keys = new List<byte[]>();
        if (edgeKeys) keys.AddRange(EdgeKeys);
        for (int i = 0; i < numKeys; i++) keys.Add(GoldenKey("member", i));
        return keys;
    }

    private static IEnumerable<(string Name, Manifest Manifest, byte[] Bin)> LoadGoldens()
    {
        foreach (string json in Directory.GetFiles(GoldenDir, "*.json", SearchOption.AllDirectories).Where(p => Path.GetFileName(Path.GetDirectoryName(p))!.StartsWith('v')).Order())
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(json), s_json)!;
            yield return (Path.GetFileNameWithoutExtension(json), manifest, File.ReadAllBytes(Path.ChangeExtension(json, ".bin")));
        }
    }

    /// <summary>
    /// Builds a recipe. Go's "string" insertions are <c>AddString(string(key))</c>, which hashes the
    /// key's raw bytes — not always valid UTF-8, so here they go through the byte overload.
    /// </summary>
    private static void Fill(IBloomFilter filter, Manifest m)
    {
        foreach (var key in Members(m.EdgeKeys, m.NumKeys)) filter.Add(key);
    }

    private static string ProbeBitmap(BloomFilter f, int n)
    {
        var bitmap = new byte[(n + 7) / 8];
        for (int i = 0; i < n; i++)
        {
            if (f.MayContain(GoldenKey("probe", i))) bitmap[i / 8] |= (byte)(1 << (i % 8));
        }
        return Convert.ToHexStringLower(bitmap);
    }

    [Fact]
    public void GoldensArePresent() => Assert.Equal(19, LoadGoldens().Count());

    [Fact]
    public void Decode()
    {
        foreach (var (name, m, bin) in LoadGoldens())
        {
            Assert.True(Convert.ToHexStringLower(SHA256.HashData(bin)) == m.Sha256, $"{name}: golden file was modified");
            Assert.Equal(m.FormatVersion, bin[0]);

            var f = BloomFilter.Deserialize(bin);
            Assert.Equal(m.NumBlocks, f.NumBlocks);
            Assert.Equal(m.K, f.K);
            Assert.Equal(m.Count, f.Count);

            foreach (var key in Members(m.EdgeKeys, m.NumKeys)) Assert.True(f.MayContain(key), $"{name}: false negative");

            Assert.True(ProbeBitmap(f, m.NumProbes) == m.ProbeResults, $"{name}: non-member results differ from the Go library's");
        }
    }

    [Fact]
    public void Reproduce()
    {
        foreach (var (name, m, bin) in LoadGoldens().Where(g => g.Manifest.FormatVersion == BloomFilter.SerializeVersion))
        {
            var f = BloomFilter.WithParameters(m.NumBlocks, m.K);
            Fill(f, m);

            byte[] got = f.Serialize();
            Assert.True(got.AsSpan().SequenceEqual(bin), $"{name}: serialized output differs from the Go library's");

            var af = AtomicBloomFilter.WithParameters(m.NumBlocks, m.K);
            Fill(af, m);
            Assert.Equal(m.Count, af.Count);

            var words = af.Blocks.AsSpan();
            for (int i = 0; i < words.Length; i++)
            {
                Assert.Equal(BinaryPrimitives.ReadUInt64LittleEndian(bin.AsSpan(BloomFilter.HeaderSize + i * 8)), words[i]);
            }
        }
    }

    private sealed record HashVector(int Len, string Hi, string Lo, uint Intra, ulong[] Blocks);

    private sealed record HashVectors(string Description, ulong[] NumBlocks, HashVector[] Vectors);

    private static byte[] HashInput(int n)
    {
        var b = new byte[n];
        for (int j = 0; j < n; j++) b[j] = (byte)(j * 131 + n + 7);
        return b;
    }

    [Fact]
    public void HashVectorsMatch()
    {
        var file = JsonSerializer.Deserialize<HashVectors>(File.ReadAllText(Path.Combine(GoldenDir, "hash_vectors.json")), s_json)!;
        Assert.Equal([1UL, 3, 1000, (1UL << 20) + 7, 1UL << 50], file.NumBlocks);
        Assert.NotEmpty(file.Vectors);

        foreach (var v in file.Vectors)
        {
            var h = BloomHash.Hash(HashInput(v.Len));
            Assert.Equal(v.Hi, h.High.ToString("x16"));
            Assert.Equal(v.Lo, h.Low.ToString("x16"));
            Assert.Equal(v.Intra, BloomHash.FoldIntra(h.Low));

            for (int i = 0; i < file.NumBlocks.Length; i++) Assert.Equal(v.Blocks[i], BloomHash.ReduceRange(h.High, file.NumBlocks[i]));
        }
    }

    [Fact]
    public void PrimePartitionsAreFrozen()
    {
        uint[][] frozen =
        [
            [167, 173, 172],
            [109, 127, 137, 139],
            [97, 101, 103, 109, 102],
            [61, 79, 83, 89, 97, 103],
            [61, 67, 71, 79, 83, 89, 62],
            [37, 47, 53, 61, 67, 71, 79, 97],
            [41, 43, 47, 53, 59, 67, 71, 73, 58],
            [31, 37, 41, 43, 47, 53, 59, 61, 67, 73],
            [29, 31, 37, 41, 43, 44, 47, 53, 59, 61, 67],
            [17, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 71],
            [17, 19, 23, 29, 31, 37, 41, 43, 47, 52, 53, 59, 61],
            [11, 13, 17, 19, 23, 29, 31, 37, 41, 47, 53, 59, 61, 71],
            [11, 13, 17, 19, 23, 28, 29, 31, 37, 41, 43, 47, 53, 59, 61],
            [5, 7, 11, 13, 17, 19, 23, 27, 29, 31, 37, 41, 43, 47, 53, 109],
            [3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 32, 37, 41, 43, 47, 53, 101],
        ];

        for (uint k = PrimePartition.MinK; k <= PrimePartition.MaxK; k++)
        {
            Assert.Equal(frozen[k - PrimePartition.MinK], PrimePartition.For(k)!.Sizes.ToArray());
        }
    }

    [Fact]
    public void FormatConstantsAreFrozen()
    {
        Assert.Equal(1, BloomFilter.SerializeVersion);
        Assert.Equal(21, BloomFilter.HeaderSize);
        Assert.Equal(4, BloomFilter.ChecksumSize);
        Assert.Equal(512, BloomParameters.BlockBits);
        Assert.Equal(8, BloomParameters.BlockWords);
    }
}
