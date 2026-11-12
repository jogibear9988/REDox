// SPDX-FileCopyrightText: 2026 CAPCOM CO., LTD.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using REDox.Serialization.Converters;
using REDox.Serialization.Metadata;

namespace REDox.Serialization.SystemTextJson;

public class SystemTextJsonSerializerSettings : SerializerSettings
{
    protected static Utf8Symbol DefaultTypeDiscriminatorPropertyName = Utf8Helper.TypeTag;

    private static readonly DataConverter[] DefaultConverters = new DataConverter[]
    {
        new SpanTypeConverter(),
        new BinaryConverter(),
        new EnumTypeConverter(),
        new JsonElementConverter(),
        new JsonNodeConverter(),
        new DoxTypeConverter(),
        new BuiltInTypeConverter(),
        new ArrayConverter(),
        new GenericCollectionConverter(),
        new CollectionConverter(),
        new ValueTypeConverter(),
        new ClassTypeConverter()
    };

    private static readonly TextEncoderPolicy DefaultEncoder = new(TextEscapeMask.Delete |
                                                                   TextEscapeMask.Ampersand |
                                                                   TextEscapeMask.SingleQuote |
                                                                   TextEscapeMask.Plus |
                                                                   TextEscapeMask.LessThan |
                                                                   TextEscapeMask.GreaterThan |
                                                                   TextEscapeMask.Backtick |
                                                                   TextEscapeMask.AllNonAscii)
    {
        UpperCaseHexEscapes = true,
        UnicodeEscapes = TextEscapeMask.DoubleQuote
    };

    private static readonly TextEncoderPolicy UnsafeEncoder = new(TextEscapeMask.NonBmp)
    {
        UpperCaseHexEscapes = true
    };

    private readonly JsonSerializerOptions _options;
    private readonly bool _useCustomReferenceResolver;

    public SystemTextJsonSerializerSettings(JsonSerializerOptions? options = null)
    {
        options = options ?? JsonSerializerOptions.Default;
        ValidateOptions(options);
        // Keep the validated contract stable even if the caller later changes its options.
        options = new JsonSerializerOptions(options);
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        options.MakeReadOnly(populateMissingResolver: true);
        _options = options;

        var nullValue = NullValueHandling.Include;
        var defaultValue = DefaultValueHandling.Include;

        NamingPolicy? namingPolicy = null;
        NamingPolicy? dictKeyPolicy = null;

        var preserveReferences = PreserveReferencesHandling.None;
        var referenceLoop = ReferenceLoopHandling.Serialize;

        var converterList = new List<DataConverter>();
        NumberHandling numberFormatHandling = default;

        UnknownTypeHandling = options.UnknownTypeHandling;

        switch (options.DefaultIgnoreCondition)
        {
            case JsonIgnoreCondition.WhenWritingNull:
                nullValue = NullValueHandling.IgnoreWrite;
                break;
            case JsonIgnoreCondition.WhenWritingDefault:
                defaultValue = DefaultValueHandling.Ignore;
                nullValue = NullValueHandling.IgnoreWrite;
                break;
        }

        switch (options.UnmappedMemberHandling)
        {
            case JsonUnmappedMemberHandling.Disallow:
                UnmappedMemberHandling = UnmappedMemberHandling.Disallow;
                break;
            case JsonUnmappedMemberHandling.Skip:
                UnmappedMemberHandling = UnmappedMemberHandling.Skip;
                break;
        }

        numberFormatHandling = GetNumberHandling(options.NumberHandling);

        namingPolicy = GetNamingPolicy(options.PropertyNamingPolicy);
        dictKeyPolicy = GetNamingPolicy(options.DictionaryKeyPolicy);

        if (options.ReferenceHandler == ReferenceHandler.Preserve)
        {
            preserveReferences = PreserveReferencesHandling.Objects | PreserveReferencesHandling.Collections;
        }


        if (options.ReferenceHandler == ReferenceHandler.IgnoreCycles)
        {
            referenceLoop = ReferenceLoopHandling.Null;
        }
        else if (options.ReferenceHandler != null && options.ReferenceHandler != ReferenceHandler.Preserve)
        {
            preserveReferences = PreserveReferencesHandling.Objects | PreserveReferencesHandling.Collections;
            _useCustomReferenceResolver = true;
        }

        foreach (var conv in options.Converters)
        {
            converterList.Add(new JsonConverterAdapter(conv, options));
        }

        IgnoreReadOnlyFields = options.IgnoreReadOnlyFields;
        IgnoreReadOnlyProperties = options.IgnoreReadOnlyProperties;
        IncludeFields = options.IncludeFields;
        PropertyNameCaseInsensitive = options.PropertyNameCaseInsensitive;

        if (options.Encoder == null || options.Encoder == JavaScriptEncoder.Default)
        {
            TextEncoderPolicy = DefaultEncoder;
        }
        else if (options.Encoder == JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
        {
            TextEncoderPolicy = UnsafeEncoder;
        }
        else
        {
            TextEncoderPolicy = new TextEncoderPolicy(GetEscapeUnicodeRanges(options.Encoder), TextEscapeMask.NonBmp)
            {
                UnicodeEscapes = TextEscapeMask.Slash | TextEscapeMask.DoubleQuote,
                UpperCaseHexEscapes = true
            };
        }

        if (options.PreferredObjectCreationHandling == JsonObjectCreationHandling.Populate)
        {
            ObjectCreationHandling = ObjectCreationHandling.ReuseObject | ObjectCreationHandling.ReuseStruct;
        }

        DefaultValueHandling = defaultValue;
        NullValueHandling = nullValue;
        UnknownObjectTypeHandling = UnknownObjectTypeHandling.Default;
        NumberHandling = numberFormatHandling;
        Converters = converterList.ToArray();
        PropertyNamingPolicy = namingPolicy;
        DictionaryKeyPolicy = dictKeyPolicy;
        PreserveReferencesHandling = preserveReferences;
        ReferenceLoopHandling = referenceLoop;
    }

    public JsonUnknownTypeHandling UnknownTypeHandling { get; init; }

    /// <summary>Checks options that the compatibility adapter cannot honor.</summary>
    /// <exception cref="NotSupportedException">An unsupported contract or validation option is configured.</exception>
    public static void ValidateOptions(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.RespectNullableAnnotations)
            throw UnsupportedOption(nameof(options.RespectNullableAnnotations));
        if (options.RespectRequiredConstructorParameters)
            throw UnsupportedOption(nameof(options.RespectRequiredConstructorParameters));
        if (!options.AllowDuplicateProperties)
            throw UnsupportedOption(nameof(options.AllowDuplicateProperties));
        if (options.AllowOutOfOrderMetadataProperties)
            throw UnsupportedOption(nameof(options.AllowOutOfOrderMetadataProperties));

        var resolver = options.TypeInfoResolver;
        if (resolver != null &&
            (resolver.GetType() != typeof(DefaultJsonTypeInfoResolver) ||
             ((DefaultJsonTypeInfoResolver)resolver).Modifiers.Count != 0))
        {
            throw UnsupportedOption(nameof(options.TypeInfoResolver));
        }
    }

