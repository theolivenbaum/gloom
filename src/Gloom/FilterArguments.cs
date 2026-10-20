namespace Gloom;

/// <summary>Argument handling and helpers shared by the filter types.</summary>
internal static class FilterArguments
{
    /// <summary>Zero blocks becomes one (a valid minimal filter); too many throws, as Go's constructors panic.</summary>
    public static ulong NormalizeNumBlocks(ulong numBlocks)
    {
        if (numBlocks == 0) return 1;

        if (numBlocks > BlockArray.MAX_NUM_BLOCKS)
        {
            throw new ArgumentOutOfRangeException(nameof(numBlocks), numBlocks, $"gloom: numBlocks {numBlocks} exceeds maximum {BlockArray.MAX_NUM_BLOCKS}.");
        }

        return numBlocks;
    }

    /// <summary>Returns the partition for <paramref name="k"/>, replacing an unsupported value with 7.</summary>
    public static PrimePartition PartitionOrDefault(ref uint k)
    {
        var partition = PrimePartition.For(k);
        if (partition is not null) return partition;

        k = 7;
        return PrimePartition.For(k)!;
    }

    /// <summary>
    /// The fill ratio over <paramref name="sampleBlocks"/> evenly spaced blocks,
    /// <c>floor(i·numBlocks / n)</c> (strictly increasing, so distinct, and deterministic); a full
    /// scan when the sample is not smaller than the filter.
    /// </summary>
    public static double SampledFillRatio(BlockArray blocks, int sampleBlocks)
    {
        ulong numBlocks = blocks.NumBlocks;

        if (sampleBlocks <= 0 || (ulong)sampleBlocks >= numBlocks)
        {
            return blocks.PopCount() / (double)(numBlocks * BloomParameters.BlockBits);
        }

        ulong n       = (ulong)sampleBlocks;
        ulong setBits = 0;

        for (ulong i = 0; i < n; i++)
        {
            ulong block = i * numBlocks / n; // < 2^59: the block limit keeps the product from overflowing
            setBits    += blocks.PopCountBlock(block);
        }

        return setBits / (double)(n * BloomParameters.BlockBits);
    }
}
