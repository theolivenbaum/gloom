using System.Text;
using BenchmarkDotNet.Attributes;
using UID;

namespace Gloom.Benchmarks;

/// <summary>
/// The Go library's benchmarks (.reference/benchmarks/benchmark_test.go), same workloads: a
/// filter for 1M items at 1%, keys "key-0" … "key-999999" cycled through. Each invocation runs
/// over all 1M keys, so the reported time per operation is directly comparable with Go's ns/op.
/// BenchmarkDotNet runs pilot and warm-up iterations first, so tiered JIT is out of the figures.
/// </summary>
[MemoryDiagnoser]
public class FilterBenchmarks
{
    private const int    ITEMS   = 1_000_000;
    private const double FP_RATE = 0.01;

    private byte[][]  _keys     = null!;
    private string[]  _keysStr  = null!;
    private UID128[]  _uids     = null!;
    private bool[]    _results  = null!;

    private BloomFilter              _plain        = null!;
    private AtomicBloomFilter        _atomic       = null!;
    private ShardedAtomicBloomFilter _sharded      = null!;
    private BloomFilter              _plainFull    = null!;
    private AtomicBloomFilter        _atomicFull   = null!;
    private ShardedAtomicBloomFilter _shardedFull  = null!;
    private BloomFilter              _plainUidFull = null!;

    [GlobalSetup]
    public void Setup()
    {
        _keys    = new byte[ITEMS][];
        _keysStr = new string[ITEMS];
        _uids    = new UID128[ITEMS];
        _results = new bool[ITEMS];

        for (int i = 0; i < ITEMS; i++)
        {
            _keysStr[i] = $"key-{i}";
            _keys[i]    = Encoding.UTF8.GetBytes(_keysStr[i]);
            _uids[i]    = UID128.New();
        }

        _plain   = new BloomFilter(ITEMS, FP_RATE);
        _atomic  = new AtomicBloomFilter(ITEMS, FP_RATE);
        _sharded = new ShardedAtomicBloomFilter(ITEMS, FP_RATE, 16);

        _plainFull    = new BloomFilter(ITEMS, FP_RATE);
        _atomicFull   = new AtomicBloomFilter(ITEMS, FP_RATE);
        _shardedFull  = new ShardedAtomicBloomFilter(ITEMS, FP_RATE, 16);
        _plainUidFull = new BloomFilter(ITEMS, FP_RATE);

        foreach (var k in _keys)
        {
            _plainFull.Add(k);
            _atomicFull.Add(k);
            _shardedFull.Add(k);
        }

        foreach (var u in _uids) _plainUidFull.Add(u);
    }

