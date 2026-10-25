using System.Text.Json;
using System.Text.Json.Serialization;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class SwarmContextTextSerializationTests
{
    [Fact]
    public void DefaultProfileIsOmittedWithoutChangingTheExplicitNativeOptionsJson()
    {
        foreach (var serializer in Serializers())
        {
            var ordinary = JsonSerializer.SerializeToElement(new SwarmOptions(), serializer);
            var explicitNative = JsonSerializer.SerializeToElement(new SwarmOptions
            {
                ContextTextProfile = SwarmContextTextProfile.NativeJson
            }, serializer);
            var name = serializer.PropertyNamingPolicy?.ConvertName(nameof(SwarmOptions.ContextTextProfile))
                ?? nameof(SwarmOptions.ContextTextProfile);

            Assert.False(ordinary.TryGetProperty(name, out _));
            Assert.False(explicitNative.TryGetProperty(name, out _));
            Assert.Equal(ordinary.GetRawText(), explicitNative.GetRawText());
            var restored = JsonSerializer.Deserialize<SwarmOptions>(ordinary.GetRawText(), serializer);
            Assert.NotNull(restored);
            Assert.Equal(SwarmContextTextProfile.NativeJson, restored.ContextTextProfile);
        }
    }

    [Fact]
    public void NondefaultPythonProfileIsSerializedAndRoundTripsThroughTheSelectedNamingPolicy()
    {
        foreach (var serializer in Serializers())
        {
            var json = JsonSerializer.SerializeToElement(new SwarmOptions
            {
                ContextTextProfile = SwarmContextTextProfile.PythonRepr
            }, serializer);
            var name = serializer.PropertyNamingPolicy?.ConvertName(nameof(SwarmOptions.ContextTextProfile))
                ?? nameof(SwarmOptions.ContextTextProfile);

            Assert.Equal((int)SwarmContextTextProfile.PythonRepr, json.GetProperty(name).GetInt32());
            var restored = JsonSerializer.Deserialize<SwarmOptions>(json.GetRawText(), serializer);
            Assert.NotNull(restored);
            Assert.Equal(SwarmContextTextProfile.PythonRepr, restored.ContextTextProfile);
        }
    }

    private static IEnumerable<JsonSerializerOptions> Serializers()
    {
        // Per-member omission must hold even when the caller retains other default values.
        yield return new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
        yield return new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    }
}