    private static NotSupportedException UnsupportedOption(string name)
    {
        return new NotSupportedException(
            $"SystemTextJsonSerializerSettings does not support JsonSerializerOptions.{name}. " +
            "Use System.Text.Json directly for this setting.");
    }

    protected internal override ReferenceResolver CreateReferenceResolver()
    {
        if (_useCustomReferenceResolver)
        {
            return new SystemTextJsonReferenceResolverAdapter(_options.ReferenceHandler!.CreateResolver());
        }

        return base.CreateReferenceResolver();
    }

    protected override DataContract ResolveContract(Type type)
    {
        var dataProps = new List<DataProperty>();

        var depth = 0;

        var typeDiscriminatorPropertyName = DefaultTypeDiscriminatorPropertyName;
        object? typeDiscriminator = null;
        var numberHandling = NumberHandling;
        var alwaysWriteTypeDiscriminator = false;
        var unknownDerivedTypeHandling = UnknownDerivedTypeHandling.FailSerialization;
        var ignoreUnrecognizedTypeDiscriminators = false;
        var derivedTypes = new List<DataContract>();
        var isPolymorphic = false;
        var unmappedMemberHandling = UnmappedMemberHandling;
        DataProperty? extensionData = null;

        var declType = type;
        while (declType != null)
        {
            foreach (var attr in declType.GetCustomAttributes())
            {
                if (attr is JsonUnmappedMemberHandlingAttribute unmappedAttr)
                {
                    if (unmappedAttr.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow)
                    {
                        unmappedMemberHandling = UnmappedMemberHandling.Disallow;
                    }

                    if (unmappedAttr.UnmappedMemberHandling == JsonUnmappedMemberHandling.Skip)
                    {
                        unmappedMemberHandling = UnmappedMemberHandling.Skip;
                    }
                }

                if (attr is JsonPolymorphicAttribute polymorphicAttribute)
                {
                    if (polymorphicAttribute.TypeDiscriminatorPropertyName != null)
                    {
                        typeDiscriminatorPropertyName =
                            new Utf8Symbol(polymorphicAttribute.TypeDiscriminatorPropertyName);
                    }

                    ignoreUnrecognizedTypeDiscriminators = polymorphicAttribute.IgnoreUnrecognizedTypeDiscriminators;

                    switch (polymorphicAttribute.UnknownDerivedTypeHandling)
                    {
                        case JsonUnknownDerivedTypeHandling.FailSerialization:
                            unknownDerivedTypeHandling = UnknownDerivedTypeHandling.FailSerialization;
                            break;
                        case JsonUnknownDerivedTypeHandling.FallBackToBaseType:
                            unknownDerivedTypeHandling = UnknownDerivedTypeHandling.FallBackToBaseType;
                            break;
                        case JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor:
                            unknownDerivedTypeHandling = UnknownDerivedTypeHandling.FallBackToNearestAncestor;
                            break;
                    }
                }

                if (attr is JsonDerivedTypeAttribute derivedTypeAttribute)
                {
                    if (derivedTypeAttribute.DerivedType == type)
                    {
                        alwaysWriteTypeDiscriminator = declType == type;
                        isPolymorphic = true;
                        typeDiscriminator = derivedTypeAttribute.TypeDiscriminator;
                    }
                }
            }

            declType = declType.BaseType;
        }

        foreach (var attr in type.GetCustomAttributes())
        {
            if (attr is JsonNumberHandlingAttribute numberHandlingAttribute)
            {
                numberHandling = GetNumberHandling(numberHandlingAttribute.Handling);
            }

            if (attr is JsonDerivedTypeAttribute derivedTypeAttribute)
            {
                if (derivedTypeAttribute.DerivedType != type)
                {
                    derivedTypes.Add(GetContract(derivedTypeAttribute.DerivedType));
                }
            }
        }

        declType = type;
        while (declType != null)
        {
            foreach (var prop in declType.GetProperties(BindingFlags.Public | BindingFlags.Instance |
                                                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var getMethod = prop.GetGetMethod();
                if (getMethod != null)
                {
                    if (getMethod.GetParameters().Length != 0)
                    {
                        continue;
                    }

                    if (getMethod.GetBaseDefinition().DeclaringType != declType)
                    {
                        continue;
                    }
                }

                var dataProp = CreateDataProperty(prop, depth, numberHandling);
                if (dataProp != null)
                {
                    if (prop.IsDefined(typeof(JsonExtensionDataAttribute)))
                    {
                        extensionData ??= dataProp;
                        continue;
                    }

                    dataProps.Add(dataProp);
                }
            }

            if (IncludeFields)
            {
                foreach (var field in declType.GetFields(BindingFlags.Public | BindingFlags.Instance |
                                                         BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var dataProp = CreateDataProperty(field, depth, numberHandling);
                    if (dataProp != null)
                    {
                        if (field.IsDefined(typeof(JsonExtensionDataAttribute)))
                        {
                            extensionData ??= dataProp;
                            continue;
                        }

                        dataProps.Add(dataProp);
                    }
                }
            }

            declType = declType.BaseType;
            depth++;
        }

        if (derivedTypes.Count > 0)
        {
            isPolymorphic = true;
        }

        var dataContract = new DataContract(type, this)
        {
            Properties = dataProps.OrderBy(v => v, Comparer.Instance).ToList(),
            Constructor = GetConstructor(type),
            AlwaysWriteTypeDiscriminator = alwaysWriteTypeDiscriminator,
            UnknownDerivedTypeHandling = unknownDerivedTypeHandling,
            IgnoreUnrecognizedTypeDiscriminators = ignoreUnrecognizedTypeDiscriminators,
            TypeDiscriminatorPropertyName = typeDiscriminatorPropertyName,
            TypeDiscriminator = typeDiscriminator is string
                ? new TypeDiscriminator(new Utf8Symbol((string)typeDiscriminator))
                : new TypeDiscriminator(typeDiscriminator is int ? (int)typeDiscriminator : 0),
            DerivedTypes = derivedTypes,
            IsPolymorphic = isPolymorphic,
            UnmappedMemberHandling = unmappedMemberHandling,
            ExtensionData = extensionData
        };

        return dataContract;
    }

    protected override DataConverter ResolveConverter(Type type)
    {
        if (Converters != null)
        {
            foreach (var converter in Converters)
            {
                if (converter.CanConvert(type))
                {
                    return converter;
                }
            }
        }

        var typeConverterAttribute = type.GetCustomAttribute<JsonConverterAttribute>(false);
        if (typeConverterAttribute != null)
        {
            var attributeConverter = CreateConverterFromAttribute(typeConverterAttribute, type);
            if (attributeConverter != null)
            {
                return attributeConverter;
            }
        }

        if (type == typeof(DBNull))
        {
            return new ClassTypeConverter().CreateConverter(type, this);
        }

        if (type == typeof(object) && UnknownObjectTypeHandling == UnknownObjectTypeHandling.Default)
        {
            return new ObjectConverter(this);
        }

        if (type == typeof(IntPtr) || type == typeof(UIntPtr))
        {
            throw new NotSupportedException(type.FullName);
        }

        foreach (var converter in DefaultConverters)
        {
            if (converter.CanConvert(type))
            {
                return converter;
            }
        }

        throw new InvalidOperationException();
    }

    private static NamingPolicy? GetNamingPolicy(JsonNamingPolicy? policy)
    {
        if (policy == null)
        {
            return null;
        }

        if (policy == JsonNamingPolicy.CamelCase)
        {
            return NamingPolicy.CamelCase;
        }

        if (policy == JsonNamingPolicy.KebabCaseLower)
        {
            return NamingPolicy.KebabCaseLower;
        }

        if (policy == JsonNamingPolicy.KebabCaseUpper)
        {
            return NamingPolicy.KebabCaseUpper;
        }

        if (policy == JsonNamingPolicy.SnakeCaseLower)
        {
            return NamingPolicy.SnakeCaseLower;
        }

        if (policy == JsonNamingPolicy.SnakeCaseUpper)
        {
            return NamingPolicy.SnakeCaseUpper;
        }

        return new JsonNamingPolicyAdapter(policy);
    }

    private static TextEscapeRange[] GetEscapeUnicodeRanges(
        JavaScriptEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);

        var ranges = new List<TextEscapeRange>();
        var rangeStart = -1;

        for (var codePoint = 0; codePoint <= char.MaxValue; codePoint++)
        {
            var mustEncode =
                codePoint is >= 0xD800 and <= 0xDFFF ||
                encoder.WillEncode(codePoint);

            if (mustEncode)
            {
                if (rangeStart < 0)
                {
                    rangeStart = codePoint;
                }
            }
            else if (rangeStart >= 0)
            {
                ranges.Add(
                    new TextEscapeRange(
                        rangeStart,
                        codePoint - rangeStart));

                rangeStart = -1;
            }
        }

        if (rangeStart >= 0)
        {
            ranges.Add(
                new TextEscapeRange(
                    rangeStart,
                    0x10000 - rangeStart));
        }

        return ranges.ToArray();
    }

    private DataConverter? CreateConverterFromAttribute(JsonConverterAttribute attribute, Type type)
    {
        if (attribute.ConverterType == typeof(JsonStringEnumConverter))
        {
            return new StringEnumConverter();
        }

        var converter = attribute.CreateConverter(type);

        if (converter == null && attribute.ConverterType != null)
        {
            converter = (JsonConverter?)Activator.CreateInstance(attribute.ConverterType);
        }

        if (converter == null)
        {
            return null;
        }

        return new JsonConverterAdapter(converter, _options);
    }

    private NumberHandling GetNumberHandling(JsonNumberHandling handling)
    {
        NumberHandling numberFormatHandling = default;

        if (handling != 0)
        {
            if ((handling & JsonNumberHandling.AllowNamedFloatingPointLiterals) != 0)
            {
                numberFormatHandling |= NumberHandling.AllowNamedFloatingPointLiterals;
            }

            if ((handling & JsonNumberHandling.WriteAsString) != 0)
            {
                numberFormatHandling |= NumberHandling.WriteAsString;
            }

            if ((handling & JsonNumberHandling.AllowReadingFromString) != 0)
            {
                numberFormatHandling |= NumberHandling.AllowReadingFromString;
            }
        }

        return numberFormatHandling;
    }

    private ConstructorInfo? GetConstructor(Type typeInfo)
    {
        foreach (var ctor in typeInfo.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic |
                                                      BindingFlags.Instance))
        {
            if (ctor.IsDefined(typeof(JsonConstructorAttribute)))
            {
                return ctor;
            }
        }

        var constructors = typeInfo.GetConstructors();

        foreach (var ctor in constructors)
        {
            if (ctor.GetParameters().Length == 0)
            {
                return ctor;
            }
        }

        if (!typeInfo.IsValueType && constructors.Length == 1)
        {
            return constructors[0];
        }

        return null;
    }


