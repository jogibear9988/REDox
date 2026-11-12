// SPDX-FileCopyrightText: 2026 CAPCOM CO., LTD.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace REDox.Serialization.SystemTextJson;

public sealed class JsonConverterAdapter : DataConverterFactory
{
    private readonly JsonSerializerOptions _options;

    public JsonConverterAdapter(JsonConverter converter, JsonSerializerOptions? options = null)
    {
        Converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _options = options ?? JsonSerializerOptions.Default;
    }

    public JsonConverter Converter { get; }

    // Shared across converter types. Leases are exclusive, including during recursive
    // converter calls; oversized buffers are discarded rather than retained in the pool.
    private sealed class Bridge
    {
        private static readonly Bridge?[] s_pool = new Bridge?[16];
        public readonly ArrayBufferWriter<byte> Buffer = new(256);
        public readonly Utf8JsonWriter Writer;

        private Bridge() => Writer = new Utf8JsonWriter(Buffer);

        public static Bridge Rent()
        {
            for (var i = 0; i < s_pool.Length; i++)
            {
                var bridge = Interlocked.Exchange(ref s_pool[i], null);
                if (bridge != null)
                {
                    return bridge;
                }
            }
            return new Bridge();
        }

        public void Return()
        {
            Writer.Reset(Buffer);
            Buffer.Clear();
            if (Buffer.Capacity <= 64 * 1024)
            {
                for (var i = 0; i < s_pool.Length; i++)
                {
                    if (Interlocked.CompareExchange(ref s_pool[i], this, null) == null)
                    {
                        return;
                    }
                }
            }
            Writer.Dispose();
        }
    }

    public override bool CanConvert(Type type)
    {
        return Converter.CanConvert(type);
    }

    public override DataConverter CreateConverter(Type type, SerializerSettings settings)
    {
        var converter = Converter;

        if (converter is JsonConverterFactory factory)
        {
            converter = factory.CreateConverter(type, _options)
                        ?? throw new InvalidOperationException(
                            $"'{factory.GetType()}' cannot create a converter for '{type}'.");
        }

        var adapterType = typeof(AdapterConverter<>).MakeGenericType(type);

        return (DataConverter)Activator.CreateInstance(adapterType, converter, _options)!;
    }

    private sealed class AdapterConverter<T> : DataConverter<T>
    {
        private static readonly JsonElementConverter s_elementConverter = new();

        private readonly JsonConverter<T> _converter;
        private readonly JsonSerializerOptions _options;

        public AdapterConverter(JsonConverter converter, JsonSerializerOptions? options = null)
        {
            _converter = converter as JsonConverter<T>
                         ?? throw new ArgumentException(
                             $"'{converter.GetType()}' is not a converter for '{typeof(T)}'.", nameof(converter));
            _options = options ?? JsonSerializerOptions.Default;
        }

        public override T ReadAsPropertyName(in DataReader reader, uint tokenId)
        {
            var propertyName = reader.ReadString(tokenId);
            var bridge = Bridge.Rent();
            try
            {
                var utf8Writer = bridge.Writer;
                utf8Writer.WriteStartObject();
                utf8Writer.WritePropertyName(propertyName);
                utf8Writer.WriteNullValue();
                utf8Writer.WriteEndObject();
                utf8Writer.Flush();

                var utf8Reader = new Utf8JsonReader(bridge.Buffer.WrittenSpan);

                if (!utf8Reader.Read() || utf8Reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new JsonException();
                }

                if (!utf8Reader.Read() || utf8Reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException();
                }

                return _converter.ReadAsPropertyName(ref utf8Reader, typeof(T), _options);
            }
            finally
            {
                bridge.Return();
            }
        }

