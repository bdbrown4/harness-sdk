using System.Globalization;
using System.Text.Json;
using Strands.Agents.SchemaImplementation;

namespace Strands.Agents.Tests;

public sealed class JsonSchemaEqualityProfileTests
{
    public static IEnumerable<object[]> Cases()
    {
        var exponent = new string('9', 160);
        var predecessor = new string('9', 159) + "8";
        yield return ["unordered-object-equal", "\"const\":{\"a\":1,\"b\":true}", "{\"b\":true,\"a\":1.0}", true];
        yield return ["unordered-object-distinct", "\"const\":{\"a\":1,\"b\":true}", "{\"b\":true,\"a\":2}", false];
        yield return ["decoded-name", "\"const\":{\"\\u006e\":1}", "{\"n\":1.0}", true];
        yield return ["decoded-string", "\"const\":\"\\u0061\"", "\"a\"", true];
        yield return ["nested-array-equal", "\"const\":[{\"a\":[1,true,null]}]", "[{\"a\":[1.0,true,null]}]", true];
        yield return ["nested-array-order", "\"const\":[1,2]", "[2,1]", false];
        yield return ["nested-object-count", "\"const\":{\"a\":[1]}", "{\"a\":[1],\"b\":null}", false];
        yield return ["enum-value-kind", "\"enum\":[\"1\",true]", "1", false];
        yield return ["zero-spellings", "\"const\":-0.00e-" + exponent, "0e" + exponent, true];
        yield return ["huge-const-spellings", "\"const\":123e" + predecessor, "12.30e" + exponent, true];
        yield return ["huge-enum-spellings", "\"enum\":[123e" + predecessor + "]", "12.30e" + exponent, true];
        yield return ["huge-unique-spellings", "\"uniqueItems\":true", "[123e" + predecessor + ",12.30e" + exponent + "]", false];
        yield return ["zero-unique-spellings", "\"uniqueItems\":true", "[-0e-" + exponent + ",0e" + exponent + "]", false];
        yield return ["unique-nested-object-order", "\"uniqueItems\":true", "[{\"x\":[1],\"y\":true},{\"y\":true,\"x\":[1.0]}]", false];
        yield return ["unique-nested-array-order", "\"uniqueItems\":true", "[[1,2],[2,1]]", true];
        yield return ["duplicate-member-interleaving", "\"const\":{\"x\":1,\"x\":2,\"a\":true}", "{\"x\":1.0,\"a\":true,\"x\":2.0}", true];
        yield return ["duplicate-member-order", "\"const\":{\"x\":1,\"x\":2}", "{\"x\":2,\"x\":1}", false];
        yield return ["duplicate-member-retained", "\"const\":{\"x\":1,\"x\":2}", "{\"x\":2}", false];
        yield return ["unique-duplicate-members", "\"uniqueItems\":true", "[{\"x\":1,\"x\":2},{\"x\":1.0,\"x\":2.0}]", false];
        yield return ["unique-reversed-duplicate-members", "\"uniqueItems\":true", "[{\"x\":1,\"x\":2},{\"x\":2,\"x\":1}]", true];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RegisteredStructuralEqualityPreservesTheDeclaredNativeValueProfile(string scenario, string constraints, string value, bool admitted)
    {
        Assert.False(string.IsNullOrWhiteSpace(scenario));
        await AssertAdmission(constraints, value, admitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeDistinctArraysAndALateEquivalentNumberUseTheSameEqualityContract(bool appendEquivalent)
    {
        var values = Enumerable.Range(0, 2048).Select(number => number.ToString(CultureInfo.InvariantCulture) + "e4294967296").ToList();
        if (appendEquivalent) values.Add("2047.0e4294967296");
        await AssertAdmission("\"uniqueItems\":true", "[" + string.Join(",", values) + "]", !appendEquivalent);
    }

    [Theory]
    [InlineData("\"enum\":{}", false)]
    [InlineData("\"uniqueItems\":\"true\"", false)]
    [InlineData("\"const\":null", true)]
    public async Task EqualityKeywordsRetainTheirOriginalSchemaSyntaxValidation(string constraints, bool valid)
    {
        using var schema = JsonDocument.Parse(Schema(constraints));
        var tool = new FunctionTool(new("syntax", "A compile-only schema capability.", schema.RootElement), (_, _, _) => throw new InvalidOperationException("Compile-only controls never invoke a tool."));
        var model = new UnusedModel();
        Agent? agent = null;
        var error = Record.Exception(() => { agent = new Agent(new() { Model = model, Tools = [tool] }); });
        if (agent is not null) await agent.DisposeAsync();
        if (valid) Assert.Null(error);
        else Assert.IsType<JsonSchemaException>(error);
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData("http://json-schema.org/draft-06/schema#", "definitions")]
    [InlineData("http://json-schema.org/draft-07/schema#", "definitions")]
    [InlineData("https://json-schema.org/draft/2019-09/schema", "$defs")]
    [InlineData("https://json-schema.org/draft/2020-12/schema", "$defs")]
    public async Task DeclaredAndReferencedDraftsKeepExactBoundsAndEquality(string draft, string definitions)
    {
        using var schema = JsonDocument.Parse("{\"$schema\":" + JsonSerializer.Serialize(draft) + ",\"$id\":\"https://schema.invalid/exact/root\",\"type\":\"object\",\"" + definitions + "\":{\"entry\":{\"$schema\":" + JsonSerializer.Serialize(draft) + ",\"$id\":\"entry\",\"type\":\"number\",\"maximum\":1,\"const\":0}},\"properties\":{\"value\":{\"$ref\":\"entry\"}},\"required\":[\"value\"]}");
        var calls = 0;
        var tool = new FunctionTool(new("referenced", "A selected referenced numeric capability.", schema.RootElement), (_, _, _) => { calls++; return new("accepted"); });
        var model = new UnusedModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        foreach (var value in new[] { "0", "1e-400", "1e2147483648" })
        {
            using var input = JsonDocument.Parse("{\"value\":" + value + "}");
            var result = await agent.InvokeToolAsync(new("reference", tool.Spec.Name, input.RootElement));
            Assert.Equal(value == "0" ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        }
        Assert.Equal(1, calls);
        Assert.Equal(0, model.Calls);
    }

    private static string Schema(string constraints)
        => "{\"type\":\"object\",\"properties\":{\"value\":{" + constraints + "}},\"required\":[\"value\"],\"additionalProperties\":false}";

    private static async Task AssertAdmission(string constraints, string value, bool admitted)
    {
        using var schema = JsonDocument.Parse(Schema(constraints));
        var calls = 0;
        var tool = new FunctionTool(new("profile", "A selected structural equality capability.", schema.RootElement), (_, _, _) => { calls++; return new("accepted"); });
        var model = new UnusedModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        using var input = JsonDocument.Parse("{\"value\":" + value + "}");
        var original = input.RootElement.GetRawText();
        var result = await agent.InvokeToolAsync(new("profile", tool.Spec.Name, input.RootElement));
        Assert.Equal(admitted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        Assert.Equal(admitted ? 1 : 0, calls);
        Assert.Equal(original, input.RootElement.GetRawText());
        Assert.Same(tool, Assert.Single(agent.Tools));
        Assert.Equal(0, model.Calls);
    }

    private sealed class UnusedModel : IModel
    {
        internal int Calls;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Equality profile tests never execute a model, command or interpreter.");
        }
    }
}
