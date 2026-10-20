# Gloom — C# ports of jcalabro/gloom and bits-and-blooms/bloom

This repository is **Gloom, C# ports of two Go bloom filter libraries**, one folder each under
`.reference/`:

- `.reference/gloom/` — [jcalabro/gloom](https://github.com/jcalabro/gloom), ported as
  `BloomFilter` / `AtomicBloomFilter` / `ShardedAtomicBloomFilter`. It is the **normative
  specification, bit for bit: when C# and Go disagree, Go is right.**
- `.reference/bloom/` — [bits-and-blooms/bloom](https://github.com/bits-and-blooms/bloom) (the
  theolivenbaum fork), ported as `Bloom`. **Not bit-compatible on purpose**: keys are `UID128` only,
  already hash-like, and are never hashed again; the Go code is the reference for behaviour and API,
  not for bits or format.

The Go CI workflows are disabled (`.github/workflows/ci.yaml.disabled`, and `.disabled` on the
copies under `.reference/bloom`); do not re-enable them. The `Gloom` package is
published to NuGet by Azure DevOps, [`.devops/azure-pipelines.yml`](.devops/azure-pipelines.yml),
on every push to `main` (CalVer `yy.M.<buildId>`, the repository README as the package README).

## Layout

| Path | Port of |
| --- | --- |
| `src/Gloom/BloomFilter.cs`, `BloomFilter.Serialization.cs` | `Filter`, `MarshalBinary` / `UnmarshalBinary` (bloom.go) |
| `src/Gloom/AtomicBloomFilter.cs`, `ShardedAtomicBloomFilter.cs` | `AtomicFilter`, `ShardedAtomicFilter` (bloom.go) |
| `src/Gloom/BloomHash.cs` | hash.go (xxh3-128 through `System.IO.Hashing`, plus an inlined 16-byte path for `UID128`) |
| `src/Gloom/BloomParameters.cs`, `PrimePartition.cs` | params.go |
| `src/Gloom/StripedCounter.cs` | counter.go |
| `src/Gloom/Bloom.cs` | bloom.go of bits-and-blooms/bloom, on `UID128` keys (double hashing of the key's halves, multiply-shift reduction) |
| `src/Gloom/BlockArray.cs` | the aligned block allocation and the probe / popcount kernels |
| `tests/Gloom.Tests/BloomTests.cs` | bloom_test.go of bits-and-blooms/bloom |
| `tests/Gloom.Tests/` | the gloom Go tests; `GoldenTests` runs `.reference/gloom/testdata/golden` in place |
| `benchmarks/Gloom.Benchmarks/` | `.reference/gloom/benchmarks` (BenchmarkDotNet) |

## Rules

- **The golden files are the compatibility contract.** `.reference/gloom/testdata/golden` is never edited,
  copied or regenerated; `GoldenTests` must stay green. A change to hashing, the partitions, the
  block selection or the format that breaks them is a bug in the change.
- **Keys are `Mosaik.Core`'s `UID.UID128`** where a 128-bit value is involved: the hash of every
  key is a `UID128` (`High` = xxh3's high 64 bits). Strings hash as UTF-8.
- **`Bloom` takes only `UID128` keys and treats them as hashes.** Do not add a hashing step or
  byte/string overloads to it; a caller with a structured key hashes it first.
- `tests/Gloom.Tests/fixtures/params_vectors.json` holds `OptimalParams` /
  `EstimateFalsePositiveRate` values computed by the Go code (floats as bit patterns). Regenerate it
  from `.reference/gloom` only if the Go code changes.
- Hot paths allocate nothing; keep it that way. Measure performance changes with
  `benchmarks/Gloom.Benchmarks` (BenchmarkDotNet warms up past tiered JIT) against the Go suite in
  `.reference/gloom/benchmarks` on the same machine, and quote only numbers you measured.

## Building and testing

```bash
dotnet build Gloom.slnx -c Release
dotnet test tests/Gloom.Tests -c Release
dotnet run -c Release --project benchmarks/Gloom.Benchmarks -- --filter '*'
cd .reference/gloom/benchmarks && GOAMD64=v3 go test -run '^$' -bench . -benchmem   # Go 1.26
cd .reference/bloom && go test -run '^$' -bench . -benchmem
```
