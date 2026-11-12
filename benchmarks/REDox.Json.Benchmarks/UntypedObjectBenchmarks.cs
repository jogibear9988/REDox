using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using REDox.Serialization;

namespace REDox.Json.Benchmarks;

[MemoryDiagnoser]
public class UntypedObjectBenchmarks
{
    [Params(10, 1000)]
    public int PropertyCount { get; set; }

    private byte[] _json = [];
    private readonly DoxSerializerSettings _settings = new()
    {
        UnknownObjectTypeHandling = UnknownObjectTypeHandling.Dictionary
    };

    [GlobalSetup]
    public void Setup()
    {
        _json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            Enumerable.Range(0, PropertyCount).ToDictionary(i => $"Property{i}", i => i));
        if (Deserialize() is not Dictionary<string, object?> result || result.Count != PropertyCount ||
            !Equals(result[$"Property{PropertyCount - 1}"], (long)(PropertyCount - 1)))
            throw new InvalidOperationException("Dictionary differs");
    }

    [Benchmark]
    public object? Deserialize() => JsonSerializer.Deserialize<object>(_json, _settings);
}
