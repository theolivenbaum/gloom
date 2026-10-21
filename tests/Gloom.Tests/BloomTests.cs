using UID;

namespace Gloom.Tests;

/// <summary>Port of bits-and-blooms/bloom's bloom_test.go (.reference/bloom), on UID128 keys.</summary>
public class BloomTests
{
    private static UID128 Key(ulong i) => Bloom.SampleKey(i);

    [Fact]
    public void Basic()
    {
        var f  = new Bloom(1000, 4);
        var n1 = Key(1);
        var n2 = Key(2);
        var n3 = Key(3);

        f.Add(n1);
        bool n3a = f.TestAndAdd(n3);
        bool n1b = f.MayContain(n1);
        bool n2b = f.MayContain(n2);
        bool n3b = f.MayContain(n3);

        Assert.True(n1b);
        Assert.False(n2b);
        Assert.False(n3a);
        Assert.True(n3b);
    }

    [Fact]
    public void LowNumbersBecomeOne()
    {
        var f = new Bloom(0, 0);
        Assert.Equal(1u, f.K);
        Assert.Equal(1UL, f.Capacity);
        f.Add(UID128.New());
        Assert.Single(f.Words.ToArray());
    }

    [Theory]
    [InlineData(1000UL, 0.0001)]
    [InlineData(10000UL, 0.0001)]
    [InlineData(100000UL, 0.0001)]
    [InlineData(1000UL, 0.001)]
    [InlineData(10000UL, 0.001)]
    [InlineData(100000UL, 0.001)]
    [InlineData(1000UL, 0.01)]
    [InlineData(10000UL, 0.01)]
    [InlineData(100000UL, 0.01)]
    public void EstimatedRateIsMet(ulong n, double maxFp)
    {
        var (m, k) = Bloom.EstimateParameters(n, maxFp);
        double fp  = Bloom.EstimateFalsePositiveRate(m, k, n);
        Assert.True(fp <= 1.5 * maxFp, $"n {n}, m {m}, k {k}: {fp} vs {maxFp}");
    }

    [Fact]
    public void RealizedRateTracksTheory()
    {
        foreach (var (n, p) in new (ulong, double)[] { (1_000_000, 0.01), (200_000, 0.001) })
        {
            var    (m, k)    = Bloom.EstimateParameters(n, p);
            double measured  = Bloom.EstimateFalsePositiveRate(m, k, n, 1_000_000);
            double theory    = Bloom.TheoreticalFalsePositiveRate(m, k, n);

            Assert.True(Math.Abs(measured - theory) / theory < 0.15, $"n {n}: measured {measured}, theory {theory}");
        }
    }

    [Fact]
    public void EstimateParametersMatchesTheFormula()
    {
        Assert.Equal((9586UL, 7u), Bloom.EstimateParameters(1000, 0.01));
        Assert.Equal((1917012UL, 14u), Bloom.EstimateParameters(100000, 0.0001));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bloom.EstimateParameters(10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bloom.EstimateParameters(10, 1));
    }

    [Fact]
    public void ProbesAreUniform()
    {
        // Port of TestLocation's chi-squared test: m = 8, k = 3, 100k keys.
        const ulong m = 8;
        const uint  k = 3;
        const int   rounds = 100_000;

        var f      = new Bloom(m, k);
        var counts = new double[m];

        for (int x = 0; x < rounds; x++)
        {
            foreach (ulong location in Bloom.Locations(Key((ulong)x), k)) counts[Math.BigMul(location, m, out _)]++;
        }

        double e   = (double)k * rounds / m;
        double chi = counts.Sum(c => (c - e) * (c - e) / e);

        Assert.True(chi < 20.278, $"chi² {chi}"); // 7 degrees of freedom at p = 0.005
        Assert.Equal(m, f.Capacity);
    }

    [Fact]
    public void ProbesOfOneKeyRarelyCollide()
    {
        var f = new Bloom(1 << 20, 10);
        for (ulong i = 0; i < 1000; i++)
        {
            f.Clear();
            f.Add(Key(i));
            Assert.InRange(f.PopCount(), 9UL, 10UL);
        }
    }

