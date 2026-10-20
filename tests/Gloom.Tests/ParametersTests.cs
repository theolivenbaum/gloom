using System.Text.Json;

namespace Gloom.Tests;

public class ParametersTests
{
    private sealed record OptimalVector(ulong Items, double Fp, ulong NumBlocks, uint K, ulong BitsPerItemBits, ulong AchievableBits, bool Met);

    private sealed record EstimateVector(ulong NumBlocks, uint K, ulong Items, ulong FpBits);

    private sealed record Vectors(string Description, OptimalVector[] Optimal, EstimateVector[] Estimate);

    private static readonly Vectors s_vectors = JsonSerializer.Deserialize<Vectors>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "params_vectors.json")),
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;

    private static void AssertClose(double expected, double actual, string what)
    {
        double tolerance = Math.Max(Math.Abs(expected), double.Epsilon) * 1e-12;
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}: Go {expected:R}, C# {actual:R}");
    }

    [Fact]
    public void OptimalMatchesGo()
    {
        Assert.NotEmpty(s_vectors.Optimal);

        foreach (var v in s_vectors.Optimal)
        {
            var (numBlocks, k, bitsPerItem) = BloomParameters.Optimal(v.Items, v.Fp);
            Assert.True(v.NumBlocks == numBlocks, $"Optimal({v.Items}, {v.Fp}): numBlocks Go {v.NumBlocks}, C# {numBlocks}");
            Assert.True(v.K == k, $"Optimal({v.Items}, {v.Fp}): k Go {v.K}, C# {k}");
            AssertClose(BitConverter.UInt64BitsToDouble(v.BitsPerItemBits), bitsPerItem, $"bitsPerItem({v.Items}, {v.Fp})");

            var (achievable, met) = BloomParameters.AchievableFalsePositiveRate(v.Items, v.Fp);
            AssertClose(BitConverter.UInt64BitsToDouble(v.AchievableBits), achievable, $"achievable({v.Items}, {v.Fp})");
            Assert.Equal(v.Met, met);
        }
    }

    [Fact]
    public void EstimateMatchesGo()
    {
        foreach (var v in s_vectors.Estimate)
        {
            AssertClose(BitConverter.UInt64BitsToDouble(v.FpBits), BloomParameters.EstimateFalsePositiveRate(v.NumBlocks, v.K, v.Items), $"Estimate({v.NumBlocks}, {v.K}, {v.Items})");
        }
    }

    [Fact]
    public void TypicalGoldenParameters() => Assert.Equal((212UL, 7u), (BloomParameters.Optimal(10_000, 0.01).NumBlocks, BloomParameters.Optimal(10_000, 0.01).K));

    [Theory]
    [InlineData(1000UL, 0.01)]
    [InlineData(10000UL, 0.001)]
    [InlineData(100000UL, 0.0001)]
    public void OptimalKIsSupported(ulong items, double fp)
    {
        var (_, k, _) = BloomParameters.Optimal(items, fp);
        Assert.InRange(k, PrimePartition.MinK, PrimePartition.MaxK);
    }

    [Fact]
    public void OptimalEdgeCases()
    {
        foreach (var (n, p) in new (ulong, double)[] { (0, 0.01), (1, 0.01), (1000, 0), (1000, -0.1), (1000, 1.0), (1000, 2.0), (1000, 0.5), (1000, 1e-7) })
        {
            var (numBlocks, k, _) = BloomParameters.Optimal(n, p);
            Assert.True(numBlocks > 0);
            Assert.InRange(k, PrimePartition.MinK, PrimePartition.MaxK);
        }
    }

    [Fact]
    public void AchievableReportsShortfall()
    {
        foreach (double fp in new[] { 0.1, 0.01, 0.001, 0.0001, 0.00001 })
        {
            var (achievable, met) = BloomParameters.AchievableFalsePositiveRate(1_000_000, fp);
            Assert.InRange(achievable, double.Epsilon, 1);
            Assert.True(met, $"fp={fp}: achievable {achievable}");
        }

        var (extreme, extremeMet) = BloomParameters.AchievableFalsePositiveRate(1_000_000, 1e-12);
        Assert.False(extremeMet);
        Assert.True(extreme > 1e-12);
    }

    [Fact]
    public void BlockedEstimateExceedsTheClassicFormula()
    {
        double estimated = BloomParameters.EstimateFalsePositiveRate(100, 7, 5000);
        double standard  = Math.Pow(1 - Math.Exp(-7.0 * 5000 / (100 * 512)), 7);

        Assert.True(estimated >= standard);
        Assert.True(estimated <= standard * 2);
    }

    [Fact]
    public void EstimateEdgeCases()
    {
        Assert.Equal(0, BloomParameters.EstimateFalsePositiveRate(100, 7, 0));
        Assert.Equal(0, BloomParameters.EstimateFalsePositiveRate(0, 7, 1000));
        Assert.InRange(BloomParameters.EstimateFalsePositiveRate(1, 7, 20000), double.Epsilon, 1);
        Assert.InRange(BloomParameters.EstimateFalsePositiveRate(100, 20, 5000), double.Epsilon, 1);
        Assert.InRange(BloomParameters.EstimateFalsePositiveRate(1, 20, 20000), double.Epsilon, 1);
    }

    [Fact]
    public void PartitionsArePairwiseCoprimeAndFillTheBlock()
    {
        static uint Gcd(uint a, uint b) => b == 0 ? a : Gcd(b, a % b);

        for (uint k = PrimePartition.MinK; k <= PrimePartition.MaxK; k++)
        {
            var p = PrimePartition.For(k)!;
            Assert.Equal(k, p.K);
            Assert.Equal(512u, (uint)p.Sizes.ToArray().Sum(x => x));

            for (int i = 0; i < p.Sizes.Length; i++)
            {
                for (int j = i + 1; j < p.Sizes.Length; j++) Assert.Equal(1u, Gcd(p.Sizes[i], p.Sizes[j]));
            }

            Assert.Equal(PrimePartition.ComputeOffsets(p.Sizes), p.Offsets.ToArray());
            Assert.Equal(0u, p.Offsets[0]);
        }

        Assert.Null(PrimePartition.For(0));
        Assert.Null(PrimePartition.For(2));
        Assert.Null(PrimePartition.For(18));
        Assert.Null(PrimePartition.For(uint.MaxValue));
    }

    [Fact]
    public void NextPowerOf2()
    {
        Assert.Equal(1UL, ShardedAtomicBloomFilter.NextPowerOf2(0));
        Assert.Equal(1UL, ShardedAtomicBloomFilter.NextPowerOf2(1));
        Assert.Equal(2UL, ShardedAtomicBloomFilter.NextPowerOf2(2));
        Assert.Equal(4UL, ShardedAtomicBloomFilter.NextPowerOf2(3));
        Assert.Equal(16UL, ShardedAtomicBloomFilter.NextPowerOf2(9));
        Assert.Equal(1UL << 63, ShardedAtomicBloomFilter.NextPowerOf2(1UL << 63));
        Assert.Equal(1UL << 63, ShardedAtomicBloomFilter.NextPowerOf2((1UL << 63) + 1));
        Assert.Equal(1UL << 63, ShardedAtomicBloomFilter.NextPowerOf2(ulong.MaxValue));
    }
}
