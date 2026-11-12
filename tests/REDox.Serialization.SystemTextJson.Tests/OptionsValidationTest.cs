using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace REDox.Serialization.SystemTextJson.Tests;

public class OptionsValidationTest
{
    [Theory]
    [InlineData(nameof(JsonSerializerOptions.RespectRequiredConstructorParameters))]
    [InlineData(nameof(JsonSerializerOptions.RespectNullableAnnotations))]
    [InlineData(nameof(JsonSerializerOptions.AllowDuplicateProperties))]
    [InlineData(nameof(JsonSerializerOptions.AllowOutOfOrderMetadataProperties))]
    public void UnsupportedValidationOptionsFailAtConstruction(string name)
    {
        var options = new JsonSerializerOptions();
        typeof(JsonSerializerOptions).GetProperty(name)!.SetValue(options,
            name != nameof(JsonSerializerOptions.AllowDuplicateProperties));

        Assert.Contains(name, Assert.Throws<NotSupportedException>(() =>
            new SystemTextJsonSerializerSettings(options)).Message);
        Assert.Throws<NotSupportedException>(() => SystemTextJsonSerializerSettings.ValidateOptions(options));
    }

    [Fact]
    public void ContractModifierIsRejectedBeforeItCanBeIgnored()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info => info.Properties.Clear());
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        Assert.Contains(nameof(options.TypeInfoResolver), Assert.Throws<NotSupportedException>(() =>
            new SystemTextJsonSerializerSettings(options)).Message);
    }

    [Fact]
    public void CustomResolverAndResolverChainsAreRejected()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new CustomResolver() };
        Assert.Throws<NotSupportedException>(() => new SystemTextJsonSerializerSettings(options));
        options = new JsonSerializerOptions();
        options.TypeInfoResolverChain.Add(new CustomResolver());
        Assert.Throws<NotSupportedException>(() => new SystemTextJsonSerializerSettings(options));
    }

    [Fact]
    public void DefaultAndUnmodifiedReflectionOptionsAreAccepted()
    {
        SystemTextJsonSerializerSettings.ValidateOptions(JsonSerializerOptions.Default);
        _ = new SystemTextJsonSerializerSettings();
        _ = new SystemTextJsonSerializerSettings(new JsonSerializerOptions());
        _ = new SystemTextJsonSerializerSettings(new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        });
    }

    [Fact]
    public void SettingsKeepValidatedOptionsStable()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        var settings = new SystemTextJsonSerializerSettings(options);
        options.RespectRequiredConstructorParameters = true;
        resolver.Modifiers.Add(info => info.Properties.Clear());
        Assert.Equal("{\"Name\":\"public\"}", Json.JsonSerializer.Serialize(new Payload(), settings));
    }

    private sealed class Payload
    {
        public string Name { get; set; } = "public";
    }

    private sealed class CustomResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => null;
    }
}
