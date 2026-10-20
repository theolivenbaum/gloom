# Gloom — C# port of jcalabro/gloom

This repository is **Gloom, a C# port of the Go bloom filter library
[jcalabro/gloom](https://github.com/jcalabro/gloom)**. The Go sources live in `.reference/` and are
the **normative specification: when C# and Go disagree, Go is right.** The Go CI workflow is
disabled (`.github/workflows/ci.yaml.disabled`); do not re-enable it. The `Gloom` package is
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
| `src/Gloom/BlockArray.cs` | the aligned block allocation and the probe / popcount kernels |
| `tests/Gloom.Tests/` | the Go tests; `GoldenTests` runs `.reference/testdata/golden` in place |
| `benchmarks/Gloom.Benchmarks/` | `.reference/benchmarks` (BenchmarkDotNet) |

## Rules

- **The golden files are the compatibility contract.** `.reference/testdata/golden` is never edited,
  copied or regenerated; `GoldenTests` must stay green. A change to hashing, the partitions, the
  block selection or the format that breaks them is a bug in the change.
- **Keys are `Mosaik.Core`'s `UID.UID128`** where a 128-bit value is involved: the hash of every
  key is a `UID128` (`High` = xxh3's high 64 bits). Strings hash as UTF-8.
- `tests/Gloom.Tests/fixtures/params_vectors.json` holds `OptimalParams` /
  `EstimateFalsePositiveRate` values computed by the Go code (floats as bit patterns). Regenerate it
  from `.reference` only if the Go code changes.
- Hot paths allocate nothing; keep it that way. Measure performance changes with
  `benchmarks/Gloom.Benchmarks` (BenchmarkDotNet warms up past tiered JIT) against the Go suite in
  `.reference/benchmarks` on the same machine, and quote only numbers you measured.

## Building and testing

```bash
dotnet build Gloom.slnx -c Release
dotnet test tests/Gloom.Tests -c Release
dotnet run -c Release --project benchmarks/Gloom.Benchmarks -- --filter '*'
cd .reference/benchmarks && GOAMD64=v3 go test -run '^$' -bench . -benchmem   # Go 1.26
```