        public override void WriteAsPropertyName(DataWriter writer, T value)
        {
            if (value is null)
            {
                throw new JsonException();
            }

            var bridge = Bridge.Rent();
            try
            {
                var utf8Writer = bridge.Writer;
                utf8Writer.WriteStartObject();
                _converter.WriteAsPropertyName(utf8Writer, value, _options);
                utf8Writer.WriteNullValue();
                utf8Writer.WriteEndObject();
                utf8Writer.Flush();

                var utf8Reader = new Utf8JsonReader(bridge.Buffer.WrittenSpan);

                if (!utf8Reader.Read() || utf8Reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new JsonException();
                }

                if (!utf8Reader.Read() || utf8Reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException();
                }

                writer.WriteString(utf8Reader.GetString() ?? throw new JsonException());
            }
            finally
            {
                bridge.Return();
            }
        }

        public override T? Read(in DataReader reader, uint tokenId, T? existingValue)
        {
            var bridge = Bridge.Rent();
            try
            {
                // Scalars do not need a second REDox writer and its streaming buffer.
                switch (reader.GetToken(tokenId).Kind)
                {
                    case DTokenKind.String:
                    case DTokenKind.Symbol:
                        bridge.Writer.WriteStringValue(reader.ReadUtf8String(tokenId));
                        bridge.Writer.Flush();
                        break;
                    case DTokenKind.Boolean:
                        bridge.Writer.WriteBooleanValue(reader.ReadBoolean(tokenId));
                        bridge.Writer.Flush();
                        break;
                    case DTokenKind.Null:
                        bridge.Writer.WriteNullValue();
                        bridge.Writer.Flush();
                        break;
                    default:
                        Json.JsonDocument.EncodeTo(reader.ReadElement(tokenId), bridge.Buffer);
                        break;
                }
                var utf8Reader = new Utf8JsonReader(bridge.Buffer.WrittenSpan);

                if (!utf8Reader.Read())
                {
                    return default;
                }

                if (utf8Reader.TokenType == JsonTokenType.Null && !_converter.HandleNull)
                {
                    return default;
                }

                return _converter.Read(ref utf8Reader, typeof(T), _options);
            }
            finally
            {
                bridge.Return();
            }
        }

        public override void Write(DataWriter writer, T? value)
        {
            if (value == null && !_converter.HandleNull)
            {
                writer.WriteNull();
                return;
            }

            var bridge = Bridge.Rent();
            try
            {
                _converter.Write(bridge.Writer, value!, _options);
                bridge.Writer.Flush();
                var utf8Reader = new Utf8JsonReader(bridge.Buffer.WrittenSpan);
                if (!utf8Reader.Read())
                {
                    throw new JsonException();
                }

                if (utf8Reader.TokenType != JsonTokenType.StartObject &&
                    utf8Reader.TokenType != JsonTokenType.StartArray)
                {
                    var end = utf8Reader;
                    if (end.Read())
                    {
                        throw new JsonException();
                    }
                    WriteScalar(writer, ref utf8Reader);
                    return;
                }

                using var document = JsonDocument.Parse(bridge.Buffer.WrittenMemory);
                s_elementConverter.Write(writer, document.RootElement);
            }
            finally
            {
                bridge.Return();
            }
        }

        private static void WriteScalar(DataWriter writer, ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    writer.WriteNull();
                    break;
                case JsonTokenType.True:
                case JsonTokenType.False:
                    writer.WriteBoolean(reader.GetBoolean());
                    break;
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out var integer))
                    {
                        writer.WriteInt64(integer);
                    }
                    else
                    {
                        writer.WriteBigNumber(reader.ValueSpan);
                    }
                    break;
                case JsonTokenType.String:
                    if (!reader.ValueIsEscaped)
                    {
                        writer.WriteString(reader.ValueSpan);
                        break;
                    }
                    byte[]? rented = null;
                    Span<byte> text = reader.ValueSpan.Length <= 256
                        ? stackalloc byte[256]
                        : (rented = ArrayPool<byte>.Shared.Rent(reader.ValueSpan.Length));
                    try
                    {
                        var length = reader.CopyString(text);
                        writer.WriteString(text.Slice(0, length));
                    }
                    finally
                    {
                        if (rented != null)
                        {
                            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                        }
                    }
                    break;
                default:
                    throw new JsonException();
            }
        }
    }
}
