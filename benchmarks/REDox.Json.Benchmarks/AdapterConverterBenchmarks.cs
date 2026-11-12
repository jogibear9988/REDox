using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using REDox.Serialization.SystemTextJson;

namespace REDox.Json.Benchmarks;

[MemoryDiagnoser]
public class AdapterConverterBenchmarks
{
    private readonly JsonSerializerOptions _options = new();
    private readonly SystemTextJsonSerializerSettings _settings;
    private readonly Row[] _rows;
    private readonly byte[] _json;

    public AdapterConverterBenchmarks()
    {
        _options.Converters.Add(new GuidConverter());
        _options.Converters.Add(new DurationConverter());
        _settings = new SystemTextJsonSerializerSettings(_options);
        _rows = Enumerable.Range(0, 1000).Select(index => new Row
        {
            Id = new Guid(index, 0, 0, new byte[8]),
            Name = $"Row{index}",
            Duration = TimeSpan.FromSeconds(index),
            Value = index * 1.25
        }).ToArray();
        _json = SystemText();
    }

    [GlobalSetup]
    public void Verify()
    {
        if (!JsonNode.DeepEquals(JsonNode.Parse(_json), JsonNode.Parse(Redox())))
            throw new InvalidOperationException("JSON differs");
        if (RedoxRead()!.Length != _rows.Length)
            throw new InvalidOperationException("Row count differs");
    }

    [Benchmark(Baseline = true)]
    public byte[] SystemText() => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_rows, _options);

    [Benchmark]
    public byte[] Redox() => JsonSerializer.SerializeToUtf8Bytes(_rows, _settings);

    [Benchmark]
    public Row[]? RedoxRead() => JsonSerializer.Deserialize<Row[]>(_json, _settings);

    public sealed class Row
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public TimeSpan Duration { get; set; }
        public double Value { get; set; }
    }

    private sealed class GuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetGuid();
        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    private sealed class DurationConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => TimeSpan.Parse(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
