using System.Text.Json;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxShellTests
{
    [Fact]
    public async Task BoundSelectionReceivesExactCommandDefaultDeadlineAndCallerToken()
    {
        var selected = new ShellFakeSandbox((_, _) => ShellIterator.Terminal(new(23, "out\n", "warn\n")));
        var other = new ShellFakeSandbox((_, _) => throw new InvalidOperationException("The Agent environment must not run a bound tool."));
        await using var agent = ShellTestContext.Agent(other);
        using var caller = new CancellationTokenSource();
        var tool = Core.SandboxShell.Create(selected);
        var text = await ShellTestContext.Invoke(tool, agent, new { command = "literal command" }, caller.Token);
        using var result = JsonDocument.Parse(text);
        Assert.Equal("out\n", result.RootElement.GetProperty("output").GetString());
        Assert.Equal("warn\n", result.RootElement.GetProperty("error").GetString());
        Assert.Equal(23, result.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Equal("literal command", selected.Command);
        Assert.NotNull(selected.Options);
        Assert.Equal(TimeSpan.FromSeconds(120), selected.Options!.Timeout);
        Assert.Equal(caller.Token, selected.CallToken);
        Assert.Equal(caller.Token, selected.EnumerationToken);
        Assert.Equal(1, selected.Calls);
        Assert.Equal(0, other.Calls);
        Assert.Equal(0, selected.FileCalls);
    }

    [Fact]
    public async Task AgentRoutedFactoryResolvesEachInvocationAndTimeoutOverride()
    {
        var first = new ShellFakeSandbox((_, _) => ShellIterator.Terminal(new(0, "first", "")));
        var second = new ShellFakeSandbox((_, _) => ShellIterator.Terminal(new(0, "second", "")));
        await using var parent = ShellTestContext.Agent(first);
        await using var child = parent.CreateChild(options => options with { Sandbox = second });
        var tool = Core.SandboxShell.CreateForAgent(new() { Name = "chosen_shell", Description = "Chosen stateless shell" });
        using var caller = new CancellationTokenSource();
        using var firstResult = JsonDocument.Parse(await ShellTestContext.Invoke(tool, parent, new { command = "one", timeout = 0.25 }, caller.Token));
        using var secondResult = JsonDocument.Parse(await ShellTestContext.Invoke(tool, child, new { command = "two", timeout = 2.5 }, caller.Token));
        Assert.Equal("first", firstResult.RootElement.GetProperty("output").GetString());
        Assert.Equal("second", secondResult.RootElement.GetProperty("output").GetString());
        Assert.Equal(TimeSpan.FromMilliseconds(250), first.Options!.Timeout);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), second.Options!.Timeout);
        Assert.Equal(caller.Token, first.CallToken);
        Assert.Equal(caller.Token, second.CallToken);
        Assert.Equal("chosen_shell", tool.Spec.Name);
        Assert.Equal("Chosen stateless shell", tool.Spec.Description);
    }

    [Fact]
    public async Task TimeoutKeepsTheSameExceptionAndAppendsPartialResultJson()
    {
        var failure = new Core.SandboxTimeoutException(TimeSpan.FromSeconds(1), new(9, "partial\n", "warning"));
        var selected = new ShellFakeSandbox((_, _) => ShellIterator.Failure(failure));
        await using var agent = ShellTestContext.Agent(selected);
        var tool = Core.SandboxShell.Create(selected);
        var caught = await Record.ExceptionAsync(async () => await ShellTestContext.Invoke(tool, agent, new { command = "never executes" }));
        Assert.Same(failure, caught);
        var lines = failure.Message.Split('\n');
        using var partial = JsonDocument.Parse(lines[lines.Length - 1]);
        Assert.Equal("partial\n", partial.RootElement.GetProperty("output").GetString());
        Assert.Equal("warning", partial.RootElement.GetProperty("error").GetString());
        Assert.Equal(124, partial.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Equal(1, selected.LastIterator!.Disposals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerAndIndependentCancellationKeepExactExceptionTokenAndData(bool callerRequested)
    {
        using var caller = new CancellationTokenSource();
        using var independent = new CancellationTokenSource();
        independent.Cancel();
        var failure = new OperationCanceledException("Known backend cancellation", callerRequested ? caller.Token : independent.Token);
        var marker = new object();
        failure.Data["fixture"] = marker;
        var selected = new ShellFakeSandbox((_, _) => new(() =>
        {
            if (callerRequested) caller.Cancel();
            throw failure;
        }));
        await using var agent = ShellTestContext.Agent(selected);
        var caught = await Record.ExceptionAsync(async () => await ShellTestContext.Invoke(Core.SandboxShell.Create(selected), agent, new { command = "never executes" }, caller.Token));
        Assert.Same(failure, caught);
        Assert.Equal(callerRequested ? caller.Token : independent.Token, failure.CancellationToken);
        Assert.Same(marker, failure.Data["fixture"]);
        Assert.Equal(callerRequested, caller.IsCancellationRequested);
        Assert.Equal(1, selected.LastIterator!.Disposals);
    }

    [Fact]
    public async Task IndependentBackendFailureRetainsItsExactCause()
    {
        var failure = new IOException("Known selected backend failure");
        var selected = new ShellFakeSandbox((_, _) => ShellIterator.Failure(failure));
        await using var agent = ShellTestContext.Agent(selected);
        var caught = await Record.ExceptionAsync(async () => await ShellTestContext.Invoke(Core.SandboxShell.Create(selected), agent, new { command = "never executes" }));
        var wrapper = Assert.IsType<Core.SandboxShellException>(caught);
        Assert.Same(failure, wrapper.InnerException);
        Assert.Equal(failure.Message, wrapper.Message);
        Assert.Equal(1, selected.LastIterator!.Disposals);
    }

    [Fact]
    public async Task MetadataAndCustomSchemaAreDetachedWithoutCallingOrOwningTheBackend()
    {
        var selected = new ShellFakeSandbox((_, _) => throw new InvalidOperationException("Factory must perform no operation."));
        ITool tool;
        using (var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}},\"required\":[\"command\"]}"))
            tool = Core.SandboxShell.Create(selected, new() { Name = "custom", Description = "Detached schema", InputSchema = document.RootElement });
        Assert.Equal("object", tool.Spec.InputSchema.GetProperty("type").GetString());
        Assert.Equal("custom", tool.Spec.Name);
        Assert.Equal("Detached schema", tool.Spec.Description);
        await using (var agent = ShellTestContext.Agent(selected)) Assert.Same(selected, agent.Sandbox);
        Assert.Equal(0, selected.Calls);
        Assert.Equal(0, selected.FileCalls);
        Assert.Equal(0, selected.Disposals);
        Assert.Equal("shell", Core.SandboxShell.Create().Spec.Name);
    }
}

