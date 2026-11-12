# Untyped dictionary capacity allocation check

Untyped object deserialization with `UnknownObjectTypeHandling.Dictionary` constructed
an empty dictionary and repeatedly grew it while reading entries. The token DOM already
records the map entry count. Passing that count as the initial capacity avoids repeated
bucket and entry-array allocation, including for nested objects.

Measured on 2026-11-12 with BenchmarkDotNet 0.15.8, .NET SDK 10.0.301,
runtime 10.0.9, Windows 11 ARM64 (Apple Silicon, 6 reported cores).
One launch, three warmups, five measured iterations, 250 ms target iteration time.
No builds or tests ran alongside measured operations. UTF-8 input and settings are
reused; setup verifies the resulting dictionary outside the measurements.

| Properties | Before mean / 99.9% error | After mean / 99.9% error | Before allocated/op | After allocated/op |
| ---: | ---: | ---: | ---: | ---: |
| 10 | 483.9 / 41.88 ns | 437.1 / 70.49 ns | 1.59 KB | 1.05 KB |
| 1,000 | 52.790 / 4.418 µs | 44.659 / 0.955 µs | 170.05 KB | 100.52 KB |

The 1,000-property workload allocates about 40.9% less managed memory and has a 15.4%
lower measured mean. The 10-property timing confidence intervals overlap. These short
local measurements are workload-specific, and do not establish universal speedups.
Allocation figures describe managed bytes per operation, not retained heap or peak RAM.
For duplicate keys, capacity is based on entry count, which may exceed distinct-key count;
dictionary overwrite semantics are unchanged.

```powershell
dotnet run -c Release --project benchmarks/REDox.Json.Benchmarks -- --filter '*UntypedObjectBenchmarks*' --job short --warmupCount 3 --iterationCount 5 --iterationTime 250
```

For the baseline, use the same benchmark file on the parent commit before the capacity change.

Additional candidates identified by code review, not yet measured or changed:

- `IEnumerableConverter<T, U>.ReadItems` in `GenericCollectionConverter.cs` creates a list
  and then copies it to an array when `UnknownArrayTypeHandling.SzArray` is selected.
  Direct array filling could remove the temporary list and backing array, but must preserve
  error-handler semantics (failed elements can be skipped).
- Parameterized construction in `ClassTypeConverter.cs` allocates token scratch arrays and
  deferred-property lists per object. Pooling or stack storage may help record-heavy workloads;
  nested calls, exception recovery, and retained-memory bounds require dedicated measurement.
- Structured STJ converter output still uses a JsonDocument bridge. A token-stream transfer
  would need correct container lengths for formats such as CBOR and DOX, plus numeric fidelity.

These candidates need representative workload benchmarks before implementation.
