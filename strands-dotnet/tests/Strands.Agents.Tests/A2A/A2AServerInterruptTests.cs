using System.Net.Http;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AServerInterruptTests
{
    [Fact]
    public async Task PausedRemoteCancellationSettlesTheNativeLedgerWithoutRepeatingAcceptedEffects()
    {
        var effects = 0; var approvals = 0; var model = new AbortInterruptTests.PausingModel();
        var agent = AbortInterruptTests.Create(model, () => effects++, () => approvals++);
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(agent) });
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        var paused = await client.CallAsync("message/send", A2AServerTests.Send("run"));
        var id = paused.GetProperty("id").GetString()!;
        Assert.Equal("input-required", paused.GetProperty("status").GetProperty("state").GetString());

        var canceled = await client.CancelTaskAsync(id);

        Assert.Equal("canceled", canceled.GetProperty("status").GetProperty("state").GetString());
        Assert.Null(agent.InterruptState); Assert.Equal(1, effects); Assert.Equal(0, approvals); Assert.Equal(1, model.Calls);
        var results = agent.Messages.Last().Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(ToolResultStatus.Success, results[0].Status); Assert.Equal(ToolResultStatus.Error, results[1].Status);
        Assert.Equal("canceled", (await client.GetTaskAsync(id)).GetProperty("status").GetProperty("state").GetString());
    }
    [Fact]
    public async Task StructuredRepliesResumeTheSameTaskAndRetainItsContextAndAcceptedSiblings()
    {
        var effects = 0; var approvals = 0;
        var agent = AbortInterruptTests.Create(new(), () => effects++, () => approvals++);
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(agent) });
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        var paused = await client.CallAsync("message/send", A2AServerTests.Send("run"));
        var id = paused.GetProperty("id").GetString()!; var context = paused.GetProperty("contextId").GetString()!;
        var question = paused.GetProperty("status").GetProperty("message").GetProperty("parts").EnumerateArray().Single(part => part.GetProperty("kind").GetString() == "data").GetProperty("data").GetProperty("interrupts")[0].GetProperty("interruptId").GetString();
        var parameters = JsonSerializer.SerializeToElement(new { message = new { kind = "message", messageId = "reply", role = "user", taskId = id, contextId = context, parts = new[] { new { kind = "data", data = new { interruptResponse = new { interruptId = question, response = true } } } } } });

        var completed = await client.CallAsync("message/send", parameters);

        Assert.Equal("completed", completed.GetProperty("status").GetProperty("state").GetString());
        Assert.Equal(id, completed.GetProperty("id").GetString()); Assert.Equal(context, completed.GetProperty("contextId").GetString());
        Assert.Equal(1, effects); Assert.Equal(1, approvals); Assert.Null(agent.InterruptState);
    }
}
