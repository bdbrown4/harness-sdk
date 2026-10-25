using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class JsonSchemaNumericEqualityTests
{
    public static IEnumerable<object[]> Cases()
    {
        var huge = new string('9', 400);
        var next = "1" + new string('0', 400);
        yield return ["const-tiny-is-not-zero", "\"const\":0", "1e-400", false];
        yield return ["enum-tiny-is-not-zero", "\"enum\":[0]", "1e-400", false];
        yield return ["const-large-equal", "\"const\":" + huge, huge, true];
        yield return ["enum-large-equal", "\"enum\":[" + huge + "]", huge, true];
        yield return ["unique-large-distinct", "\"uniqueItems\":true", "[" + huge + "," + next + "]", true];
        yield return ["unique-tiny-distinct", "\"uniqueItems\":true", "[0,1e-400]", true];
        yield return ["unique-object-numeric-equivalence", "\"uniqueItems\":true", "[{\"n\":1},{\"n\":1.0}]", false];
        yield return ["const-nested-tiny-is-not-zero", "\"const\":{\"n\":0}", "{\"n\":1e-400}", false];
        yield return ["ordinary-const-numeric-equivalence", "\"const\":1", "1.0", true];
        yield return ["ordinary-enum-value-kinds", "\"enum\":[true]", "1", false];
        yield return ["ordinary-unique-numeric-equivalence", "\"uniqueItems\":true", "[1,1.0]", false];
        yield return ["ordinary-unique-distinct", "\"uniqueItems\":true", "[1,2,true,null]", true];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RegisteredEqualityConstraintsPreserveMathematicalNumericValues(string scenario, string constraint, string value, bool admitted)
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"value\":{" + constraint + "}},\"required\":[\"value\"],\"additionalProperties\":false}");
        var calls = 0;
        var tool = new FunctionTool(new("equality", "A selected nonprocess equality constraint capability.", schema.RootElement), (_, _, _) => { calls++; return new("accepted"); });
        var model = new UnusedModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        using var input = JsonDocument.Parse("{\"value\":" + value + "}");
        var original = input.RootElement.GetRawText();
        var result = await agent.InvokeToolAsync(new("equality-call", tool.Spec.Name, input.RootElement));
        Assert.False(string.IsNullOrWhiteSpace(scenario));
        Assert.Equal(admitted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        Assert.Equal(admitted ? 1 : 0, calls);
        Assert.Same(tool, Assert.Single(agent.Tools));
        Assert.Equal(original, input.RootElement.GetRawText());
        Assert.Equal(0, model.Calls);
    }

    private sealed class UnusedModel : IModel
    {
        internal int Calls;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Equality constraint tests never execute a model, command or interpreter.");
        }
    }
}
