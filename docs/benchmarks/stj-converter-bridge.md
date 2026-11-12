# System.Text.Json converter bridge allocation check

Measured on 2026-11-12 with BenchmarkDotNet 0.15.8, .NET SDK 10.0.301,
runtime 10.0.9, Windows 11 ARM64 (Apple Silicon, 6 reported cores).
One launch, three warmups, five measured iterations, 250 ms target iteration time.
No builds or test runs ran alongside measured benchmark operations.

The workload is the 1,000-row Guid/TimeSpan custom-converter reproduction from
[upstream issue #7](https://github.com/CAPCOM-TD-OSS/REDox/issues/7).
Both serializers return UTF-8 bytes; settings and input are reused. Setup checks
parsed output equivalence outside measurements. The read case uses the same JSON.

| Operation | Before mean / 99.9% error | After mean / 99.9% error | Before allocated/op | After allocated/op |
| --- | ---: | ---: | ---: | ---: |
| REDox serialize | 431.9 / 83.02 µs | 257.1 / 35.36 µs | 1,283.28 KB | 134.85 KB |
| REDox deserialize | 385.5 / 130.71 µs | 295.0 / 16.94 µs | 241.43 KB | 139.87 KB |
| STJ serialize (control) | 332.1 / 682.42 µs | 353.9 / 675.15 µs | 135.14 KB | 135.15 KB |

Serialization allocations fell about 89.5%; deserialization allocations fell about
42.1%. These are managed bytes allocated per operation, not peak or retained memory.
Timing is noisy, especially the STJ control: do not interpret the means as a universal
speedup or a reliable ranking of serializers. Repeat on the deployment CPU and workload.

The bridge now reuses an exclusive buffer/writer lease, transfers scalar writes
without a JsonDocument or managed string, and encodes scalar reads directly with
the STJ writer. Structured writes retain the JsonDocument fallback and structured
reads retain REDox encoding. Dictionary keys also reuse the bridge.
At most 16 bridges are pooled across all converter types, and buffers larger than
64 KiB are discarded, limiting pooled buffer capacity to 1 MiB (plus writer overhead).
Concurrent and nested calls use separate leases; cleanup runs after exceptions.

Reproduce the benchmark on the parent commit and this branch, with the same benchmark
file and project reference on each:

```powershell
dotnet run -c Release --project benchmarks/REDox.Json.Benchmarks -- --filter '*AdapterConverterBenchmarks*' --job short --warmupCount 3 --iterationCount 5 --iterationTime 250
```

Validation: all 7,707 STJ compatibility tests pass. Bridge regression coverage includes
scalar and structured output, DOX round-trips, escaped/large strings, exception recovery,
recursive calls, concurrent calls, and existing custom dictionary-key converters.
