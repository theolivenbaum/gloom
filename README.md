# Gloom

A high-performance bloom filter for .NET, implementing cache-line blocked one-hashing (OHBF). A C#
port of [jcalabro/gloom](https://github.com/jcalabro/gloom) (Go); the original sources live in
[`.reference/`](https://github.com/theolivenbaum/gloom/tree/main/.reference) and are the
specification. Filters serialized by either library load in the other.

```
dotnet add package Gloom
```

## Features

- **Cache-line optimized**: all k bit probes for a key land in one 64-byte, 64-byte-aligned block,
  so an add or a lookup touches one cache line.
- **One hash per key**: a single xxh3-128 call (`System.IO.Hashing.XxHash128`); the high 64 bits
  pick the block with a multiply-shift reduction (no modulo bias, no capacity cliff), the low 64
  bits are folded into the intra-block hash that is reduced modulo k pairwise-coprime partition
  sizes. The reductions use Lemire's fastmod (two multiplies) instead of a hardware divide.
- **`UID128` keys**: built on [`Mosaik.Core`](https://www.nuget.org/packages/Mosaik.Core)'s
  `UID.UID128`. The 128-bit hash of every key *is* a `UID128`, a `UID128` key is hashed with an
  inlined fixed-length xxh3 path, and a precomputed hash can be added directly (`AddHash`).
- **Batch APIs** for `UID128` keys and hashes: hash a window of keys, prefetch their blocks, then
  probe, so cache misses overlap.
- **Three implementations**:
  - `BloomFilter` — not thread-safe, fastest single-threaded; serializable (with a CRC-32C check).
  - `AtomicBloomFilter` — lock-free (`Interlocked.Or`, skipped when the bit is already set), with a
    striped counter so concurrent writes scale.
  - `ShardedAtomicBloomFilter` — independent atomic shards for the highest write concurrency.
- **Accurate sizing**: `BloomParameters.Optimal` compensates for the blocking penalty so the realized
  false-positive rate meets the target; `AchievableFalsePositiveRate` reports targets a 512-bit block
  cannot meet.
- **Zero allocations** on every add and lookup path. AVX-512 population count for the fill-ratio
  scans, hardware CRC-32C for serialization.

## Usage

```csharp
using Gloom;
using UID;

// 1 million items at a 1% false-positive rate.
var filter = new BloomFilter(1_000_000, 0.01);

filter.Add("hello"u8);                 // bytes
filter.Add("world");                   // strings are hashed as UTF-8
filter.Add(UID128.New());              // UID128 keys

if (filter.MayContain("hello"u8)) { /* might be present */ }
if (!filter.MayContain("not-added")) { /* definitely not present */ }

// Batches of UID128 keys (prefetched).
UID128[] uids = /* ... */ [];
filter.Add(uids);
var results = new bool[uids.Length];
int hits = filter.MayContain(uids, results);

// Precomputed hashes: BloomHash.Hash(...) returns the xxh3-128 hash as a UID128.
UID128 hash = BloomHash.Hash("some key");
filter.AddHash(hash);
```

### Thread-safe

```csharp
var filter = new AtomicBloomFilter(1_000_000, 0.01);

Parallel.For(0, 8, t =>
{
    for (int i = 0; i < 100_000; i++) filter.Add($"key-{t}-{i}");
});

// Or, for the last bit of write throughput on many-core machines:
var sharded = new ShardedAtomicBloomFilter(1_000_000, 0.01);        // one shard per core, at least 4
var sixteen = new ShardedAtomicBloomFilter(1_000_000, 0.01, 16);    // rounded up to a power of two
```

### Explicit parameters and statistics

```csharp
// 1000 512-bit blocks, 7 hash functions.
var f = BloomFilter.WithParameters(1000, 7);

Console.WriteLine($"Capacity: {f.Capacity} bits");
Console.WriteLine($"Hash functions: {f.K}");
Console.WriteLine($"Items added: {f.Count}");
Console.WriteLine($"Fill ratio: {f.EstimatedFillRatio():P2}");          // scans the whole array
Console.WriteLine($"Fill ratio (sampled): {f.SampledFillRatio(4096):P2}");
Console.WriteLine($"Est. FP rate: {f.EstimatedFalsePositiveRate():P4}");

var (numBlocks, k, bitsPerItem) = BloomParameters.Optimal(1_000_000, 0.001);
var (achievable, met)           = BloomParameters.AchievableFalsePositiveRate(1_000_000, 1e-12);
```

### Serialization

```csharp
byte[] bytes = filter.Serialize();
BloomFilter copy = BloomFilter.Deserialize(bytes);           // throws BloomFilterFormatException

if (!BloomFilter.TryDeserialize(bytes, out var loaded, out var error, out var message)) { /* ... */ }
```

The format is the Go library's `MarshalBinary` (version 1): version, k, block count and item count,
the block words, then a CRC-32C trailer, all little-endian. Only `BloomFilter` is serializable: a
concurrent filter cannot be snapshotted soundly while writers run.

## Compatibility with the Go library

The key-to-bit mapping, the partitions and the serialized format are the Go library's, bit for bit.
The test suite runs the Go library's golden files in place
([`.reference/testdata/golden`](https://github.com/theolivenbaum/gloom/tree/main/.reference/testdata/golden)):
every filter Go serialized loads and answers identically, and rebuilding each recipe here produces
the same bytes. `BloomParameters` is checked against values computed by the Go code.

Two differences in the API surface:

- A `string` (or `ReadOnlySpan<char>`) key is hashed as its UTF-8 bytes — what Go's `AddString`
  hashes for valid UTF-8. A Go string holding invalid UTF-8 corresponds to the `ReadOnlySpan<byte>`
  overload here.
- The largest filter is `BloomFilter.MaxNumBlocks` (≈2.7 × 10⁸ blocks, 17 GB), the limit of one .NET
  array, against Go's 2⁵⁰ blocks. Deserializing a larger Go filter fails with `InvalidData`.

## Design

```
                 key
                  │
          xxh3-128 (one call)  ──►  UID128 { High, Low }
                  │
       ┌──────────┴───────────┐
       ▼                      ▼
 block = High·n >> 64     h = (uint)Low ^ (uint)(Low >> 32)
       │                      │
       ▼                      ▼
 ┌───────────────────── one 512-bit block (one cache line) ─────────────────────┐
 │ │ 61 bits │ 67 bits │ 71 bits │ 79 bits │ 83 bits │ 89 bits │ 62 bits │      │
 │     h%61      h%67      h%71      h%79      h%83      h%89      h%62  (k = 7) │
 └───────────────────────────────────────────────────────────────────────────────┘
```

The partition sizes are pairwise coprime and sum to 512, so one hash reduced modulo each gives k
independent positions (a Chinese Remainder Theorem argument).

### References

- [Space/Time Trade-offs in Hash Coding with Allowable Errors](https://dl.acm.org/doi/10.1145/362686.362692) (Bloom, 1970)
- [One-Hashing Bloom Filter](https://yangtonghome.github.io/uploads/One_Hashing.pdf) — the prime partition technique
- [RocksDB Bloom Filter](https://github.com/facebook/rocksdb/wiki/RocksDB-Bloom-Filter) — cache-line blocking
- [Faster Remainder by Direct Computation](https://arxiv.org/abs/1902.01961) (Lemire, Kaser, Kurz, 2019) — fastmod
- [xxHash](https://github.com/Cyan4973/xxHash)

## Performance

Measured 2026-10-20 on a 4-vCPU shared VM: Intel Xeon @ 2.10 GHz (Sapphire Rapids class, AVX-512).
.NET 10.0.13 with BenchmarkDotNet 0.15.8, default job: pilot, warm-up and at least 15 measured
iterations, so tiered JIT is out of the figures. Go 1.26.0 with `GOAMD64=v3`, `-count=5`, median
of the five. Same workloads on both sides: the Go suite's (`.reference/benchmarks`), a filter for
1M items at 1%, keys `key-0` … `key-999999`. Time per operation; lower is better.

| Benchmark | Go | C# | |
| --- | ---: | ---: | --- |
| Add, bytes | 37.8 ns | 37.9 ns | parity |
| Add, string | 36.6 ns | 42.0 ns | UTF-16 strings are transcoded to UTF-8 |
| Add, atomic | 80.4 ns | 38.8 ns | the locked OR is skipped when the bit is set |
| Add, sharded (16) | 78.9 ns | 41.6 ns | |
| Test, bytes | 38.9 ns | 37.4 ns | |
| Test, string | 35.7 ns | 40.1 ns | |
| Test, atomic | 38.8 ns | 37.8 ns | |
| Test, sharded (16) | 43.6 ns | 40.2 ns | |
| Add, 4 threads, atomic | 37.6 ns | 38.1 ns | parity |
| Add, 4 threads, sharded | 35.2 ns | 25.9 ns | |
| Test, 4 threads, atomic | 9.6 ns | 10.4 ns | |
| Test, 4 threads, sharded | 11.4 ns | 11.7 ns | |
| Mixed 50/50, 4 threads, atomic | 38.2 ns | 22.1 ns | |
| Mixed 50/50, 4 threads, sharded | 28.0 ns | 18.9 ns | |
| High contention (1000 items), atomic | 68.3 ns | 37.4 ns | |
| High contention (1000 items), sharded | 52.4 ns | 27.7 ns | |
| Throughput, 800k adds | 25.7 ms | 18.0 ms | |
| Throughput, 800k adds, 8 threads, atomic | 35.4 ms | 31.9 ms | |
| Throughput, 800k adds, 8 threads, sharded | 30.2 ms | 20.1 ms | |
| `EstimatedFillRatio`, 1M-item filter | 78.6 µs | 35.3 µs | AVX-512BW popcount |
| `SampledFillRatio(4096)` | 53.4 µs | 11.5 µs | |
| Serialize, 1M-item filter (1.3 MB) | 482 µs | 473 µs | 3-way interleaved hardware CRC-32C |
| Deserialize | 462 µs | 434 µs | |

`UID128` keys, which the Go library does not have; the Go column is the same filter fed random
16-byte keys:

| Benchmark | Go (16-byte key) | C# (`UID128`) |
| --- | ---: | ---: |
| Add, 1M-item filter | 42.1 ns | 20.3 ns (batch: 18.9 ns) |
| Test, 1M-item filter | 40.2 ns | 20.0 ns (batch: 19.8 ns) |
| Add, 64M-item filter (77 MB), 4M random keys | 133.7 ns | 88.6 ns (batch: **30.0 ns**) |
| Test, empty 64M-item filter | 66.7 ns | 10.4 ns (batch: 13.8 ns) |

The hash alone: 3.7 ns for a `key-N` byte key, 7.2 ns for the same string, 2.6 ns for a `UID128`.
On a filter that fits in cache the batch APIs gain little; on one that does not, batched adds are
~3× faster because the prefetches overlap the misses. Batched *tests* gain nothing on a mostly
empty filter (the first probe already answers, and the CPU overlaps the independent lookups on its
own) — the large-filter test rows above are that case.

What the numbers come from:

- **Lemire fastmod** for the k partition reductions (two multiplies, no `DIV`).
- **An inlined xxh3 for 16-byte `UID128` keys**, and a register-only xxh3 for 4–16-char ASCII strings:
  writing a string's UTF-8 bytes and reading them back as words stalls on store-to-load forwarding
  (18.7 → 7.2 ns per string hash).
- **Check before `Interlocked.Or`**: bits are only ever set, so a bit seen set needs no locked
  instruction and no cache-line ownership transfer.
- **Cache-line aligned blocks on the pinned object heap**, probed through a raw pointer (no bounds
  checks), and a cache-line padded striped counter.
- **AVX-512BW popcount** (nibble lookup + `VPSADBW`; .NET exposes no `VPOPCNTQ`) and **three interleaved
  CRC-32C streams** merged by GF(2) multiplication.

## Building, testing, benchmarking

```bash
dotnet build Gloom.slnx -c Release
dotnet test tests/Gloom.Tests -c Release
dotnet run -c Release --project benchmarks/Gloom.Benchmarks -- --filter '*'
```

The Go reference benchmarks: `cd .reference/benchmarks && GOAMD64=v3 go test -bench=. -benchmem`.

The package is published to nuget.org by Azure DevOps
([`.devops/azure-pipelines.yml`](https://github.com/theolivenbaum/gloom/blob/main/.devops/azure-pipelines.yml))
on every push to `main`, versioned `yy.M.<buildId>`.

## License

MIT, as the original. Copyright (c) 2026 jcalabro (the Go library) and Curiosity GmbH (this port).
