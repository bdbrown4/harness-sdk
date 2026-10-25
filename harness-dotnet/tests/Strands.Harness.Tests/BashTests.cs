using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed partial class BashTests
{
    [Fact]
    public async Task VariablesPersistAcrossCommandsInOneAgent()
    {
        await using var tool = NewTool();
        await using var agent = NewAgent();
        await Execute(tool, agent, "STRANDS_PERSISTENT_VALUE='hello world'");
        Assert.Equal("hello world", await Execute(tool, agent, "printf '%s' \"$STRANDS_PERSISTENT_VALUE\""));
    }

    [Fact]
    public async Task WorkingDirectoryPersistsAcrossCommands()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-bash-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "child"));
        try
        {
            await using var tool = NewTool(directory);
            await using var agent = NewAgent();
            await Execute(tool, agent, "cd child");
            Assert.Equal("child", await Execute(tool, agent, "printf '%s' \"${PWD##*/}\""));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OneSharedToolKeepsAgentsIndependent()
    {
        await using var tool = NewTool();
        await using var first = NewAgent();
        await using var second = NewAgent();
        await Execute(tool, first, "STRANDS_AGENT_VALUE='first'");
        Assert.Equal("unset", await Execute(tool, second, "printf '%s' \"${STRANDS_AGENT_VALUE-unset}\""));
        Assert.Equal("first", await Execute(tool, first, "printf '%s' \"$STRANDS_AGENT_VALUE\""));
    }

    [Fact]
    public async Task RestartLosesInterpreterStateAndReturnsTheSharedMessage()
    {
        await using var tool = NewTool();
        await using var agent = NewAgent();
        await Execute(tool, agent, "STRANDS_RESTART_VALUE='before'");
        var restarted = await Invoke(tool, agent, new { mode = "restart" });
        Assert.Equal("Bash session restarted", restarted.Content);
        Assert.Equal("unset", await Execute(tool, agent, "printf '%s' \"${STRANDS_RESTART_VALUE-unset}\""));
    }

    [Fact]
    public async Task FirstTimeoutBecomesTheDefaultUntilRestartResetsIt()
    {
        await using var tool = NewTool();
        await using var agent = NewAgent();
        await Assert.ThrowsAsync<BashTimeoutException>(() => Execute(tool, agent, "sleep 0.4", timeout: 0.15));
        await Assert.ThrowsAsync<BashTimeoutException>(() => Execute(tool, agent, "sleep 0.4"));
        await Invoke(tool, agent, new { mode = "restart" });
        Assert.Equal("after", await Execute(tool, agent, "sleep 0.25; printf after"));
    }

    [Fact]
    public async Task StopBeforeStartupSettlesAndRejectsAnyLaterRun()
    {
        var generation = new BashSession.Generation(new()
        {
            ExecutablePath = Environment.GetEnvironmentVariable("STRANDS_BASH_TEST_EXECUTABLE"),
            WorkingDirectory = Environment.CurrentDirectory
        });
        var stopped = generation.StopAsync();
        try
        {
            Assert.Same(stopped, await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(2))));
            await stopped;
            await Assert.ThrowsAsync<BashSessionException>(() => generation.RunAsync(":", CancellationToken.None));
        }
        finally
        {
            // The defective implementation needs Start's finally to release its orphaned startup signal.
            // A canceled startup cannot create a child; always join the original physical close afterward.
            try
            {
                if (!stopped.IsCompleted) await generation.RunAsync(":", new CancellationToken(canceled: true));
            }
            catch (OperationCanceledException) { }
            catch (BashSessionException) { }
            finally { await stopped; }
        }
    }

    private static BashTool NewTool(string? directory = null)
    {
        var executable = Environment.GetEnvironmentVariable("STRANDS_BASH_TEST_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new InvalidOperationException("Real Bash tests require an explicitly selected interpreter on an owned verification runner.");
        return Bash.Create(new() { ExecutablePath = executable, WorkingDirectory = directory });
    }
    private static async Task<string> Execute(BashTool tool, Agent agent, string command, double? timeout = null)
    {
        var input = new Dictionary<string, object?> { ["mode"] = "execute", ["command"] = command };
        if (timeout is not null) input["timeout"] = timeout;
        var result = await Invoke(tool, agent, input);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        return structured.GetProperty("output").GetString()!;
    }
    private static async Task<ToolResultBlock> Invoke(BashTool tool, Agent agent, object input)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var json = JsonSerializer.SerializeToElement(input);
        return await tool.InvokeResultAsync(json, new(agent, new("bash-test", tool.Spec.Name, json), new Dictionary<string, object?>()), limit.Token);
    }
    private static Agent NewAgent() => new(new() { Model = new EmptyModel() });
    private sealed class EmptyModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
