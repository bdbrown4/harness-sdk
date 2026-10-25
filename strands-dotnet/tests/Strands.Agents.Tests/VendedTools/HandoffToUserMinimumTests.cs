using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.VendedTools;

namespace Strands.Agents.Tests.VendedTools;

public sealed class HandoffToUserMinimumTests
{
    [Fact]
    public void InertFactoryHasDefaultSchemaAndCustomMetadata()
    {
        var tool = HandoffToUser.Create();
        Assert.Equal("handoff_to_user", tool.Spec.Name);
        Assert.Equal(HandoffToUser.DefaultDescription, tool.Spec.Description);
        var schema = tool.Spec.InputSchema;
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("message").GetProperty("type").GetString());
        Assert.Equal("message", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
        Assert.False(schema.GetProperty("properties").TryGetProperty("tool_context", out _));
        var renamed = HandoffToUser.Create(new() { Name = "ask_user", Description = "Selected question." });
        Assert.Equal("ask_user", renamed.Spec.Name);
        Assert.Equal("Selected question.", renamed.Spec.Description);
        Assert.False(tool is IDisposable);
        Assert.False(tool is IAsyncDisposable);
    }

    [Fact]
    public async Task RenamingRetainsFixedInterruptNameAndExactUntrimmedReason()
    {
        string? name = null;
        JsonElement? reason = null;
        var calls = 0;
        var context = Context((selected, value, response) =>
        {
            calls++;
            name = selected;
            reason = value?.Clone();
            Assert.Null(response);
            return Json("\"answer\"");
        }, "ask_user");
        var result = await HandoffToUser.Create(new() { Name = "ask_user" }).InvokeResultAsync(Input("  Question?\n"), context);
        Assert.Equal(1, calls);
        Assert.Equal("strands:handoff-to-user", name);
        Assert.Equal("  Question?\n", reason!.Value.GetString());
        Assert.Equal("handoff-use", result.ToolUseId);
        Assert.Equal("answer", Assert.IsType<TextBlock>(Assert.Single(result.ContentBlocks)).Text);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"message\":false}")]
    [InlineData("{\"message\":\"\"}")]
    [InlineData("{\"message\":\" \\t\\n\"}")]
    public async Task InvalidInputNeverCallsTheInterruptPrimitive(string raw)
    {
        var calls = 0;
        var context = Context((_, _, _) => { calls++; return Json("null"); });
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await HandoffToUser.Create().InvokeResultAsync(Json(raw), context));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RealAgentPauseAndResumeDoNotRepeatACompletedSibling()
    {
        var effects = 0;
        var model = new ScriptedModel(Call(("done-use", "done", "{}"), ("handoff-use", "ask_user", "{\"message\":\"Confirm?\"}")), Text("complete"));
        var sibling = new FunctionTool(new("done", "Complete one effect.", Json("{\"type\":\"object\"}")), (_, _, _) => { effects++; return new("committed"); });
        await using var agent = new Agent(new() { Model = model, ToolExecution = ToolExecutionStrategy.Sequential, Tools = [sibling, HandoffToUser.Create(new() { Name = "ask_user" })] });
        var paused = await agent.InvokeAsync("go");
        Assert.Equal(StopReasons.Interrupt, paused.StopReason);
        var question = Assert.Single(paused.Interrupts);
        Assert.Equal(HandoffToUser.InterruptName, question.Name);
        Assert.Equal("Confirm?", question.Reason!.Value.GetString());
        Assert.Equal("handoff-use", question.ToolUseId);
        Assert.Equal(1, effects);
        Assert.Equal(1, model.Calls);
        Assert.Equal("committed", Assert.Single(agent.InterruptState!.CompletedToolResults).Content);
        var resumed = await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, Json("\"yes\""))] });
        Assert.Equal(StopReasons.EndTurn, resumed.StopReason);
        Assert.Equal(1, effects);
        Assert.Equal(2, model.Calls);
        Assert.Null(agent.InterruptState);
        var results = agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().ToArray();
        Assert.Equal(new[] { "done-use", "handoff-use" }, results.Select(value => value.ToolUseId));
        Assert.Equal("yes", results[1].Content);
    }

    [Fact]
    public async Task ExplicitJsonNullIsAReplyAndDoesNotReopenTheQuestion()
    {
        var model = new ScriptedModel(Call(("handoff-use", "handoff_to_user", "{\"message\":\"Answer?\"}")), Text("complete"));
        await using var agent = new Agent(new() { Model = model, Tools = [HandoffToUser.Create()] });
        var paused = await agent.InvokeAsync("go");
        Assert.Equal(StopReasons.Interrupt, paused.StopReason);
        var question = Assert.Single(paused.Interrupts);
        var resumed = await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, Json("null"))] });
        Assert.Equal(StopReasons.EndTurn, resumed.StopReason);
        Assert.Empty(resumed.Interrupts);
        Assert.Equal("null", Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).Content);
        Assert.Equal(2, model.Calls);
    }

    [Theory]
    [InlineData(HandoffToUserBehavior.TypeScript, "{\"ok\":true}")]
    [InlineData(HandoffToUserBehavior.TypeScript, "[1,\"two\"]")]
    [InlineData(HandoffToUserBehavior.Python, "{\"ok\":true}")]
    [InlineData(HandoffToUserBehavior.Python, "[1,\"two\"]")]
    public async Task OrdinaryJsonReplyUsesTheSelectedSourceBlockLayout(HandoffToUserBehavior behavior, string raw)
    {
        var reply = Json(raw);
        var result = await HandoffToUser.Create(new() { Behavior = behavior }).InvokeResultAsync(Input("Question?"), Context((_, _, _) => reply));
        Assert.Equal("handoff-use", result.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        if (behavior == HandoffToUserBehavior.Python)
        {
            var text = Assert.IsType<TextBlock>(Assert.Single(result.ContentBlocks));
            Assert.True(JsonElement.DeepEquals(reply, Json(text.Text)));
        }
        else
        {
            var block = Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks));
            Assert.True(JsonElement.DeepEquals(reply, reply.ValueKind == JsonValueKind.Array ? block.Json.GetProperty("$value") : block.Json));
        }
    }

    [Theory]
    [InlineData(HandoffToUserBehavior.TypeScript)]
    [InlineData(HandoffToUserBehavior.Python)]
    public async Task PythonEnvelopeRetainsErrorStatusAndOverridesIdWhileTypeScriptKeepsOrdinaryData(HandoffToUserBehavior behavior)
    {
        var reply = Json("{\"status\":\"error\",\"content\":[{\"text\":\"No\"}],\"toolUseId\":\"spoof\"}");
        var result = await HandoffToUser.Create(new() { Behavior = behavior }).InvokeResultAsync(Input("Question?"), Context((_, _, _) => reply));
        Assert.Equal("handoff-use", result.ToolUseId);
        if (behavior == HandoffToUserBehavior.Python)
        {
            Assert.Equal(ToolResultStatus.Error, result.Status);
            Assert.Equal("No", Assert.IsType<TextBlock>(Assert.Single(result.ContentBlocks)).Text);
        }
        else
        {
            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.True(JsonElement.DeepEquals(reply, Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json));
        }
        Assert.Equal("spoof", reply.GetProperty("toolUseId").GetString());
    }

    private static JsonElement Json(string raw) { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
    private static JsonElement Input(string message) => JsonSerializer.SerializeToElement(new { message });
    private static ToolContext Context(Func<string, JsonElement?, JsonElement?, JsonElement> interrupt, string toolName = "handoff_to_user")
        => new(null!, new("handoff-use", toolName, Input("Question?")), new Dictionary<string, object?>()) { InterruptHandler = interrupt };
    private static ModelStreamEvent[] Call(params (string Id, string Name, string Input)[] calls)
    {
        var events = new List<ModelStreamEvent> { new ModelMessageStartEvent() };
        for (var index = 0; index < calls.Length; index++)
        {
            var call = calls[index];
            events.Add(new ModelContentBlockStartEvent(index, new(call.Id, call.Name, Json("{}"))));
            events.Add(new ModelContentBlockDeltaEvent(index, ToolInput: call.Input));
            events.Add(new ModelContentBlockStopEvent(index));
        }
        events.Add(new ModelMessageStopEvent(StopReasons.ToolUse));
        return events.ToArray();
    }
    private static ModelStreamEvent[] Text(string text) =>
    [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];
    private sealed class ScriptedModel(params ModelStreamEvent[][] turns) : IModel
    {
        internal int Calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var value in turns[Calls++])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
                await Task.Yield();
            }
        }
    }
}