    // ---- Sequential Add --------------------------------------------------------------------

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public void AddSequential_Gloom()
    {
        var f = _plain; var keys = _keys;
        for (int i = 0; i < keys.Length; i++) f.Add(keys[i]);
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public void AddSequential_GloomString()
    {
        var f = _plain; var keys = _keysStr;
        for (int i = 0; i < keys.Length; i++) f.Add(keys[i]);
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public void AddSequential_GloomAtomic()
    {
        var f = _atomic; var keys = _keys;
        for (int i = 0; i < keys.Length; i++) f.Add(keys[i]);
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public void AddSequential_GloomSharded()
    {
        var f = _sharded; var keys = _keys;
        for (int i = 0; i < keys.Length; i++) f.Add(keys[i]);
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public void AddSequential_GloomUID128()
    {
        var f = _plain; var keys = _uids;
        for (int i = 0; i < keys.Length; i++) f.Add(keys[i]);
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public void AddBatch_GloomUID128() => _plain.Add(_uids);

    // ---- Sequential Test -------------------------------------------------------------------

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public int TestSequential_Gloom()
    {
        var f = _plainFull; var keys = _keys; int n = 0;
        for (int i = 0; i < keys.Length; i++) n += f.MayContain(keys[i]) ? 1 : 0;
        return n;
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public int TestSequential_GloomString()
    {
        var f = _plainFull; var keys = _keysStr; int n = 0;
        for (int i = 0; i < keys.Length; i++) n += f.MayContain(keys[i]) ? 1 : 0;
        return n;
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public int TestSequential_GloomAtomic()
    {
        var f = _atomicFull; var keys = _keys; int n = 0;
        for (int i = 0; i < keys.Length; i++) n += f.MayContain(keys[i]) ? 1 : 0;
        return n;
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public int TestSequential_GloomSharded()
    {
        var f = _shardedFull; var keys = _keys; int n = 0;
        for (int i = 0; i < keys.Length; i++) n += f.MayContain(keys[i]) ? 1 : 0;
        return n;
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public int TestSequential_GloomUID128()
    {
        var f = _plainUidFull; var keys = _uids; int n = 0;
        for (int i = 0; i < keys.Length; i++) n += f.MayContain(keys[i]) ? 1 : 0;
        return n;
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public int TestBatch_GloomUID128() => _plainUidFull.MayContain(_uids, _results);

    // ---- Parallel (one worker per core, as Go's RunParallel uses GOMAXPROCS goroutines) ---

    // Like Go's RunParallel: every worker walks the keys from index 0, ITEMS / workers operations each.
    private static void Parallel(int total, Action<int, int> body)
    {
        int workers = Environment.ProcessorCount;
        System.Threading.Tasks.Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, _ => body(0, total / workers));
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public void AddParallel_GloomAtomic() => Parallel(ITEMS, (from, to) =>
    {
        var f = _atomic; var keys = _keys;
        for (int i = from; i < to; i++) f.Add(keys[i]);
    });

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public void AddParallel_GloomSharded() => Parallel(ITEMS, (from, to) =>
    {
        var f = _sharded; var keys = _keys;
        for (int i = from; i < to; i++) f.Add(keys[i]);
    });

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public void TestParallel_GloomAtomic() => Parallel(ITEMS, (from, to) =>
    {
        var f = _atomicFull; var keys = _keys;
        for (int i = from; i < to; i++) f.MayContain(keys[i]);
    });

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public void TestParallel_GloomSharded() => Parallel(ITEMS, (from, to) =>
    {
        var f = _shardedFull; var keys = _keys;
        for (int i = from; i < to; i++) f.MayContain(keys[i]);
    });

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public void Mixed_GloomAtomic() => Parallel(ITEMS, (from, to) =>
    {
        var f = _atomic; var keys = _keys;
        for (int i = from; i < to; i++)
        {
            if ((i & 1) == 0) f.Add(keys[(ITEMS / 2 + i) % ITEMS]);
            else              f.MayContain(keys[i]);
        }
    });

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public void Mixed_GloomSharded() => Parallel(ITEMS, (from, to) =>
    {
        var f = _sharded; var keys = _keys;
        for (int i = from; i < to; i++)
        {
            if ((i & 1) == 0) f.Add(keys[(ITEMS / 2 + i) % ITEMS]);
            else              f.MayContain(keys[i]);
        }
    });
}

/// <summary>
/// A filter far larger than the caches (64M items at 1%, ~77 MB), where every probe is a cache miss:
/// the batch APIs overlap the misses with prefetches. Not in the Go suite.
/// </summary>
public class LargeFilterBenchmarks
{
    private const int ITEMS = 64_000_000;
    private const int OPS   = 4_000_000;

    private UID128[]    _keys    = null!;
    private bool[]      _results = null!;
    private BloomFilter _filter  = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng  = new Random(1);
        _keys    = Enumerable.Range(0, OPS).Select(_ => new UID128((ulong)rng.NextInt64(), (ulong)rng.NextInt64())).ToArray();
        _results = new bool[OPS];
        _filter  = new BloomFilter(ITEMS, 0.01);
    }

    [Benchmark(OperationsPerInvoke = OPS)]
    public void AddLarge_GloomUID128()
    {
        var f = _filter; var keys = _keys;
        for (int i = 0; i < keys.Length; i++) f.Add(keys[i]);
    }

    [Benchmark(OperationsPerInvoke = OPS)]
    public void AddLargeBatch_GloomUID128() => _filter.Add(_keys);

    [Benchmark(OperationsPerInvoke = OPS)]
    public int TestLarge_GloomUID128()
    {
        var f = _filter; var keys = _keys; int n = 0;
        for (int i = 0; i < keys.Length; i++) n += f.MayContain(keys[i]) ? 1 : 0;
        return n;
    }

    [Benchmark(OperationsPerInvoke = OPS)]
    public int TestLargeBatch_GloomUID128() => _filter.MayContain(_keys, _results);
}

/// <summary>Go's BenchmarkHighContention_*: a filter for 1000 items, every worker writing the same 1000 keys.</summary>
public class ContentionBenchmarks
{
    private const int OPS = 1_000_000;

    private byte[][]                 _keys    = null!;
    private AtomicBloomFilter        _atomic  = null!;
    private ShardedAtomicBloomFilter _sharded = null!;

    [GlobalSetup]
    public void Setup()
    {
        _keys    = Enumerable.Range(0, 1000).Select(i => Encoding.UTF8.GetBytes($"key-{i}")).ToArray();
        _atomic  = new AtomicBloomFilter(1000, 0.01);
        _sharded = new ShardedAtomicBloomFilter(1000, 0.01, 16);
    }

    private void Run(Action<byte[]> add)
    {
        int workers = Environment.ProcessorCount;
        System.Threading.Tasks.Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, _ =>
        {
            var keys = _keys;
            for (int i = 0; i < OPS / workers; i++) add(keys[i % 1000]);
        });
    }

    [Benchmark(OperationsPerInvoke = OPS)]
    public void HighContention_GloomAtomic() => Run(k => _atomic.Add(k));

    [Benchmark(OperationsPerInvoke = OPS)]
    public void HighContention_GloomSharded() => Run(k => _sharded.Add(k));
}

/// <summary>Go's BenchmarkThroughput_*: a fresh-sized filter for 800k items, one op = 800k adds.</summary>
public class ThroughputBenchmarks
{
    private const int ITEMS              = 800_000;
    private const int THREADS            = 8;
    private const int ITEMS_PER_THREAD   = ITEMS / THREADS;

    private byte[][]                 _keys    = null!;
    private BloomFilter              _plain   = null!;
    private AtomicBloomFilter        _atomic  = null!;
    private ShardedAtomicBloomFilter _sharded = null!;

    [GlobalSetup]
    public void Setup()
    {
        _keys    = Enumerable.Range(0, ITEMS).Select(i => Encoding.UTF8.GetBytes($"key-{i}")).ToArray();
        _plain   = new BloomFilter(ITEMS, 0.01);
        _atomic  = new AtomicBloomFilter(ITEMS, 0.01);
        _sharded = new ShardedAtomicBloomFilter(ITEMS, 0.01, 16);
    }

    [Benchmark]
    public void Throughput_Gloom()
    {
        var f = _plain; var keys = _keys;
        for (int i = 0; i < keys.Length; i++) f.Add(keys[i]);
    }

    [Benchmark]
    public void Throughput_GloomAtomic() => System.Threading.Tasks.Parallel.For(0, THREADS, new ParallelOptions { MaxDegreeOfParallelism = THREADS }, t =>
    {
        var f = _atomic; var keys = _keys;
        for (int i = t * ITEMS_PER_THREAD; i < (t + 1) * ITEMS_PER_THREAD; i++) f.Add(keys[i]);
    });

    [Benchmark]
    public void Throughput_GloomSharded() => System.Threading.Tasks.Parallel.For(0, THREADS, new ParallelOptions { MaxDegreeOfParallelism = THREADS }, t =>
    {
        var f = _sharded; var keys = _keys;
        for (int i = t * ITEMS_PER_THREAD; i < (t + 1) * ITEMS_PER_THREAD; i++) f.Add(keys[i]);
    });
}

/// <summary>The serialization and fill-ratio paths (CRC-32C and the AVX-512 popcount), not in the Go suite.</summary>
[MemoryDiagnoser]
public class MaintenanceBenchmarks
{
    private BloomFilter _filter = null!;
    private byte[]      _bytes  = null!;

    [GlobalSetup]
    public void Setup()
    {
        _filter = new BloomFilter(1_000_000, 0.01);
        for (int i = 0; i < 1_000_000; i++) _filter.Add(new UID128(1, (ulong)i));
        _bytes = _filter.Serialize();
    }

    [Benchmark] public double EstimatedFillRatio() => _filter.EstimatedFillRatio();

    [Benchmark] public double SampledFillRatio4096() => _filter.SampledFillRatio(4096);

    [Benchmark] public byte[] Serialize() => _filter.Serialize();

    [Benchmark] public BloomFilter Deserialize() => BloomFilter.Deserialize(_bytes);
}
