using System.Text;
using UID;

namespace Gloom.Tests;

public class FilterTests
{
    public static TheoryData<string> Kinds => new() { "plain", "atomic", "sharded" };

    private static IBloomFilter Create(string kind, ulong items, double fp) => kind switch
    {
        "plain"   => new BloomFilter(items, fp),
        "atomic"  => new AtomicBloomFilter(items, fp),
        "sharded" => new ShardedAtomicBloomFilter(items, fp, 8),
        _         => throw new ArgumentException(kind),
    };

    private static byte[] Key(string prefix, int i) => Encoding.ASCII.GetBytes($"{prefix}-{i}");

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Basic(string kind)
    {
        var f = Create(kind, 1000, 0.01);

        f.Add("hello"u8);
        f.Add("world".AsSpan());
        f.Add(new UID128(1, 2));
        f.AddHash(new UID128(3, 4));

        Assert.True(f.MayContain("hello"u8));
        Assert.True(f.MayContain("hello".AsSpan()));
        Assert.True(f.MayContain("world"u8));
        Assert.True(f.MayContain(new UID128(1, 2)));
        Assert.True(f.MayContainHash(new UID128(3, 4)));
        Assert.True(f.MayContainHash(BloomHash.Hash(new UID128(1, 2))));
        Assert.Equal(4UL, f.Count);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void FalsePositiveRateMeetsTarget(string kind)
    {
        const int    items  = 10_000;
        const double target = 0.01;

        var f = Create(kind, items, target);
        for (int i = 0; i < items; i++) f.Add(Key("item", i));

        int falsePositives = 0;
        for (int i = 0; i < 10_000; i++)
        {
            if (f.MayContain(Key("notitem", i))) falsePositives++;
        }

        Assert.True(falsePositives / 10_000.0 <= target * 2, $"FP rate {falsePositives / 10_000.0}");
    }

    [Fact]
    public void FalsePositiveRateAtScale()
    {
        foreach (double target in new[] { 0.01, 0.001 })
        {
            const int items = 1_000_000;
            var       f     = new BloomFilter(items, target);

            for (int i = 0; i < items; i++) f.Add(new UID128(0, (ulong)i));

            int fps = 0;
            for (int i = 0; i < 1_000_000; i++)
            {
                if (f.MayContain(new UID128(1, (ulong)i))) fps++;
            }

            double rate = fps / 1_000_000.0;
            Assert.True(rate <= target * 1.15, $"target {target}: realized {rate}");
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void NoFalseNegativesForAnyKeyShape(string kind)
    {
        var f    = Create(kind, 5000, 0.01);
        var rng  = new Random(3);
        var keys = new List<byte[]>(GoldenTests.EdgeKeys);

        for (int i = 0; i < 5000; i++)
        {
            var key = new byte[rng.Next(0, 300)];
            rng.NextBytes(key);
            keys.Add(key);
        }

        foreach (var key in keys) f.Add(key);
        foreach (var key in keys) Assert.True(f.MayContain(key));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ByteAndStringOverloadsAgree(string kind)
    {
        var f = Create(kind, 1000, 0.01);
        for (int i = 0; i < 1000; i++)
        {
            if (i % 2 == 0) f.Add($"key-{i}-ü".AsSpan());
            else            f.Add(Encoding.UTF8.GetBytes($"key-{i}-ü"));
        }

        for (int i = 0; i < 1000; i++)
        {
            Assert.True(f.MayContain(Encoding.UTF8.GetBytes($"key-{i}-ü")));
            Assert.True(f.MayContain($"key-{i}-ü".AsSpan()));
        }
    }

    [Fact]
    public void StringOverloadsMatchSpanOverloads()
    {
        var a = new BloomFilter(100, 0.01);
        var b = new BloomFilter(100, 0.01);

        a.Add("hello");
        b.Add("hello".AsSpan());

        Assert.Equal(a.Serialize(), b.Serialize());
        Assert.True(a.MayContain("hello"));
    }

    [Fact]
    public void AllFilterTypesSetIdenticalBits()
    {
        var plain  = BloomFilter.WithParameters(97, 9);
        var atomic = AtomicBloomFilter.WithParameters(97, 9);

        for (int i = 0; i < 3000; i++)
        {
            plain.Add(Key("k", i));
            atomic.Add(Key("k", i));
        }

        Assert.Equal(plain.Blocks.AsSpan().ToArray(), atomic.Blocks.AsSpan().ToArray());
        Assert.Equal(plain.Count, atomic.Count);
    }

    [Fact]
    public void ShardedRoutesLikeOneAtomicFilterPerShard()
    {
        var sharded = new ShardedAtomicBloomFilter(10_000, 0.01, 4);

        for (int i = 0; i < 10_000; i++) sharded.Add(Key("s", i));

        for (int i = 0; i < 10_000; i++)
        {
            var hash  = BloomHash.Hash(Key("s", i));
            var shard = sharded.Shards[(int)(hash.High & 3)];
            Assert.True(shard.MayContainHash(new UID128(hash.High, hash.Low)));
        }

        Assert.Equal(10_000UL, sharded.Count);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void AddIsIdempotentOnBits(string kind)
    {
        var f = Create(kind, 1000, 0.01);
        f.Add("x"u8);
        double fill = f.EstimatedFillRatio();

        for (int i = 0; i < 10; i++) f.Add("x"u8);

        Assert.Equal(fill, f.EstimatedFillRatio());
        Assert.Equal(11UL, f.Count);
    }

    [Fact]
    public void BatchApisMatchSingleKeyApis()
    {
        var keys = Enumerable.Range(0, 1000).Select(i => new UID128((ulong)i * 31, (ulong)i)).ToArray();

        var single  = new BloomFilter(2000, 0.01);
        var batched = new BloomFilter(2000, 0.01);
        var atomic  = new AtomicBloomFilter(2000, 0.01);

        foreach (var k in keys) single.Add(k);
        batched.Add(keys);
        atomic.Add(keys);

        Assert.Equal(single.Serialize(), batched.Serialize());
        Assert.Equal(single.Blocks.AsSpan().ToArray(), atomic.Blocks.AsSpan().ToArray());
        Assert.Equal(1000UL, atomic.Count);

        var probes   = Enumerable.Range(0, 3000).Select(i => new UID128((ulong)i * 31, (ulong)i)).ToArray();
        var results  = new bool[probes.Length];
        var results2 = new bool[probes.Length];

        int found  = batched.MayContain(probes, results);
        int found2 = atomic.MayContain(probes, results2);

        for (int i = 0; i < probes.Length; i++) Assert.Equal(single.MayContain(probes[i]), results[i]);
        Assert.Equal(results, results2);
        Assert.Equal(results.Count(r => r), found);
        Assert.Equal(found, found2);

        var hashes = keys.Select(BloomHash.Hash).ToArray();
        var viaHashes = new BloomFilter(2000, 0.01);
        viaHashes.AddHashes(hashes);
        Assert.Equal(single.Serialize(), viaHashes.Serialize());
        Assert.Equal(keys.Length, viaHashes.MayContainHashes(hashes, new bool[hashes.Length]));

        Assert.Throws<ArgumentException>(() => batched.MayContain(probes, new bool[1]));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Statistics(string kind)
    {
        var f = Create(kind, 10_000, 0.01);

        Assert.Equal(f.NumBlocks * 512, f.Capacity);
        Assert.InRange(f.K, PrimePartition.MinK, PrimePartition.MaxK);
        Assert.Equal(0, f.EstimatedFillRatio());
        Assert.Equal(0, f.EstimatedFalsePositiveRate());

        for (int i = 0; i < 10_000; i++) f.Add(Key("i", i));

        Assert.Equal(10_000UL, f.Count);
        Assert.InRange(f.EstimatedFillRatio(), 0.3, 0.7);
        Assert.InRange(f.EstimatedFalsePositiveRate(), 0.001, 0.02);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void SampledFillRatioTracksTheExactRatio(string kind)
    {
        const int items = 2_000_000;
        var       f     = Create(kind, items, 0.01);

        for (int i = 0; i < items; i++) f.Add(new UID128(7, (ulong)i));

        double exact = f.EstimatedFillRatio();
        Assert.InRange(exact, double.Epsilon, 1);

        foreach (int n in new[] { 256, 1024, 4096 })
        {
            double got = f.SampledFillRatio(n);
            Assert.True(Math.Abs(got - exact) / exact <= 0.05, $"sample {n}: {got} vs {exact}");
        }

        Assert.Equal(exact, f.SampledFillRatio(0), 12);
        Assert.Equal(exact, f.SampledFillRatio(-5), 12);
        if (kind != "sharded") Assert.Equal(exact, f.SampledFillRatio((int)f.NumBlocks + 1));
    }

    [Fact]
    public void PopCountMatchesScalarCount()
    {
        var f   = BloomFilter.WithParameters(37, 7);
        var rng = new Random(5);
        for (int i = 0; i < 3000; i++) f.Add(new UID128((ulong)rng.NextInt64(), (ulong)rng.NextInt64()));

        ulong expected = 0;
        foreach (ulong w in f.Blocks.AsSpan()) expected += (ulong)System.Numerics.BitOperations.PopCount(w);

        Assert.Equal(expected, f.Blocks.PopCount());

        ulong perBlock = 0;
        for (ulong b = 0; b < f.NumBlocks; b++) perBlock += f.Blocks.PopCountBlock(b);
        Assert.Equal(expected, perBlock);
    }

    [Fact]
    public void EveryKWorks()
    {
        for (uint k = PrimePartition.MinK; k <= PrimePartition.MaxK; k++)
        {
            var f = BloomFilter.WithParameters(100, k);
            Assert.Equal(k, f.K);

            for (int i = 0; i < 1000; i++) f.Add(Key("k", i));
            for (int i = 0; i < 1000; i++) Assert.True(f.MayContain(Key("k", i)));

            // Each key sets exactly k bits in its block when the filter starts empty.
            var single = BloomFilter.WithParameters(1, k);
            single.Add("one"u8);
            Assert.Equal(k, (uint)single.Blocks.PopCount());
        }
    }

    [Fact]
    public void InvalidKDefaultsToSeven()
    {
        foreach (uint k in new uint[] { 0, 1, 2, 18, 100 })
        {
            Assert.Equal(7u, BloomFilter.WithParameters(10, k).K);
            Assert.Equal(7u, AtomicBloomFilter.WithParameters(10, k).K);
        }
    }

    [Fact]
    public void ZeroBlocksBecomesOne()
    {
        Assert.Equal(1UL, BloomFilter.WithParameters(0, 7).NumBlocks);
        Assert.Equal(1UL, AtomicBloomFilter.WithParameters(0, 7).NumBlocks);
    }

    [Fact]
    public void TooManyBlocksThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BloomFilter.WithParameters(BloomFilter.MaxNumBlocks + 1, 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => AtomicBloomFilter.WithParameters(ulong.MaxValue, 7));
    }

    [Fact]
    public unsafe void BlocksAreCacheLineAligned()
    {
        for (ulong n = 1; n < 20; n++)
        {
            Assert.Equal(0UL, (ulong)BloomFilter.WithParameters(n, 7).Blocks.Words % 64);
            Assert.Equal(0UL, (ulong)AtomicBloomFilter.WithParameters(n, 7).Blocks.Words % 64);
        }
    }

    [Fact]
    public void ShardCountIsRoundedToPowerOfTwo()
    {
        Assert.Equal(8UL, new ShardedAtomicBloomFilter(1000, 0.01, 5).NumShards);
        Assert.Equal(1UL, new ShardedAtomicBloomFilter(1000, 0.01, 0).NumShards);
        Assert.True(new ShardedAtomicBloomFilter(1000, 0.01).NumShards >= 4);

        var sharded = new ShardedAtomicBloomFilter(1000, 0.01, 4);
        Assert.Equal(sharded.Shards[0].NumBlocks * 4, sharded.NumBlocks);
        Assert.Equal(sharded.NumBlocks * 512, sharded.Capacity);
    }
}
