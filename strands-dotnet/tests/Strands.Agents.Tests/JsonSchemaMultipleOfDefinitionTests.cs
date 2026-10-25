using System.Text.Json;
using Strands.Agents.SchemaImplementation;

namespace Strands.Agents.Tests;

public sealed class JsonSchemaMultipleOfDefinitionTests
{
    [Theory]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("-0", false)]
    [InlineData("1", true)]
    [InlineData("1e-4294967296", true)]
    public async Task SchemaCompilationRequiresAPositiveDivisorWithoutEvaluatingAnyInstance(string divisor, bool valid)
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"number\",\"multipleOf\":" + divisor + "}}}");
        var calls = 0;
        var tool = new FunctionTool(new("divisor_definition", "A compile-only schema capability.", document.RootElement), (_, _, _) => { calls++; return new("unused"); });
        var model = new UnusedModel();
        Agent? agent = null;
        var error = Record.Exception(() => { agent = new Agent(new() { Model = model, Tools = [tool] }); });
        if (agent is not null) await agent.DisposeAsync();

        if (valid) Assert.Null(error);
        else Assert.IsType<JsonSchemaException>(error);
        Assert.Equal(0, calls);
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData("1e4294967296", "2", true)]
    [InlineData("1e-4294967296", "1", false)]
    public async Task RegisteredDivisibilityDoesNotWrapAnExponentToZero(string number, string divisor, bool admitted)
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"number\",\"multipleOf\":" + divisor + "}},\"required\":[\"value\"],\"additionalProperties\":false}");
        var calls = 0;
        var tool = new FunctionTool(new("divisibility", "A selected nonprocess exact divisibility capability.", schema.RootElement), (_, _, _) => { calls++; return new("accepted"); });
        var model = new UnusedModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        using var input = JsonDocument.Parse("{\"value\":" + number + "}");
        var result = await agent.InvokeToolAsync(new("multiple", tool.Spec.Name, input.RootElement));

        Assert.Equal(admitted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        Assert.Equal(admitted ? 1 : 0, calls);
        Assert.Equal(0, model.Calls);
        Assert.Same(tool, Assert.Single(agent.Tools));
    }

    private sealed class UnusedModel : IModel
    {
        internal int Calls;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Schema definition tests never execute a model, command or interpreter.");
        }
    }
}