    [Fact]
    public void LocationsMatchAdd()
    {
        var f = Bloom.WithEstimates(1000, 0.001);
        var love = Key(10);

        f.Add(love);
        Assert.True(f.MayContainLocations(Bloom.Locations(love, f.K)));
        Assert.False(f.MayContainLocations(Bloom.Locations(Key(11), f.K)));
        Assert.Equal(f.K, (uint)Bloom.Locations(love, f.K).Length);
    }

    [Fact]
    public void TestOrAddOnlySetsMissingBits()
    {
        var f = new Bloom(1000, 4);
        Assert.False(f.TestOrAdd(Key(1)));
        Assert.True(f.TestOrAdd(Key(1)));
        Assert.True(f.MayContain(Key(1)));
        Assert.False(f.TestAndAdd(Key(2)));
        Assert.True(f.TestAndAdd(Key(2)));
    }

    [Fact]
    public void ApproximatedSize()
    {
        var f = Bloom.WithEstimates(1000, 0.001);
        for (ulong i = 0; i < 4; i++) f.Add(Key(i));
        Assert.Equal(4UL, f.ApproximatedSize());

        var big = Bloom.WithEstimates(100_000, 0.01);
        for (ulong i = 0; i < 50_000; i++) big.Add(Key(i));
        Assert.InRange(big.ApproximatedSize(), 49_000UL, 51_000UL);
    }

    [Fact]
    public void FalsePositiveProbability()
    {
        var f = Bloom.WithEstimates(1000, 0.001);
        for (ulong i = 0; i < 1000; i++) f.Add(Key(i));

        int count = 0;
        for (ulong i = 0; i < 1000; i++) count += f.MayContain(Key(i + 1000)) ? 1 : 0;

        Assert.True(count / 1000.0 <= 0.003, $"{count} false positives");
    }

    [Fact]
    public void NoFalseNegatives()
    {
        var f    = Bloom.WithEstimates(100_000, 0.01);
        var keys = Enumerable.Range(0, 100_000).Select(_ => UID128.New()).ToArray();

        foreach (var key in keys) f.Add(key);
        foreach (var key in keys) Assert.True(f.MayContain(key));
    }

    [Fact]
    public void BatchApisMatchSingleKeyApis()
    {
        var keys   = Enumerable.Range(0, 5000).Select(i => Key((ulong)i)).ToArray();
        var single = Bloom.WithEstimates(5000, 0.01);
        var batch  = Bloom.WithEstimates(5000, 0.01);

        foreach (var key in keys) single.Add(key);
        batch.Add(keys);
        Assert.Equal(single, batch);

        var probes  = Enumerable.Range(0, 20_000).Select(i => Key((ulong)i)).ToArray();
        var results = new bool[probes.Length];
        int found   = batch.MayContain(probes, results);

        for (int i = 0; i < probes.Length; i++) Assert.Equal(single.MayContain(probes[i]), results[i]);
        Assert.Equal(results.Count(r => r), found);
        Assert.Throws<ArgumentException>(() => batch.MayContain(probes, new bool[1]));
    }

    [Fact]
    public void GatherPathMatchesScalarPath()
    {
        Assert.Equal(System.Runtime.Intrinsics.X86.Avx512F.IsSupported, Bloom.CanGather(1000));
        Assert.False(Bloom.CanGather(1UL << 32));

        foreach (uint k in new uint[] { 1, 3, 7, 8, 9, 16, 17, 30 })
        {
            foreach (ulong m in new ulong[] { 1, 63, 64, 1000, 1_000_003, (1UL << 32) - 1 })
            {
                if (m > 100_000_000 && k != 9) continue;

                var f = new Bloom(m, k);
                for (ulong i = 0; i < Math.Min(m / 10, 10_000); i++) f.Add(Key(i));

                for (ulong i = 0; i < 20_000; i++) Assert.Equal(f.MayContainScalar(Key(i)), f.MayContain(Key(i)));
            }
        }
    }

