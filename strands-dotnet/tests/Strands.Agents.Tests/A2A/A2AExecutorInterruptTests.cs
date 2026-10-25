using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AExecutorInterruptTests
{
    [Fact]
    public async Task NativeInterruptsPublishInputRequiredWithOpaqueAddressableQuestions()
    {
        var effects = 0;
        var model = new Model();
        var agent = new Agent(new()
        {
            Model = model,
            Tools = [Tool("done", _ => { effects++; return "committed"; }), Tool("approve", context => context.Interrupt("approval", Json("{\"action\":\"write\"}")).ToString())]
        });
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(agent) });
        var sink = new A2AExecutorTests.Sink();

        await executor.ExecuteAsync(A2AExecutorTests.Context("task", "context", "Run"), sink);

        var last = sink.Events.Last();
        Assert.Equal("input-required", last.GetProperty("status").GetProperty("state").GetString());
        Assert.True(last.GetProperty("final").GetBoolean());
        var question = last.GetProperty("status").GetProperty("message").GetProperty("parts").EnumerateArray().Single(part => part.GetProperty("kind").GetString() == "data").GetProperty("data").GetProperty("interrupts")[0];
        Assert.Equal(Assert.Single(agent.InterruptState!.Interrupts).Id, question.GetProperty("interruptId").GetString());
        Assert.Equal("approval", question.GetProperty("name").GetString());
        Assert.Equal("write", question.GetProperty("reason").GetProperty("action").GetString());
        Assert.Equal(1, effects); Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task StructuredAnswersResumeTheOriginalTaskWithoutRepeatingCompletedSiblingEffects()
    {
        var effects = 0; var approvals = 0;
        var model = new Model();
        var agent = new Agent(new()
        {
            Model = model,
            Tools = [Tool("done", _ => { effects++; return "committed"; }), Tool("approve", context => { Assert.True(context.Interrupt("approval").GetBoolean()); approvals++; return "approved"; })]
        });
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(agent) });
        await executor.ExecuteAsync(A2AExecutorTests.Context("task", "context", "Run"), new A2AExecutorTests.Sink());
        var question = Assert.Single(agent.InterruptState!.Interrupts);
        var resume = new A2ARequestContext("task", "context", JsonSerializer.SerializeToElement(new { kind = "message", messageId = "answer", role = "user", taskId = "task", contextId = "context", parts = new[] { new { kind = "data", data = new { interruptResponse = new { interruptId = question.Id, response = true } } } } }));
        var sink = new A2AExecutorTests.Sink();

        await executor.ExecuteAsync(resume, sink);

        Assert.Equal("completed", sink.Events.Last().GetProperty("status").GetProperty("state").GetString());
        Assert.Equal(1, effects); Assert.Equal(1, approvals); Assert.Equal(2, model.Calls);
        Assert.Null(agent.InterruptState);
        Assert.Equal(new[] { "call-done", "call-approve" }, agent.Messages[2].Content.OfType<ToolResultBlock>().Select(result => result.ToolUseId));
    }
    [Theory]
    [InlineData("prompt")]
    [InlineData("wrong-task")]
    [InlineData("unknown-question")]
    [InlineData("duplicate")]
    [InlineData("mixed")]
    [InlineData("malformed-kind")]
    [InlineData("missing-response")]
    public async Task InvalidContinuationDoesNotPublishWorkingOrAlterThePendingBatch(string attempt)
    {
        var model = new Model(); var effects = 0;
        var agent = new Agent(new()
        {
            Model = model,
            Tools = [Tool("done", _ => { effects++; return "done"; }), Tool("approve", context => context.Interrupt("approval").ToString())]
        });
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(agent) });
        await executor.ExecuteAsync(A2AExecutorTests.Context("task", "context", "Run"), new A2AExecutorTests.Sink());
        var pending = JsonSerializer.Serialize(agent.InterruptState);
        var question = Assert.Single(agent.InterruptState!.Interrupts);
        var answer = JsonSerializer.SerializeToElement(new { kind = "data", data = new { interruptResponse = new { interruptId = attempt == "unknown-question" ? "unknown" : question.Id, response = true } } });
        var parts = attempt switch
        {
            "prompt" => new[] { Json("{\"kind\":\"text\",\"text\":\"new work\"}") },
            "duplicate" => new[] { answer, answer },
            "mixed" => new[] { answer, Json("{\"kind\":\"text\",\"text\":\"new work\"}") },
            "malformed-kind" => new[] { JsonSerializer.SerializeToElement(new { kind = 42, data = new { interruptResponse = new { interruptId = question.Id, response = true } } }) },
            "missing-response" => new[] { JsonSerializer.SerializeToElement(new { kind = "data", data = new { interruptResponse = new { interruptId = question.Id } } }) },
            _ => new[] { answer }
        };
        var task = attempt == "wrong-task" ? "different" : "task";
        var context = new A2ARequestContext(task, "context", JsonSerializer.SerializeToElement(new { kind = "message", messageId = "answer", role = "user", taskId = task, contextId = "context", parts }));
        var sink = new A2AExecutorTests.Sink();

        var error = await Assert.ThrowsAsync<A2ARpcException>(() => executor.ExecuteAsync(context, sink));

        Assert.Equal(-32602, error.Code); Assert.Empty(sink.Events);
        Assert.Equal(pending, JsonSerializer.Serialize(agent.InterruptState));
        Assert.Equal(1, effects); Assert.Equal(1, model.Calls);
    }
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static FunctionTool Tool(string name, Func<ToolContext, string> callback) => new(new(name, name, Json("{\"type\":\"object\"}")), (_, context, _) => new ValueTask<string>(callback(context)));
    private sealed class Model : IModel
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
            Calls++;
            yield return new ModelMessageStartEvent();
            if (Calls == 1)
            {
                yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("call-done", "done", Json("{}"))); yield return new ModelContentBlockStopEvent(0);
                yield return new ModelContentBlockStartEvent(1, new ToolUseBlock("call-approve", "approve", Json("{}"))); yield return new ModelContentBlockStopEvent(1);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "Approved"); yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
        }
    }
}
