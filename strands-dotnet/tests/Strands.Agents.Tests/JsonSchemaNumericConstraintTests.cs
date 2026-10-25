using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class JsonSchemaNumericConstraintTests
{
    public static IEnumerable<object[]> Cases()
    {
        var huge = new string('9', 400);
        var smaller = "8" + new string('9', 399);
        var nextMagnitude = "1" + new string('0', 400);
        yield return ["minimum400-equal", "\"minimum\":" + huge, huge, true];
        yield return ["minimum400-below", "\"minimum\":" + huge, smaller, false];
        yield return ["maximum400-equal", "\"maximum\":" + huge, huge, true];
        yield return ["maximum400-above", "\"maximum\":" + huge, nextMagnitude, false];
        yield return ["negative-minimum", "\"minimum\":-" + nextMagnitude, "-" + huge, true];
        yield return ["negative-maximum", "\"maximum\":-" + huge, "-" + smaller, false];
        yield return ["signed-int64-exclusive", "\"exclusiveMinimum\":9223372036854775807", "9223372036854775808", true];
        yield return ["fractional-bound-exact", "\"exclusiveMaximum\":9007199254740992.5", "9007199254740993", false];
        yield return ["large-multiple", "\"multipleOf\":1e399", "2e399", true];
        yield return ["large-odd", "\"multipleOf\":2", huge, false];
        yield return ["scientific-range", "\"minimum\":9e39,\"maximum\":1e41", "1.00e40", true];
        yield return ["negative-scientific-range", "\"minimum\":-3e40,\"maximum\":-1e40", "-2e40", true];
        yield return ["zero-with-fractional-multiple", "\"minimum\":0,\"maximum\":0,\"multipleOf\":1e-3", "0e400", true];
        yield return ["opposite-sign-maximum", "\"maximum\":-1", "0", false];
        yield return ["exponent-above-int32", "\"maximum\":1", "1e2147483648", false];
        yield return ["exponent-wraps-int32", "\"maximum\":1", "1e4294967296", false];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RegisteredIntegerTypingDoesNotBypassOtherExactNumericConstraints(string scenario, string constraints, string value, bool admitted)
    {
        var calls = 0;
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"," + constraints + "}},\"required\":[\"value\"],\"additionalProperties\":false}");
        var tool = new FunctionTool(new("numeric_constraints", "A selected nonprocess numeric constraint capability.", schema.RootElement), (_, _, _) => { calls++; return new("accepted"); });
        var model = new UnusedModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        using var input = JsonDocument.Parse("{\"value\":" + value + "}");

        var result = await agent.InvokeToolAsync(new("numeric-constraint", tool.Spec.Name, input.RootElement));

        Assert.False(string.IsNullOrWhiteSpace(scenario));
        Assert.Equal(admitted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        Assert.Equal(admitted ? 1 : 0, calls);
        Assert.Same(tool, Assert.Single(agent.Tools));
        Assert.Equal(0, model.Calls);
    }

    private sealed class UnusedModel : IModel
    {
        internal int Calls;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Numeric constraint tests never execute a model, command or interpreter.");
        }
    }
}
