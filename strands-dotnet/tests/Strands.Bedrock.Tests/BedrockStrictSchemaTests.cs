using System.Text.Json;
using Amazon.BedrockRuntime.Model;
using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Bedrock.Tests;

public sealed class BedrockStrictSchemaTests
{
    [Fact]
    public async Task StrictToolsCloseNestedObjectsWithoutRequiringOptionalPropertiesOrMutatingTheirSource()
    {
        const string schema = """{"type":"object","properties":{"optional":{"type":"object","properties":{"items":{"type":"array","items":{"type":"object"}}}}}}""";
        using var document = JsonDocument.Parse(schema);
        var spec = new ToolSpec("lookup", "Lookup", document.RootElement);
        var native = await MapAsync(spec, true);
        var mapped = JsonSerializer.SerializeToElement(native.InputSchema.Json);
        Assert.True(native.Strict);
        Assert.False(mapped.GetProperty("additionalProperties").GetBoolean());
        Assert.False(mapped.GetProperty("properties").GetProperty("optional").GetProperty("additionalProperties").GetBoolean());
        Assert.False(mapped.GetProperty("properties").GetProperty("optional").GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("additionalProperties").GetBoolean());
        Assert.False(mapped.TryGetProperty("required", out _));
        Assert.Equal(schema, spec.InputSchema.GetRawText());
    }

