namespace Gloom;

/// <summary>
/// Sizing and false-positive estimation for blocked one-hashing bloom filters. Port of params.go.
/// </summary>
public static class BloomParameters
{
    /// <summary>The number of bits per block (one cache line).</summary>
    public const int BlockBits = 512;

    /// <summary>The number of 64-bit words per block.</summary>
    public const int BlockWords = BlockBits / 64;

    private const double LN2_SQUARED = 0.4804530139182014;

    /// <summary>
    /// Bounds how far <see cref="Optimal"/> grows the block count beyond the classic estimate
    /// while compensating for the cache-line blocking penalty. Attainable targets never reach it.
    /// </summary>
    private const double BLOCKING_GROWTH_CAP = 4.0;

    /// <summary>
    /// Calculates the block count and number of hash functions for <paramref name="expectedItems"/>
    /// at <paramref name="falsePositiveRate"/>, together with the classic (non-blocked) bits per item
    /// used as the starting estimate.
    /// </summary>
    /// <remarks>
    /// The block count grows (about 12.5% a step, at most 4x) until the blocked, partitioned
    /// estimate actually meets the target, compensating for the Poisson load variance across
    /// blocks; <c>k</c> is the FP-minimising one for each candidate, not <c>(m/n)·ln 2</c>. A target
    /// too low for a 512-bit block at any size is capped: check
    /// <see cref="AchievableFalsePositiveRate"/> when a guarantee matters.
    /// </remarks>
    public static (ulong NumBlocks, uint K, double BitsPerItem) Optimal(ulong expectedItems, double falsePositiveRate)
    {
        if (expectedItems == 0) expectedItems = 1;
        if (falsePositiveRate <= 0) falsePositiveRate = 0.0001;
        if (falsePositiveRate >= 1) falsePositiveRate = 0.99;

        double bitsPerItem = -Math.Log(falsePositiveRate) / LN2_SQUARED;
        double totalBits   = expectedItems * bitsPerItem;
        ulong  baseBlocks  = (ulong)Math.Ceiling(totalBits / BlockBits);
        ulong  maxBlocks   = (ulong)Math.Ceiling(baseBlocks * BLOCKING_GROWTH_CAP);

        ulong numBlocks = baseBlocks;
        uint  k         = BestK(numBlocks, expectedItems);

        while (EstimateFalsePositiveRate(numBlocks, k, expectedItems) > falsePositiveRate && numBlocks < maxBlocks)
        {
            numBlocks = Math.Min(numBlocks + numBlocks / 8 + 1, maxBlocks);
            k         = BestK(numBlocks, expectedItems);
        }

        return (numBlocks, k, bitsPerItem);
    }

    /// <summary>Returns the supported <c>k</c> that minimises the estimated false-positive rate.</summary>
    internal static uint BestK(ulong numBlocks, ulong expectedItems)
    {
        uint   best   = PrimePartition.MinK;
        double bestFP = double.PositiveInfinity;

        for (uint k = PrimePartition.MinK; k <= PrimePartition.MaxK; k++)
        {
            double fp = EstimateFalsePositiveRate(numBlocks, k, expectedItems);
            if (fp < bestFP)
            {
                bestFP = fp;
                best   = k;
            }
        }

        return best;
    }

    /// <summary>
    /// Returns the lowest false-positive rate a filter sized by <see cref="Optimal"/> delivers for
    /// <paramref name="expectedItems"/>, and whether that meets <paramref name="falsePositiveRate"/>.
    /// Targets below roughly 1e-11 cannot be met by a 512-bit block at any size.
    /// </summary>
    public static (double Achievable, bool Met) AchievableFalsePositiveRate(ulong expectedItems, double falsePositiveRate)
    {
        var (numBlocks, k, _) = Optimal(expectedItems, falsePositiveRate);
        double achievable     = EstimateFalsePositiveRate(numBlocks, k, expectedItems);

        // Tiny floating-point overshoot still counts as met; the requested rate is the contract.
        return (achievable, achievable <= falsePositiveRate * (1 + 1e-9));
    }

    /// <summary>
    /// The false-positive rate of one block holding <paramref name="j"/> items under the partitioned
    /// scheme: <c>∏ᵢ (1 - (1 - 1/pᵢ)^j)</c>.
    /// </summary>
    private static double PartitionedBlockFP(ReadOnlySpan<uint> sizes, double j)
    {
        double fp = 1.0;
        foreach (uint p in sizes) fp *= 1 - Math.Pow(1 - 1 / (double)p, j);
        return fp;
    }

    /// <summary>
    /// Estimates the false-positive rate of a filter with <paramref name="numBlocks"/> blocks and
    /// <paramref name="k"/> hash functions after <paramref name="itemsAdded"/> insertions:
    /// <c>E[∏ᵢ (1 - (1 - 1/pᵢ)^J)]</c> with <c>J ~ Poisson(n/B)</c>, the per-block load.
    /// </summary>
    public static double EstimateFalsePositiveRate(ulong numBlocks, uint k, ulong itemsAdded)
    {
        if (numBlocks == 0 || itemsAdded == 0) return 0;

        var    partition = PrimePartition.For(k);
        double lambda    = itemsAdded / (double)numBlocks;
        double kf        = k;
        double s         = BlockBits;

        // At very large loads the Poisson spread is negligible relative to the mean.
        if (lambda > 10000)
        {
            if (partition is not null) return PartitionedBlockFP(partition.Sizes, lambda);

            double m = numBlocks * s;
            return Math.Pow(1 - Math.Exp(-kf * itemsAdded / m), kf);
        }

        int    maxJ         = (int)(lambda + 10 * Math.Sqrt(lambda) + 20);
        double fp           = 0;
        double logFactorial = 0;
        double logLambda    = Math.Log(lambda);

        for (int j = 0; j <= maxJ; j++)
        {
            if (j > 0) logFactorial += Math.Log(j);

            double prob = Math.Exp(-lambda + j * logLambda - logFactorial);

            if (prob < 1e-15 && j > (int)lambda) break;

            if (j > 0)
            {
                double blockFP = partition is not null
                    ? PartitionedBlockFP(partition.Sizes, j)
                    : Math.Pow(1 - Math.Exp(-kf * j / s), kf);

                fp += prob * blockFP;
            }
        }

        return fp;
    }
}
