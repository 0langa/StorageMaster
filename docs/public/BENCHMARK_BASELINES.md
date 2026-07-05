# Benchmark baselines

Captured with `dotnet run -c Release --project benchmarks/StorageMaster.Benchmarks -- --filter "*"`.

Baselines are machine-specific. Compare new numbers only against a baseline captured on the same hardware class, and re-capture after touching hashing, normalization, or the duplicate candidate query.

## Baseline: NVMe laptop (2026-07-05, v2.1.4)

| Component | Value |
|---|---|
| CPU | 11th Gen Intel Core i5-1135G7 @ 2.40 GHz, 4 physical / 8 logical cores |
| RAM | 16 GB |
| Disk | KIOXIA KBG40ZNS256G (NVMe, 256 GB) |
| OS | Windows 11 25H2 (build 26200.8737) |
| Runtime | .NET 8.0.28, X64 RyuJIT x86-64-v4, BenchmarkDotNet v0.15.8 |

| Benchmark | Mean | Error | Allocated |
|---|---:|---:|---:|
| `Sha256_8MiB` | 7.905 ms | 0.142 ms | 7.32 KB |
| `PartialHash_8MiB` | 319.7 us | 6.16 us | 133.21 KB |
| `NormalizedText_100kLines` | 10.906 ms | 0.362 ms | 21.3 MB |
| `CandidateQuery_SameSizeBuckets` (10k entries) | 39.81 ms | 0.515 ms | 5.75 MB |

Notes:

- `Sha256_8MiB` ≈ 1.0 GB/s single-stream hashing on this CPU; partial hashing is ~25× cheaper, which is why the dedupe pipeline pre-filters with it.
- `NormalizedText_100kLines` allocation (21 MB per operation) is dominated by per-line string materialization; acceptable because it only runs on review-mode text candidates.
- HDD and SATA SSD baselines from `docs/public/STORAGEMASTER_3_AUDIT.md` remain open — capture on representative machines when available. The benchmarks above are CPU/SQLite-bound and transfer reasonably; scanner throughput baselines are the disk-sensitive ones.
