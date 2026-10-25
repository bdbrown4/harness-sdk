using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class StructuredOutputTests
{
    private static StructuredOutputOptions<Answer> Options => new()
    {
        Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer", minimum = 1 } }, required = new[] { "value" }, additionalProperties = false })
    };

    [Fact]
    public async Task ValidatesThenDeserializesIntoTypedResult()
    {
        var tool = new StructuredOutputTool<Answer>(Options);
        var context = Context(tool);
        await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 5 }), context);
        Assert.True(tool.HasResult);
        Assert.Equal(5, tool.Output!.Value);
        Assert.Equal(1, tool.ValidationAttempts);
        Assert.Equal("strands_structured_output", tool.Spec.Name);
    }

    [Fact]
    public async Task InvalidSchemaResultRemainsUnavailableAndCanBeCorrected()
    {
        var tool = new StructuredOutputTool<Answer>(Options);
        await Assert.ThrowsAsync<StructuredOutputException>(async () => await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = -1 }), Context(tool)));
        Assert.False(tool.HasResult);
        await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 2 }), Context(tool));
        Assert.Equal(2, tool.Output!.Value);
        Assert.Equal(2, tool.ValidationAttempts);
    }

    [Fact]
    public async Task CustomDeserializerFailureProducesFeedbackWithoutClaimingSuccess()
    {
        var failure = new FormatException("business constraint");
        var tool = new StructuredOutputTool<Answer>(Options with { Deserializer = _ => throw failure });
        var error = await Assert.ThrowsAsync<StructuredOutputException>(async () => await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 1 }), Context(tool)));
        Assert.Same(failure, error.InnerException);
        Assert.Contains("business constraint", error.Message);
        Assert.False(tool.HasResult);
    }

    [Fact]
    public async Task CancellationDoesNotConsumeValidationAttempt()
    {
        var tool = new StructuredOutputTool<Answer>(Options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 1 }), Context(tool), cancellation.Token));
        Assert.Equal(0, tool.ValidationAttempts);
    }

    [Fact]
    public async Task AttemptBudgetIsBoundedAndResultsAreCorrelated()
    {
        var tool = new StructuredOutputTool<Answer>(Options with { MaxValidationAttempts = 1 });
        await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 3 }), Context(tool));
        Assert.True(tool.TryGetResult("result", out var output));
        Assert.Equal(3, output!.Value);
        Assert.False(tool.TryGetResult("unknown", out _));
        await Assert.ThrowsAsync<StructuredOutputException>(async () => await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 4 }), Context(tool)));
        Assert.Equal(1, tool.ValidationAttempts);
    }

    private static ToolContext Context(ITool tool)
    {
        var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(AgentTests.Text("done")) });
        return new(agent, new("result", tool.Spec.Name, JsonSerializer.SerializeToElement(new { })), new Dictionary<string, object?>());
    }

    public sealed record Answer(int Value);
}
