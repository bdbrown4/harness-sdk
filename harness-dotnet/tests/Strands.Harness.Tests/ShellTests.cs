using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class ShellTests
{
    [Fact]
    public async Task FactoryShellKeepsItsOneMiBCaptureLimit()
    {
        var command = OperatingSystem.IsWindows() ? "[Console]::Write('x' * 1200000)" : "head -c 1200000 /dev/zero | tr '\\000' x";
        await using var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            Model = new Model(command),
            BuiltinTools = ["shell"],
            BuiltinPlugins = [],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Skills = new() { Enabled = false },
            Session = new() { Enabled = false },
        });
        await agent.InvokeAsync("run");
        var result = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        using var document = JsonDocument.Parse(result.Content);
        var output = document.RootElement.GetProperty("output").GetString()!;
        Assert.StartsWith(new string('x', 1024 * 1024), output, StringComparison.Ordinal);
        Assert.Contains("Shell output truncated", output, StringComparison.Ordinal);
        Assert.True(output.Length < 1024 * 1024 + 100);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task ShellBuiltinExecutesARealCommandAndReturnsItsCanonicalResult(int exitCode)
    {
        var model = new Model("echo shell-parity; exit " + exitCode);
        var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            Model = model,
            BuiltinTools = ["shell"],
            BuiltinPlugins = [],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });
        Assert.Equal("done", (await agent.InvokeAsync("run the shell")).ToString());
        var result = Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]);
        using var document = JsonDocument.Parse(result.Content);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("shell-parity", document.RootElement.GetProperty("output").GetString()!.Trim());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("error").GetString());
        Assert.Equal(exitCode, document.RootElement.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task PortableShellDescriptionAndExplicitSandboxBindingAreApplied()
    {
        var config = HarnessAgentConfig.Parse("{\"builtinTools\":{\"*\":false,\"shell\":{\"description\":\"Run approved operations\"}},\"caching\":false,\"contextManager\":false,\"skills\":false,\"memory\":false,\"session\":false,\"builtinPlugins\":[],\"sandbox\":{\"module\":\"remote\"}}");
        var sandbox = new CommandSandbox();
        var agent = await Harness.CreateAsync(config.ToOptions(new() { ModelResolver = _ => new Model(), SandboxResolver = _ => sandbox }) with { BackgroundTasks = null });
        Assert.Equal("Run approved operations", Assert.Single(agent.Tools).Spec.Description);
        await agent.InvokeAsync("run");
        Assert.Equal(1, sandbox.Calls);
        Assert.Equal(TimeSpan.FromSeconds(120), sandbox.Timeout);
        Assert.Equal("echo shell-parity", sandbox.Command);
    }

    [Fact]
    public async Task TextOnlySandboxCannotAcquireHostCommandAuthority()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            Model = new Model(),
            BuiltinTools = ["shell"],
            BuiltinPlugins = [],
            Sandbox = new TextOnlySandbox(),
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        }));
        Assert.Contains(nameof(ICommandSandbox), error.Message);
    }

    [Fact]
    public async Task TimeoutToolResultPreservesCanonicalPartialOutputAndCorrelation()
    {
        var sandbox = new CommandSandbox { TimedOut = true };
        var tool = Shell.Create(sandbox);
        var input = JsonSerializer.SerializeToElement(new { command = "wait", timeout = 0.5 });
        var parent = new Agent(new() { Model = new Model() });
        var result = await tool.InvokeResultAsync(input, new(parent, new("shell-call", "shell", input), new Dictionary<string, object?>()));

        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Equal("shell-call", result.ToolUseId);
        Assert.StartsWith("Execution timed out after 0.5 seconds\n", result.Content);
        Assert.Equal("partial", result.StructuredContent!.Value.GetProperty("output").GetString());
        Assert.Equal("warn", result.StructuredContent.Value.GetProperty("error").GetString());
        Assert.Equal(124, result.StructuredContent.Value.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task TruncatedOutputIsVisibleAndInvalidTimeoutDoesNotInvokeSandbox()
    {
        var sandbox = new CommandSandbox { Truncated = true };
        var tool = Shell.Create(sandbox);
        var parent = new Agent(new() { Model = new Model() });
        var input = JsonSerializer.SerializeToElement(new { command = "output", timeout = 0 });
        var result = await tool.InvokeResultAsync(input, new(parent, new("call", "shell", input), new Dictionary<string, object?>()));
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Equal(0, sandbox.Calls);
        input = JsonSerializer.SerializeToElement(new { command = "output" });
        result = await tool.InvokeResultAsync(input, new(parent, new("call", "shell", input), new Dictionary<string, object?>()));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("truncated", result.StructuredContent!.Value.GetProperty("output").GetString());
    }

    private sealed class Model(string command = "echo shell-parity") : IModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            if (Interlocked.Increment(ref _calls) == 1)
                yield return new ModelContentBlockStartEvent(0, new("shell", "shell", JsonSerializer.SerializeToElement(new { command })));
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            }
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(_calls == 1 ? StopReasons.ToolUse : StopReasons.EndTurn);
        }
    }

    private sealed class TextOnlySandbox : ISandbox
    {
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new IOException("No host filesystem access.");
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new IOException("No host filesystem access.");
    }
    private sealed class CommandSandbox : ICommandSandbox
    {
        public int Calls { get; private set; }
        public string? Command { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public bool TimedOut { get; init; }
        public bool Truncated { get; init; }
        public ValueTask<SandboxExecutionResult> ExecuteAsync(string command, ShellExecutionOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++; Command = command; Timeout = options!.Timeout;
            if (TimedOut) throw new SandboxTimeoutException(options.Timeout, new("partial", "warn", 124));
            return new(new SandboxExecutionResult("remote", string.Empty, 0, Truncated));
        }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new IOException();
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new IOException();
    }
}