    public DataProperty? CreateDataProperty(MemberInfo info, int depth, NumberHandling numberHandling)
    {
        var name = info.Name;

        if (PropertyNamingPolicy != null)
        {
            name = PropertyNamingPolicy.ConvertName(name);
        }

        var jsonProp = info.GetCustomAttribute<JsonPropertyNameAttribute>(true);
        if (jsonProp != null)
        {
            name = jsonProp.Name;
        }

        var objectCreationHandling = ObjectCreationHandling;

        var jsonObjectCreationAttr = info.GetCustomAttribute<JsonObjectCreationHandlingAttribute>();

        if (jsonObjectCreationAttr != null)
        {
            if (jsonObjectCreationAttr.Handling == JsonObjectCreationHandling.Populate)
            {
                objectCreationHandling = ObjectCreationHandling.ReuseObject | ObjectCreationHandling.ReuseStruct;
            }
            else
            {
                objectCreationHandling = ObjectCreationHandling.Replace;
            }
        }

        var orderAttr = info.GetCustomAttribute<JsonPropertyOrderAttribute>();
        var order = 0;
        if (orderAttr != null)
        {
            order = orderAttr.Order;
        }

        var numberAttr = info.GetCustomAttribute<JsonNumberHandlingAttribute>();
        if (numberAttr != null)
        {
            numberHandling = GetNumberHandling(numberAttr.Handling);
        }

        var nullValueHandling = NullValueHandling;
        var defaultValueHandling = DefaultValueHandling;

        var ignoreAttr = info.GetCustomAttribute<JsonIgnoreAttribute>();
        if (ignoreAttr != null)
        {
            nullValueHandling = NullValueHandling.Include;
            defaultValueHandling = DefaultValueHandling.Include;

            switch (ignoreAttr.Condition)
            {
                case JsonIgnoreCondition.WhenWritingDefault:
                    defaultValueHandling = DefaultValueHandling.Ignore;
                    break;
                case JsonIgnoreCondition.WhenWritingNull:
                    nullValueHandling = NullValueHandling.Ignore;
                    break;
                case JsonIgnoreCondition.Always:
                    return null;
            }
        }

        var prop = new DataProperty(info)
        {
            Name = name,
            ReferenceLoopHandling = ReferenceLoopHandling,
            NullValueHandling = nullValueHandling,
            NumberHandling = numberHandling,
            DefaultValueHandling = defaultValueHandling,
            ObjectCreationHandling = objectCreationHandling,
            IsRequired = info.IsDefined(typeof(JsonRequiredAttribute)) ||
                         info.IsDefined(typeof(RequiredMemberAttribute)),
            Order = order,
            Depth = depth
        };

        var jsonInclude = info.IsDefined(typeof(JsonIncludeAttribute));

        if (!prop.IsPublic && !jsonInclude)
        {
            return null;
        }

        {
            if (info is PropertyInfo propInfo && propInfo.SetMethod != null)
            {
                if ((propInfo.SetMethod.Attributes & MethodAttributes.MemberAccessMask) !=
                    MethodAttributes.Public)
                {
                    if (!info.IsDefined(typeof(JsonIncludeAttribute)))
                    {
                        prop = prop with { Writable = false };
                    }
                }
            }
        }

        if (prop.IsReadOnly && (prop.PropertyType == typeof(string) ||
                                !prop.PropertyType.IsAssignableTo(typeof(IEnumerable))))
        {
            if (IgnoreReadOnlyFields && info is FieldInfo)
            {
                return null;
            }

            if (IgnoreReadOnlyProperties && info is PropertyInfo propInfo)
            {
                if (propInfo.SetMethod == null)
                {
                    return null;
                }

                if (!propInfo.IsDefined(typeof(JsonIncludeAttribute)))
                {
                    return null;
                }
            }
        }

        var jsonConv = info.GetCustomAttribute<JsonConverterAttribute>(true);
        if (jsonConv != null)
        {
            var converter = CreateConverterFromAttribute(jsonConv, prop.PropertyType);
            if (converter is JsonConverterAdapter adapter)
            {
                prop = prop with { Converter = adapter.CreateConverter(prop.PropertyType, this) };
            }
            else if (converter != null)
            {
                prop = prop with { Converter = converter };
            }
        }

        return prop;
    }

    private class Comparer : IComparer<DataProperty>
    {
        public static readonly Comparer Instance = new();

        public int Compare(DataProperty? left, DataProperty? right)
        {
            if (left == right)
            {
                return 0;
            }

            if (left == null || right == null)
            {
                throw new ArgumentNullException();
            }

            if (left.Depth != right.Depth)
            {
                return left.Depth - right.Depth;
            }

            if (left.Order != right.Order)
            {
                return Math.Sign(left.Order - right.Order);
            }

            if (left.Name == right.Name)
            {
                throw new InvalidOperationException(left.Name);
            }

            return 0;
        }
    }
}