    [Fact]
    public void MergeCopyAndEqual()
    {
        var a = new Bloom(1000, 4);
        var b = new Bloom(1000, 4);
        a.Add(Key(1));
        b.Add(Key(2));

        var copy = a.Copy();
        Assert.Equal(a, copy);
        Assert.NotSame(a, copy);

        a.Merge(b);
        Assert.True(a.MayContain(Key(1)));
        Assert.True(a.MayContain(Key(2)));
        Assert.False(copy.MayContain(Key(2)));
        Assert.NotEqual(a, copy);

        Assert.Throws<ArgumentException>(() => a.Merge(new Bloom(1001, 4)));
        Assert.Throws<ArgumentException>(() => a.Merge(new Bloom(1000, 5)));
        Assert.False(a.Equals(new Bloom(1000, 5)));
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void MergeOfLargeFiltersIsAUnion()
    {
        var a = new Bloom(100_037, 5);
        var b = new Bloom(100_037, 5);
        for (ulong i = 0; i < 5000; i++) { a.Add(Key(i)); b.Add(Key(i + 5000)); }

        a.Merge(b);
        for (ulong i = 0; i < 10_000; i++) Assert.True(a.MayContain(Key(i)));
    }

    [Fact]
    public void ClearRemovesEverything()
    {
        var f = new Bloom(1000, 4);
        f.Add(Key(1));
        f.Clear();
        Assert.False(f.MayContain(Key(1)));
        Assert.Equal(0UL, f.PopCount());
    }

    [Fact]
    public void From()
    {
        var f = new Bloom(1000, 4);
        for (ulong i = 0; i < 100; i++) f.Add(Key(i));

        var g = Bloom.From(f.Words, 1000, 4);
        Assert.Equal(f, g);
        for (ulong i = 0; i < 100; i++) Assert.True(g.MayContain(Key(i)));

        var h = Bloom.From(new ulong[] { ulong.MaxValue, ulong.MaxValue }, 3);
        Assert.Equal(128UL, h.Capacity);
        Assert.True(h.MayContain(Key(42)));

        var tail = Bloom.From(new ulong[] { ulong.MaxValue, ulong.MaxValue }, 70, 3);
        Assert.Equal(70UL, tail.PopCount());

        Assert.Throws<ArgumentException>(() => Bloom.From(new ulong[1], 65, 3));
    }

    [Fact]
    public void PopCountCoversEveryWord()
    {
        foreach (ulong m in new ulong[] { 1, 64, 65, 512, 513, 1000, 64 * 17 + 5 })
        {
            var f = Bloom.From(Enumerable.Repeat(ulong.MaxValue, (int)((m + 63) / 64)).ToArray(), m, 1);
            Assert.Equal(m, f.PopCount());
        }
    }

    [Fact]
    public void SerializationRoundTrips()
    {
        foreach (ulong m in new ulong[] { 1, 64, 1000, 1_000_003 })
        {
            var f = new Bloom(m, 6);
            for (ulong i = 0; i < 1000; i++) f.Add(Key(i));

            byte[] bytes = f.Serialize();
            Assert.Equal(f.SerializedSize, bytes.Length);

            var g = Bloom.Deserialize(bytes);
            Assert.Equal(f, g);
            Assert.Equal(f.Capacity, g.Capacity);
            Assert.Equal(f.K, g.K);
        }
    }

    [Fact]
    public void DeserializationRejectsBadData()
    {
        var bytes = new Bloom(1000, 4).Serialize();

        BloomFilterFormatError Error(byte[] data) => Assert.Throws<BloomFilterFormatException>(() => Bloom.Deserialize(data)).Error;

        Assert.Equal(BloomFilterFormatError.InvalidData, Error([]));
        Assert.Equal(BloomFilterFormatError.InvalidData, Error(bytes[..^1]));

        var magic = (byte[])bytes.Clone(); magic[0] = 1;
        Assert.Equal(BloomFilterFormatError.InvalidData, Error(magic));

        var version = (byte[])bytes.Clone(); version[1] = 9;
        Assert.Equal(BloomFilterFormatError.UnsupportedVersion, Error(version));

        var flipped = (byte[])bytes.Clone(); flipped[20] ^= 1;
        Assert.Equal(BloomFilterFormatError.ChecksumMismatch, Error(flipped));

        // A gloom BloomFilter is not a Bloom.
        Assert.Throws<BloomFilterFormatException>(() => Bloom.Deserialize(new BloomFilter(100, 0.01).Serialize()));
    }

    [Fact]
    public void TooManyBitsThrows() => Assert.Throws<ArgumentOutOfRangeException>(() => new Bloom(Bloom.MaxBits + 1, 3));
}
