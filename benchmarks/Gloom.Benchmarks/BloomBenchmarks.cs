using BenchmarkDotNet.Attributes;
using UID;

namespace Gloom.Benchmarks;

/// <summary>
/// <see cref="Bloom"/> against bits-and-blooms/bloom (.reference/bloom): 1M items at 1%, random
/// keys. The Go library hashes its 16-byte keys with murmur3 on every call; a UID128 key is used as
/// its own hash, so the C# figures carry no hashing.
/// </summary>
[MemoryDiagnoser]
public class BloomBenchmarks
{
    private const int ITEMS = 1_000_000;

    private UID128[] _keys    = null!;
    private UID128[] _misses  = null!;
    private Bloom    _empty   = null!;
    private Bloom    _full    = null!;
    private Bloom    _other   = null!;

    [GlobalSetup]
    public void Setup()
    {
        _keys    = Enumerable.Range(0, ITEMS).Select(_ => UID128.New()).ToArray();
        _misses  = Enumerable.Range(0, ITEMS).Select(_ => UID128.New()).ToArray();
        _empty   = Bloom.WithEstimates(ITEMS, 0.01);
        _full    = Bloom.WithEstimates(ITEMS, 0.01);
        _other   = Bloom.WithEstimates(ITEMS, 0.01);

        foreach (var k in _keys) _full.Add(k);
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public void BloomAdd()
    {
        var f = _empty; var keys = _keys;
        for (int i = 0; i < keys.Length; i++) f.Add(keys[i]);
    }


    [Benchmark(OperationsPerInvoke = ITEMS)]
    public int BloomTestHit()
    {
        var f = _full; var keys = _keys; int n = 0;
        for (int i = 0; i < keys.Length; i++) n += f.MayContain(keys[i]) ? 1 : 0;
        return n;
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public int BloomTestHitScalar()
    {
        var f = _full; var keys = _keys; int n = 0;
        for (int i = 0; i < keys.Length; i++) n += f.MayContainScalar(keys[i]) ? 1 : 0;
        return n;
    }


    [Benchmark(OperationsPerInvoke = ITEMS)]
    public int BloomTestMiss()
    {
        var f = _full; var keys = _misses; int n = 0;
        for (int i = 0; i < keys.Length; i++) n += f.MayContain(keys[i]) ? 1 : 0;
        return n;
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public int BloomTestMissScalar()
    {
        var f = _full; var keys = _misses; int n = 0;
        for (int i = 0; i < keys.Length; i++) n += f.MayContainScalar(keys[i]) ? 1 : 0;
        return n;
    }

    [Benchmark(OperationsPerInvoke = ITEMS)]
    public int BloomTestAndAdd()
    {
        var f = _empty; var keys = _keys; int n = 0;
        for (int i = 0; i < keys.Length; i++) n += f.TestAndAdd(keys[i]) ? 1 : 0;
        return n;
    }

    [Benchmark] public ulong BloomApproximatedSize() => _full.ApproximatedSize();

    [Benchmark] public void BloomMerge() => _empty.Merge(_other);

    [Benchmark] public byte[] BloomSerialize() => _full.Serialize();
}

/// <summary><see cref="Bloom"/> far larger than the caches: 64M items at 1% (~77 MB), 4M random keys.</summary>
public class BloomLargeBenchmarks
{
    private const int OPS = 4_000_000;

    private UID128[] _keys    = null!;
    private Bloom    _filter  = null!;

    [GlobalSetup]
    public void Setup()
    {
        _keys    = Enumerable.Range(0, OPS).Select(_ => UID128.New()).ToArray();
        _filter  = Bloom.WithEstimates(64_000_000, 0.01);
        foreach (var k in _keys) _filter.Add(k);
    }

    [Benchmark(OperationsPerInvoke = OPS)]
    public void BloomAddLarge()
    {
        var f = _filter; var keys = _keys;
        for (int i = 0; i < keys.Length; i++) f.Add(keys[i]);
    }


    [Benchmark(OperationsPerInvoke = OPS)]
    public int BloomTestLarge()
    {
        var f = _filter; var keys = _keys; int n = 0;
        for (int i = 0; i < keys.Length; i++) n += f.MayContain(keys[i]) ? 1 : 0;
        return n;
    }

}