internal static class ShellTestContext
{
    internal static Agent Agent(Core.Sandbox selected) => new(new() { Model = new NoModel(), Sandbox = selected });
    internal static ValueTask<string> Invoke(ITool tool, Agent agent, object arguments, CancellationToken cancellationToken = default)
    {
        var input = JsonSerializer.SerializeToElement(arguments);
        return tool.InvokeAsync(input, new(agent, new("shell-call", tool.Spec.Name, input), new Dictionary<string, object?>()), cancellationToken);
    }
    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Fake shell tests never invoke a model or real process.");
    }
}

internal sealed class ShellFakeSandbox(Func<string, Core.ExecuteOptions?, ShellIterator> create) : Core.Sandbox, IDisposable
{
    internal int Calls, FileCalls, Disposals;
    internal string? Command;
    internal Core.ExecuteOptions? Options;
    internal CancellationToken CallToken, EnumerationToken;
    internal ShellIterator? LastIterator;
    public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        Command = command;
        Options = options;
        CallToken = cancellationToken;
        return new Enumerable(this, create(command, options));
    }
    private sealed class Enumerable(ShellFakeSandbox owner, ShellIterator iterator) : IAsyncEnumerable<Core.SandboxStreamEvent>
    {
        public IAsyncEnumerator<Core.SandboxStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            owner.EnumerationToken = cancellationToken;
            owner.LastIterator = iterator;
            return iterator;
        }
    }
    private Exception NoFile() { FileCalls++; return new InvalidOperationException("No file, command transport, interpreter or host fallback is allowed."); }
    public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw NoFile();
    public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw NoFile();
    public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw NoFile();
    public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw NoFile();
    public override ValueTask<IReadOnlyList<Strands.Agents.Files.FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw NoFile();
    public void Dispose() => Disposals++;
}

internal sealed class ShellIterator(Func<bool> move, Core.SandboxStreamEvent? current = null, Func<ValueTask>? dispose = null) : IAsyncEnumerator<Core.SandboxStreamEvent>
{
    internal int Disposals;
    public Core.SandboxStreamEvent Current => current ?? throw new InvalidOperationException("No current element.");
    public ValueTask<bool> MoveNextAsync() => new(move());
    public ValueTask DisposeAsync() { Disposals++; return dispose?.Invoke() ?? default; }
    internal static ShellIterator Failure(Exception failure) => new(() => throw failure);
    internal static ShellIterator Terminal(Core.ExecutionResult terminal)
    {
        var emitted = false;
        return new(() => { if (emitted) return false; emitted = true; return true; }, terminal);
    }
}
