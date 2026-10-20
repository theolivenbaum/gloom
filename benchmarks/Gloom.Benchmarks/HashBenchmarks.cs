using System.Text;
using BenchmarkDotNet.Attributes;
using UID;

namespace Gloom.Benchmarks;

/// <summary>The hash alone, per key shape: "key-N" as bytes and as a string (short ASCII register path), and a UID128.</summary>
public class HashBenchmarks
{
    private const int KEYS = 1_000_000;

    private byte[][] _keys = null!;
    private string[] _str  = null!;
    private UID128[] _uids = null!;

    [GlobalSetup]
    public void Setup()
    {
        _str  = Enumerable.Range(0, KEYS).Select(i => $"key-{i}").ToArray();
        _keys = _str.Select(Encoding.UTF8.GetBytes).ToArray();
        _uids = Enumerable.Range(0, KEYS).Select(_ => UID128.New()).ToArray();
    }

    [Benchmark(OperationsPerInvoke = KEYS)]
    public ulong HashBytes()
    {
        ulong x = 0; var k = _keys;
        for (int i = 0; i < k.Length; i++) x += BloomHash.Hash(k[i]).Low;
        return x;
    }

    [Benchmark(OperationsPerInvoke = KEYS)]
    public ulong HashString()
    {
        ulong x = 0; var k = _str;
        for (int i = 0; i < k.Length; i++) x += BloomHash.Hash(k[i].AsSpan()).Low;
        return x;
    }

    [Benchmark(OperationsPerInvoke = KEYS)]
    public ulong HashUID128()
    {
        ulong x = 0; var k = _uids;
        for (int i = 0; i < k.Length; i++) x += BloomHash.Hash(k[i]).Low;
        return x;
    }
}
