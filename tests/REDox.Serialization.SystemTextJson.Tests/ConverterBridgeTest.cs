using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace REDox.Serialization.SystemTextJson.Tests;

public class ConverterBridgeTest
{
    private static SystemTextJsonSerializerSettings Settings(JsonConverter converter)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(converter);
        return new SystemTextJsonSerializerSettings(options);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("-9223372036854775808")]
    [InlineData("18446744073709551615")]
    [InlineData("1.234567890123456789e+50")]
    [InlineData("\"plain\"")]
    [InlineData("\"a\\n\\u00E9\\uD83D\\uDE00\\\"\\\\\"")]
    [InlineData("[1,\"two\",null]")]
    [InlineData("{\"nested\":[1,true],\"text\":\"a\\nb\"}")]
    public void ScalarAndStructuredOutputRoundTripAcrossFormats(string json)
    {
        var settings = Settings(new RawConverter());
        var value = new Raw(json);
        var actual = Json.JsonSerializer.Serialize(value, settings);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(actual)));
        var restored = Json.JsonSerializer.Deserialize<Raw>(actual, settings);
        Assert.NotNull(restored);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(restored!.Json)));

        var dox = DoxSerializer.Serialize(value, settings);
        restored = DoxSerializer.Deserialize<Raw>(dox, settings);
        Assert.NotNull(restored);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(restored!.Json)));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(100000)]
    public void LargeEscapedStringsDoNotCorruptSubsequentCalls(int size)
    {
        var settings = Settings(new RawConverter());
        var text = new string('\n', size) + "é😀";
        var json = JsonSerializer.Serialize(text);
        Assert.Equal(json, Json.JsonSerializer.Serialize(new Raw(json), settings));
        Assert.Equal("true", Json.JsonSerializer.Serialize(new Raw("true"), settings));
    }

    [Fact]
    public void FailedConvertersReturnCleanBridgeState()
    {
        var converter = new RawConverter { Fail = true };
        var settings = Settings(converter);
        Assert.Throws<InvalidOperationException>(() => Json.JsonSerializer.Serialize(new Raw("1"), settings));
        var exception = Assert.Throws<SerializationException>(() => Json.JsonSerializer.Deserialize<Raw>("1", settings));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        converter.Fail = false;
        Assert.Equal("2", Json.JsonSerializer.Serialize(new Raw("2"), settings));
        Assert.Equal("3", Json.JsonSerializer.Deserialize<Raw>("3", settings)!.Json);
    }

    [Fact]
    public void ConcurrentAndRecursiveCallsUseIndependentBridges()
    {
        var settings = Settings(new RecursiveConverter());
        Parallel.For(0, 1000, index =>
        {
            var json = Json.JsonSerializer.Serialize(new Recursive(3, index), settings);
            Assert.Equal(index.ToString(System.Globalization.CultureInfo.InvariantCulture), json);
            Assert.Equal(index, Json.JsonSerializer.Deserialize<Recursive>(json, settings)!.Value);
        });
    }

    public sealed record Raw(string Json);
    public sealed record Recursive(int Depth, int Value);

    private sealed class RawConverter : JsonConverter<Raw>
    {
        public bool Fail { get; set; }
        public override bool HandleNull => true;

        public override Raw Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (Fail) throw new InvalidOperationException("Read failed");
            using var doc = JsonDocument.ParseValue(ref reader);
            return new Raw(doc.RootElement.GetRawText());
        }

        public override void Write(Utf8JsonWriter writer, Raw value, JsonSerializerOptions options)
        {
            if (Fail)
            {
                writer.WriteStartObject();
                throw new InvalidOperationException("Write failed");
            }
            writer.WriteRawValue(value.Json);
        }
    }

    private sealed class RecursiveConverter : JsonConverter<Recursive>
    {
        public override Recursive Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var value = reader.GetInt32();
            // Exercise nested read leases without infinite recursion.
            var settings = Settings(new RawConverter());
            Assert.Equal("true", Json.JsonSerializer.Deserialize<Raw>("true", settings)!.Json);
            return new Recursive(0, value);
        }

        public override void Write(Utf8JsonWriter writer, Recursive value, JsonSerializerOptions options)
        {
            if (value.Depth == 0)
                writer.WriteNumberValue(value.Value);
            else
                writer.WriteRawValue(Json.JsonSerializer.Serialize(value with { Depth = value.Depth - 1 },
                    Settings(this)));
        }
    }
}
