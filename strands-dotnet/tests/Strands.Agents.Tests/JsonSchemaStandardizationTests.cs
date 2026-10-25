using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class JsonSchemaStandardizationTests
{
    public static IEnumerable<object[]> ExactIntegerCases()
    {
        yield return ["huge-integer", new string('9', 400), true];
        yield return ["huge-fraction", new string('9', 400) + ".5", false];
        yield return ["double-rounded-fraction", "9007199254740992.5", false];
        yield return ["tiny-fraction", "1e-400", false];
        yield return ["integral-decimal", "1.0", true];
        yield return ["int-limit-exponent", "1e2147483648", true];
    }

    [Theory]
    [MemberData(nameof(ExactIntegerCases))]
    public async Task RegisteredIntegerTypeMakesAnExactDecisionBeforeTheOriginalBody(string scenario, string number, bool admitted)
    {
        var (result, tool) = await Invoke(ValueSchema("{\"type\":\"integer\"}"), number);
        Assert.False(string.IsNullOrWhiteSpace(scenario));
        Assert.Equal(admitted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        Assert.Equal(admitted ? 1 : 0, tool.Calls);
        if (admitted) Assert.Equal(number, tool.Value);
        else Assert.Contains("does not match the schema", result.Content);
    }

    public static IEnumerable<object[]> DefaultDraftCases()
    {
        yield return ["prefix-tail", "{\"type\":\"array\",\"prefixItems\":[{\"type\":\"string\"}],\"items\":false}", "[\"ok\",2]", false];
        yield return ["contains-zero", "{\"type\":\"array\",\"contains\":{\"type\":\"integer\"},\"minContains\":0}", "[]", true];
        yield return ["contains-maximum", "{\"type\":\"array\",\"contains\":{\"type\":\"integer\"},\"maxContains\":1}", "[1,2]", false];
    }

    [Theory]
    [MemberData(nameof(DefaultDraftCases))]
    public async Task UndeclaredSchemasUseTheDocumented2020Draft(string scenario, string schema, string value, bool admitted)
    {
        var (result, tool) = await Invoke(ValueSchema(schema), value);
        Assert.False(string.IsNullOrWhiteSpace(scenario));
        Assert.Equal(admitted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        Assert.Equal(admitted ? 1 : 0, tool.Calls);
    }

    [Fact]
    public async Task UndeclaredLegacyTupleSchemaRequiresAnExplicitLegacyDraft()
    {
        var tool = new RecordingTool(ValueSchema("{\"type\":\"array\",\"items\":[{\"type\":\"string\"}]}"));
        var model = new UnusedModel();
        Agent? constructed = null;
        var error = Record.Exception(() => { constructed = new Agent(new() { Model = model, Tools = [tool] }); });
        if (constructed is not null) await constructed.DisposeAsync();
        Assert.NotNull(error);
        Assert.Equal(0, tool.Calls);
        Assert.Equal(0, model.Calls);
    }

    public static IEnumerable<object[]> DeclaredLegacyCases()
    {
        foreach (var draft in new[] { "http://json-schema.org/draft-06/schema#", "http://json-schema.org/draft-07/schema#" })
        {
            yield return [draft, "tuple", "{\"type\":\"array\",\"items\":[{\"type\":\"string\"},{\"type\":\"integer\"}],\"additionalItems\":false}", "[\"ok\",2]", true];
            yield return [draft, "dependencies", "{\"type\":\"object\",\"dependencies\":{\"a\":[\"b\"]}}", "{\"a\":true}", false];
        }
    }

    [Theory]
    [MemberData(nameof(DeclaredLegacyCases))]
    public async Task DeclaredDraftsKeepTheirOwnTupleAndDependencyContracts(string draft, string scenario, string schema, string value, bool admitted)
    {
        var (result, tool) = await Invoke(ValueSchema(schema, draft), value);
        Assert.False(string.IsNullOrWhiteSpace(scenario));
        Assert.Equal(admitted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        Assert.Equal(admitted ? 1 : 0, tool.Calls);
    }

    [Theory]
    [InlineData("http://json-schema.org/draft-06/schema#", "definitions")]
    [InlineData("http://json-schema.org/draft-07/schema#", "definitions")]
    [InlineData("https://json-schema.org/draft/2019-09/schema", "$defs")]
    [InlineData("https://json-schema.org/draft/2020-12/schema", "$defs")]
    public async Task DeclaredAndRelativeReferencedResourcesRetainExactIntegerTyping(string draft, string definitions)
    {
        var schema = "{\"$schema\":" + JsonSerializer.Serialize(draft) + ",\"$id\":\"https://schema.invalid/standardization/root\",\"type\":\"object\",\"" + definitions + "\":{\"entry\":{\"$schema\":" + JsonSerializer.Serialize(draft) + ",\"$id\":\"entry\",\"type\":\"integer\"}},\"properties\":{\"value\":{\"$ref\":\"entry\"}},\"required\":[\"value\"],\"additionalProperties\":false}";
        var number = new string('9', 400);
        var (result, tool) = await Invoke(schema, number);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal(1, tool.Calls);
        Assert.Equal(number, tool.Value);
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("\"3\"", false)]
    public async Task OrdinaryNullableAndNonnumericTypeDecisionsRemainIntact(string value, bool admitted)
    {
        var (result, tool) = await Invoke(ValueSchema("{\"type\":[\"integer\",\"null\"]}"), value);
        Assert.Equal(admitted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        Assert.Equal(admitted ? 1 : 0, tool.Calls);
    }

    [Fact]
    public async Task MalformedTypeDeclarationsNeverBecomeAnUnvalidatedTool()
    {
        var tool = new RecordingTool(ValueSchema("{\"type\":\"INTEGER\"}"));
        var model = new UnusedModel();
        Agent? constructed = null;
        var error = Record.Exception(() => { constructed = new Agent(new() { Model = model, Tools = [tool] }); });
        if (constructed is not null) await constructed.DisposeAsync();
        Assert.NotNull(error);
        Assert.Equal(0, tool.Calls);
        Assert.Equal(0, model.Calls);
    }

    private static string ValueSchema(string schema, string? draft = null)
        => "{" + (draft is null ? string.Empty : "\"$schema\":" + JsonSerializer.Serialize(draft) + ",") + "\"type\":\"object\",\"properties\":{\"value\":" + schema + "},\"required\":[\"value\"],\"additionalProperties\":false}";

    private static async Task<(ToolResultBlock Result, RecordingTool Tool)> Invoke(string schema, string value)
    {
        var tool = new RecordingTool(schema);
        var model = new UnusedModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        using var input = JsonDocument.Parse("{\"value\":" + value + "}");
        var original = input.RootElement.GetRawText();
        var result = await agent.InvokeToolAsync(new("schema-call", tool.Spec.Name, input.RootElement));
        Assert.Equal("schema-call", result.ToolUseId);
        Assert.Same(tool, Assert.Single(agent.Tools));
        Assert.Equal(original, input.RootElement.GetRawText());
        Assert.Equal(0, model.Calls);
        return (result, tool);
    }

    private sealed class RecordingTool : ITool
    {
        internal int Calls;
        internal string? Value;
        internal RecordingTool(string schema)
        {
            using var document = JsonDocument.Parse(schema);
            Spec = new("schema_probe", "A selected nonprocess tool recording schema admission.", document.RootElement);
        }
        public ToolSpec Spec { get; }
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Value = input.GetProperty("value").GetRawText();
            return new("admitted");
        }
    }

    private sealed class UnusedModel : IModel
    {
        internal int Calls;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Schema tests never execute a model, command or interpreter.");
        }
    }
}
