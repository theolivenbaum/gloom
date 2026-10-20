using System.Text;
using UID;

namespace Gloom.Tests;

public class ConcurrencyTests
{
    private const int THREADS    = 8;
    private const int PER_THREAD = 50_000;

    private static void RunConcurrently(Action<int> body)
    {
        using var start   = new Barrier(THREADS);
        var       threads = new Thread[THREADS];

        for (int t = 0; t < THREADS; t++)
        {
            int id     = t;
            threads[t] = new Thread(() => { start.SignalAndWait(); body(id); });
            threads[t].Start();
        }

        foreach (var thread in threads) thread.Join();
    }

    public static TheoryData<string> Kinds => new() { "atomic", "sharded" };

    private static IBloomFilter Create(string kind) => kind == "atomic"
        ? new AtomicBloomFilter(THREADS * PER_THREAD, 0.01)
        : new ShardedAtomicBloomFilter(THREADS * PER_THREAD, 0.01, 16);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ConcurrentAddsHaveNoFalseNegativesAndAnExactCount(string kind)
    {
        var f = Create(kind);

        RunConcurrently(t =>
        {
            for (int i = 0; i < PER_THREAD; i++)
            {
                switch (i % 3)
                {
                    case 0: f.Add(Encoding.ASCII.GetBytes($"t{t}-{i}")); break;
                    case 1: f.Add($"t{t}-{i}".AsSpan()); break;
                    default: f.Add(new UID128((ulong)t, (ulong)i)); break;
                }
            }
        });

        Assert.Equal((ulong)(THREADS * PER_THREAD), f.Count);

        for (int t = 0; t < THREADS; t++)
        {
            for (int i = 0; i < PER_THREAD; i++)
            {
                bool hit = (i % 3) switch
                {
                    0 => f.MayContain(Encoding.ASCII.GetBytes($"t{t}-{i}")),
                    1 => f.MayContain($"t{t}-{i}".AsSpan()),
                    _ => f.MayContain(new UID128((ulong)t, (ulong)i)),
                };
                Assert.True(hit);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ConcurrentReadersSeeEveryCompletedWrite(string kind)
    {
        var f = Create(kind);
        int failures = 0;

        RunConcurrently(t =>
        {
            for (int i = 0; i < PER_THREAD; i++)
            {
                var key = new UID128((ulong)t + 100, (ulong)i);
                f.Add(key);
                if (!f.MayContain(key)) Interlocked.Increment(ref failures);
                f.MayContain(new UID128((ulong)((t + 1) % THREADS) + 100, (ulong)i));
            }
        });

        Assert.Equal(0, failures);
    }

    [Fact]
    public void ConcurrentBitsEqualSequentialBits()
    {
        var concurrent = AtomicBloomFilter.WithParameters(500, 8);
        var sequential = BloomFilter.WithParameters(500, 8);

        RunConcurrently(t =>
        {
            for (int i = 0; i < PER_THREAD; i++) concurrent.Add(new UID128((ulong)t, (ulong)i));
        });

        for (int t = 0; t < THREADS; t++)
        {
            for (int i = 0; i < PER_THREAD; i++) sequential.Add(new UID128((ulong)t, (ulong)i));
        }

        Assert.Equal(sequential.Blocks.AsSpan().ToArray(), concurrent.Blocks.AsSpan().ToArray());
        Assert.Equal(sequential.Count, concurrent.Count);
    }

    [Fact]
    public void ConcurrentBatchAdds()
    {
        var f = new AtomicBloomFilter(THREADS * PER_THREAD, 0.01);

        RunConcurrently(t =>
        {
            var keys = Enumerable.Range(0, PER_THREAD).Select(i => new UID128((ulong)t, (ulong)i)).ToArray();
            f.Add(keys);
        });

        Assert.Equal((ulong)(THREADS * PER_THREAD), f.Count);

        var probes  = Enumerable.Range(0, PER_THREAD).Select(i => new UID128(3, (ulong)i)).ToArray();
        Assert.Equal(PER_THREAD, f.MayContain(probes, new bool[PER_THREAD]));
    }

    [Fact]
    public void StripedCounterIsExact()
    {
        var counter = new StripedCounter(THREADS);
        Assert.Equal(THREADS, counter.StripeCount);

        RunConcurrently(t =>
        {
            for (int i = 0; i < PER_THREAD; i++) counter.Increment((ulong)(i * 7 + t));
        });

        Assert.Equal((ulong)(THREADS * PER_THREAD), counter.Load());
    }

    [Fact]
    public void StripedCounterStripeCountIsBounded()
    {
        Assert.Equal(1, new StripedCounter(0).StripeCount);
        Assert.Equal(1, new StripedCounter(1).StripeCount);
        Assert.Equal(4, new StripedCounter(3).StripeCount);
        Assert.Equal(StripedCounter.MAX_STRIPES, new StripedCounter(100_000).StripeCount);
        Assert.Equal(64, System.Runtime.CompilerServices.Unsafe.SizeOf<StripedCounter.PaddedCounter>());
    }
}