    [Fact]
    public async Task LocalReferenceWithSiblingsIsInlinedAndSiblingValuesOverrideTheTarget()
    {
        var native = await MapAsync(Spec("""{"type":"object","$defs":{"item":{"type":"object","description":"target","additionalProperties":true}},"properties":{"item":{"$ref":"#/$defs/item","description":"sibling","additionalProperties":false}}}"""), true);
        var child = JsonSerializer.SerializeToElement(native.InputSchema.Json).GetProperty("properties").GetProperty("item");
        Assert.False(child.TryGetProperty("$ref", out _));
        Assert.Equal("sibling", child.GetProperty("description").GetString());
        Assert.False(child.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task ObjectUnionAndCompositionSchemasRetainTheirConstraintsWhileReceivingClosure()
    {
        var native = await MapAsync(Spec("""{"type":["object","null"],"allOf":[{"type":"object"}],"anyOf":[{"type":"object"}],"oneOf":[{"type":"object"}],"properties":{"n":{"type":"integer","minimum":1}}}"""), true);
        var mapped = JsonSerializer.SerializeToElement(native.InputSchema.Json);
        Assert.False(mapped.GetProperty("additionalProperties").GetBoolean());
        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" }) Assert.False(mapped.GetProperty(keyword)[0].GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(1, mapped.GetProperty("properties").GetProperty("n").GetProperty("minimum").GetInt32());
    }

    [Fact]
    public async Task DefaultModeLeavesSchemasAndTheNativeStrictFieldUnspecified()
    {
        var spec = Spec("""{"type":"object","properties":{"value":{"type":"object"}}}""");
        var native = await MapAsync(spec, false);
        Assert.Null(native.Strict);
        Assert.True(JsonElement.DeepEquals(spec.InputSchema, JsonSerializer.SerializeToElement(native.InputSchema.Json)));
    }

    [Fact]
    public async Task PureReferencesRemainReferencesAndTheirDefinitionsAreClosed()
    {
        var native = await MapAsync(Spec("""{"type":"object","$defs":{"item":{"type":"object"}},"properties":{"item":{"$ref":"#/$defs/item"}}}"""), true);
        var mapped = JsonSerializer.SerializeToElement(native.InputSchema.Json);
        Assert.False(mapped.GetProperty("$defs").GetProperty("item").GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("#/$defs/item", mapped.GetProperty("properties").GetProperty("item").GetProperty("$ref").GetString());
    }

    [Fact]
    public async Task ExplicitAdditionalPropertySchemasAreRetainedAndTheirObjectsAreClosed()
    {
        var native = await MapAsync(Spec("""{"type":"object","additionalProperties":{"type":"object","properties":{"value":{"type":"integer"}}}}"""), true);
        var mapped = JsonSerializer.SerializeToElement(native.InputSchema.Json);
        Assert.False(mapped.GetProperty("additionalProperties").GetProperty("additionalProperties").GetBoolean());
        Assert.True(mapped.GetProperty("additionalProperties").TryGetProperty("properties", out _));
    }

    [Fact]
    public async Task ReferencedExplicitAdditionalPropertiesWinWhenOnlyDescriptionIsOverridden()
    {
        var native = await MapAsync(Spec("""{"$defs":{"item":{"type":"object","additionalProperties":true}},"type":"object","properties":{"item":{"type":"object","$ref":"#/$defs/item","description":"own"}}}"""), true);
        var item = JsonSerializer.SerializeToElement(native.InputSchema.Json).GetProperty("properties").GetProperty("item");
        Assert.True(item.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("own", item.GetProperty("description").GetString());
    }

    [Fact]
    public async Task RecursiveReferencesTerminateAndExternalReferencesRemainVisible()
    {
        var native = await MapAsync(Spec("""{"$defs":{"node":{"type":"object","properties":{"next":{"$ref":"#/$defs/node","description":"next"}}}},"type":"object","properties":{"node":{"$ref":"#/$defs/node"},"external":{"$ref":"https://example.invalid/schema","description":"external"}}}"""), true);
        var mapped = JsonSerializer.SerializeToElement(native.InputSchema.Json);
        Assert.Equal("https://example.invalid/schema", mapped.GetProperty("properties").GetProperty("external").GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/node", mapped.GetProperty("properties").GetProperty("node").GetProperty("$ref").GetString());
    }

    [Fact]
    public async Task NativeJsonPointerEscapesAndOrdinaryPrototypeNamedPropertiesAreSafe()
    {
        var native = await MapAsync(Spec("""{"$defs":{"a/b~c":{"type":"object"}},"type":"object","properties":{"__proto__":{"$ref":"#/$defs/a~1b~0c","description":"own"}}}"""), true);
        var item = JsonSerializer.SerializeToElement(native.InputSchema.Json).GetProperty("properties").GetProperty("__proto__");
        Assert.False(item.TryGetProperty("$ref", out _));
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task LiteralDataAndLargeIntegersAreNotReinterpretedAsSchemas()
    {
        var native = await MapAsync(Spec("""{"type":"object","const":{"type":"object","number":9007199254740993},"examples":[{"type":"object"}],"properties":{"value":{"enum":[{"type":"object"}]}}}"""), true);
        var mapped = JsonSerializer.SerializeToElement(native.InputSchema.Json);
        Assert.Equal(9007199254740993L, mapped.GetProperty("const").GetProperty("number").GetInt64());
        Assert.False(mapped.GetProperty("const").TryGetProperty("additionalProperties", out _));
        Assert.False(mapped.GetProperty("examples")[0].TryGetProperty("additionalProperties", out _));
        Assert.False(mapped.GetProperty("properties").GetProperty("value").GetProperty("enum")[0].TryGetProperty("additionalProperties", out _));
    }

    [Fact]
    public void UnsupportedConstraintsAreRetainedAndReportedInDeterministicOrder()
    {
        var original = Spec("""{"type":"object","additionalProperties":true,"properties":{"value":{"type":"string","maxLength":5,"minLength":1},"number":{"type":"number","minimum":0}}}""").InputSchema;
        var strict = BedrockStrictSchema.Apply(original, out var warnings);
        Assert.Equal(new[] { "additionalProperties", "maxLength", "minLength", "minimum" }, warnings);
        Assert.Equal(5, strict.GetProperty("properties").GetProperty("value").GetProperty("maxLength").GetInt32());
        Assert.True(strict.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task DeepReferenceExpansionFailsBeforeAnyNativeRequest()
    {
        var definitions = new Dictionary<string, object>();
        for (var index = 0; index < 80; index++) definitions["n" + index] = index == 79 ? new { type = "object" } : new Dictionary<string, object> { ["$ref"] = "#/$defs/n" + (index + 1), ["description"] = "chain" };
        var schema = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["$defs"] = definitions, ["type"] = "object" });
        using var client = new BedrockModelTests.StubClient();
        using var model = new BedrockModel(client, new() { StrictTools = true });
        var error = await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")], Tools: [new("lookup", "Lookup", schema)])));
        Assert.Contains("limit", error.Message);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task ChainedReferencesRetainTerminalPropertiesAndSiblingPrecedence()
    {
        var native = await MapAsync(Spec("""{"$defs":{"alias":{"$ref":"#/$defs/item"},"item":{"type":"object","properties":{"value":{"type":"integer"}}}},"type":"object","properties":{"item":{"$ref":"#/$defs/alias","description":"own"}}}"""), true);
        var item = JsonSerializer.SerializeToElement(native.InputSchema.Json).GetProperty("properties").GetProperty("item");
        Assert.Equal("integer", item.GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
        Assert.Equal("own", item.GetProperty("description").GetString());
        Assert.False(item.TryGetProperty("$ref", out _));
    }

    [Fact]
    public async Task NativeConverseAndConfigurationUpdatesShareStrictToolBehavior()
    {
        using var client = new BedrockNonStreamingTests.Client();
        using var model = new BedrockModel(client, new() { Streaming = false });
        model.UpdateConfig(options => options with { StrictTools = true });
        Assert.True(model.GetConfig().StrictTools);
        await BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")], Tools: [Spec("""{"type":"object"}""")]));
        var spec = Assert.Single(Assert.Single(client.ConverseRequests).ToolConfig.Tools).ToolSpec;
        Assert.True(spec.Strict);
        Assert.False(JsonSerializer.SerializeToElement(spec.InputSchema.Json).GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task DuplicateSchemaPropertiesFailWithATypedErrorBeforeTransport()
    {
        using var client = new BedrockModelTests.StubClient();
        using var model = new BedrockModel(client, new() { StrictTools = true });
        var error = await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")], Tools: [Spec("""{"type":"object","type":"string"}""")])));
        Assert.NotNull(error.InnerException);
        Assert.Empty(client.Requests);
    }

    private static ToolSpec Spec(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return new("lookup", "Lookup", document.RootElement);
    }

    internal static async Task<ToolSpecification> MapAsync(ToolSpec spec, bool strict)
    {
        using var body = BedrockModelTests.Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new() { StrictTools = strict });
        await BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")], Tools: [spec]));
        return Assert.Single(Assert.Single(client.Requests).ToolConfig.Tools).ToolSpec;
    }
}
