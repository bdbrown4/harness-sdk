using System.Diagnostics;
using System.Text.Json;

namespace Strands.Agents.Monty.Tests;

public sealed class ProgrammaticToolContractTests
{
    [Fact]
    public async Task GatherUsesAtMostTenHostCallsAndPreservesGuestResultOrder()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tenStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var maximum = 0; var started = 0;
        var work = Tool("work", async (input, _, token) =>
        {
            var current = Interlocked.Increment(ref active);
            lock (release) maximum = Math.Max(maximum, current);
            if (Interlocked.Increment(ref started) == 10) tenStarted.TrySetResult(true);
            try { await release.Task; token.ThrowIfCancellationRequested(); return input.GetProperty("value").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture); }
            finally { Interlocked.Decrement(ref active); }
        });
        var caller = ProgrammaticToolCaller.Create();
        await using var agent = Agent(work, caller);
        var call = Invoke(caller, agent, "import asyncio\nvalues = await asyncio.gather(*[work(value=i) for i in range(13)])\nprint(','.join(values))").AsTask();
        try
        {
            await Deadline(tenStarted.Task);
            Assert.Equal(10, Volatile.Read(ref started));
        }
        finally { release.TrySetResult(true); }
        var result = await Deadline(call);
        Assert.True(result.Status == ToolResultStatus.Success, result.Content);
        Assert.Equal("0,1,2,3,4,5,6,7,8,9,10,11,12", result.Content);
        Assert.Equal(10, maximum);
        Assert.Equal(13, started);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task HostFailuresAndPositionalArgumentsAreCatchableWithoutExecutingInvalidCalls()
    {
        var effects = 0;
        var work = Tool("work", (_, _, _) => { effects++; throw new IOException("the host failed"); });
        var caller = ProgrammaticToolCaller.Create();
        await using var agent = Agent(work, caller);
        var result = await Invoke(caller, agent, "try:\n await work(1)\nexcept TypeError:\n print('keyword-only')\ntry:\n await work(value=1)\nexcept RuntimeError as error:\n print(str(error))");
        Assert.True(result.Status == ToolResultStatus.Success, result.Content);
        Assert.Contains("keyword-only", result.Content);
        Assert.Contains("the host failed", result.Content);
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task AllowedToolsAreAnIntersectionAndExcludeEveryProgrammaticCaller()
    {
        var caller = ProgrammaticToolCaller.Create(new() { AllowedTools = new[] { "allowed", "missing", "other_caller" } });
        var other = ProgrammaticToolCaller.Create(new() { Name = "other_caller" });
        await using var agent = Agent(Tool("allowed", (_, _, _) => new("yes")), Tool("denied", (_, _, _) => throw new InvalidOperationException("No authority")), caller, other);
        var result = await Invoke(caller, agent, "print(await allowed())\ntry:\n await denied()\nexcept NameError:\n print('denied')\ntry:\n await other_caller()\nexcept NameError:\n print('other')\ntry:\n await missing()\nexcept NameError:\n print('missing')");
        Assert.True(result.Status == ToolResultStatus.Success, result.Content);
        Assert.Equal("yes\ndenied\nother\nmissing", result.Content);
    }

    [Fact]
    public async Task AliasesNeverShadowRealToolNamesOrResolveAmbiguousNames()
    {
        var caller = ProgrammaticToolCaller.Create();
        await using var agent = Agent(Tool("fetch-url", (_, _, _) => new("hyphen")), Tool("fetch_url", (_, _, _) => new("real")), Tool("a-b_c", (_, _, _) => new("first")), Tool("a_b-c", (_, _, _) => new("second")), caller);
        var result = await Invoke(caller, agent, "print(await fetch_url())\ntry:\n await a_b_c()\nexcept NameError:\n print('ambiguous')");
        Assert.True(result.Status == ToolResultStatus.Success, result.Content);
        Assert.Equal("real\nambiguous", result.Content);
    }

    [Fact]
    public async Task NonIdentifierToolNamesHaveWorkingAliases()
    {
        var caller = ProgrammaticToolCaller.Create();
        await using var agent = Agent(Tool("fetch-url", (_, _, _) => new("hyphen")), caller);
        Assert.Equal("hyphen", (await Invoke(caller, agent, "print(await fetch_url())")).Content);
    }

    [Fact]
    public async Task RefusedApprovalRemainsCatchableAndCannotBeSwallowedByTheHook()
    {
        var effects = 0;
        var caller = ProgrammaticToolCaller.Create();
        await using var agent = Agent(Tool("guarded", (_, _, _) => { effects++; return new("effect"); }), caller);
        using var hook = agent.Hooks.Register<BeforeToolCallEvent>((value, _) => { if (value.ToolUse.Name == "guarded") { try { value.Interrupt("permission"); } catch (InvalidOperationException) { } } return default; });
        var result = await Invoke(caller, agent, "try:\n await guarded()\nexcept RuntimeError as error:\n print(str(error))");
        Assert.True(result.Status == ToolResultStatus.Success, result.Content);
        Assert.Contains("cannot prompt", result.Content);
        Assert.Equal(0, effects);
        Assert.Null(agent.InterruptState);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task CancellationStopsTheRealWorkerAndCancelsAnAwaitedHostTool()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pid = 0;
        var caller = ProgrammaticToolCaller.Create(new() { ProcessStarted = id => pid = id });
        using var cancellation = new CancellationTokenSource();
        var tool = Tool("wait", async (_, _, token) => { entered.TrySetResult(true); try { await Task.Delay(Timeout.Infinite, token); } finally { cancelled.TrySetResult(token.IsCancellationRequested); } return "unreachable"; });
        await using var agent = Agent(tool, caller);
        var call = Invoke(caller, agent, "await wait()", cancellation.Token).AsTask();
        try
        {
            await Deadline(entered.Task);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Deadline(call));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.True(await Deadline(cancelled.Task));
            AssertExited(pid);
        }
        finally { cancellation.Cancel(); try { await Deadline(call); } catch (OperationCanceledException) { } }
        Assert.Empty(agent.Messages);
    }

    [Theory]
    [InlineData("time")]
    [InlineData("recursion")]
    [InlineData("suspensions")]
    public async Task GenuineVmEnforcesEachGuestBudget(string budget)
    {
        var caller = ProgrammaticToolCaller.Create(new() { MaxDuration = TimeSpan.FromMilliseconds(100), MaxRecursionDepth = 20, MaxSuspensions = 3 });
        await using var agent = Agent(Tool("ping", (_, _, _) => new("pong")), caller);
        var code = budget switch { "time" => "while True:\n pass", "recursion" => "def recurse():\n return recurse()\nrecurse()", _ => "for i in range(10):\n await ping()" };
        var result = await Deadline(Invoke(caller, agent, code).AsTask());
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Contains(budget == "time" ? "TimeoutError" : budget == "recursion" ? "RecursionError" : "suspensions", result.Content);
    }

    [Fact]
    public async Task OversizedUnicodePrintsAreCappedWithoutSplittingSurrogates()
    {
        var caller = ProgrammaticToolCaller.Create();
        await using var agent = Agent(caller);
        var result = await Invoke(caller, agent, "print('🙂' * 150000)");
        Assert.True(result.Status == ToolResultStatus.Success, result.Content);
        Assert.EndsWith("[output truncated at 200000 characters]", result.Content);
        var printed = result.Content.Substring(0, result.Content.IndexOf('\n'));
        Assert.Equal(200000, printed.Length);
        Assert.True(char.IsLowSurrogate(printed[printed.Length - 1]));
    }

    [Theory]
    [InlineData("structured", "7")]
    [InlineData("json", "7")]
    [InlineData("text-object", "7")]
    [InlineData("scalar", "string:7")]
    [InlineData("text-blocks", "first\nsecond")]
    public async Task RichResultsFollowTheSourceUnwrappingRules(string kind, string expected)
    {
        var caller = ProgrammaticToolCaller.Create();
        await using var agent = Agent(new Rich(kind), caller);
        var code = kind == "scalar" ? "value = await rich()\nprint('string:' + value)" : kind == "text-blocks" ? "print(await rich())" : "value = await rich()\nprint(value['answer'])";
        var result = await Invoke(caller, agent, code);
        Assert.True(result.Status == ToolResultStatus.Success, result.Content);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public async Task TheWallClockDeadlineIncludesSuspendedToolTimeAndPreservesPrints()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = ProgrammaticToolCaller.Create(new() { Timeout = TimeSpan.FromSeconds(10), MaxDuration = TimeSpan.FromMilliseconds(50) });
        await using var agent = Agent(Tool("wait", async (_, _, token) => { started.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); return "unreachable"; }), caller);
        var call = Invoke(caller, agent, "print('before waiting')\nawait wait()").AsTask();
        await Deadline(started.Task);
        var result = await Deadline(call);
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.StartsWith("before waiting", result.Content);
        Assert.Contains("timed out after 10 seconds", result.Content);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Error);
    }

    [Fact]
    public async Task UncooperativeHostCleanupIsObservableWithoutLeavingTheWorkerAlive()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pid = 0;
        var caller = ProgrammaticToolCaller.Create(new() { ProcessStarted = id => pid = id });
        await using var agent = Agent(Tool("wait", async (_, _, _) => { entered.TrySetResult(true); await release.Task; return "late completion"; }), caller);
        using var cancellation = new CancellationTokenSource();
        var call = Invoke(caller, agent, "await wait()", cancellation.Token).AsTask();
        try
        {
            await Deadline(entered.Task);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Deadline(call));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            var cleanup = Assert.IsAssignableFrom<Task<IReadOnlyList<Exception>>>(error.Data["Strands.CleanupTask"]);
            Assert.False(cleanup.IsCompleted);
            AssertExited(pid);
            release.TrySetResult(true);
            Assert.Empty(await Deadline(cleanup));
        }
        finally { release.TrySetResult(true); cancellation.Cancel(); try { await Deadline(call); } catch (OperationCanceledException) { } }
    }

    private sealed class Rich(string kind) : IRichTool
    {
        public ToolSpec Spec { get; } = new("rich", "A structured host result", JsonSerializer.SerializeToElement(new { type = "object" }));
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The rich path is required.");
        public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            var value = JsonSerializer.SerializeToElement(new { answer = 7 });
            ToolResultBlock result = kind switch
            {
                "structured" => new(context.ToolUse.ToolUseId, "display text") { StructuredContent = value },
                "json" => new(context.ToolUse.ToolUseId, new ContentBlock[] { new JsonBlock(value) }),
                "text-object" => new(context.ToolUse.ToolUseId, "{\"answer\":7}"),
                "scalar" => new(context.ToolUse.ToolUseId, "7"),
                _ => new(context.ToolUse.ToolUseId, new ContentBlock[] { new TextBlock("first"), new TextBlock("second") }),
            };
            return new ValueTask<ToolResultBlock>(result);
        }
    }

    private static FunctionTool Tool(string name, Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> run) => new(new(name, "Host test tool", JsonSerializer.SerializeToElement(new { type = "object" })), run);
    private static Agent Agent(params ITool[] tools) => new(new() { Model = new NoModel(), Tools = tools });
    private static ValueTask<ToolResultBlock> Invoke(ITool caller, Agent agent, string code, CancellationToken token = default)
    {
        var input = JsonSerializer.SerializeToElement(new { code });
        return ((IRichTool)caller).InvokeResultAsync(input, new(agent, new("programmatic-call", caller.Spec.Name, input), new Dictionary<string, object?>()), token);
    }
    private static async Task<T> Deadline<T>(Task<T> task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20))));
        return await task;
    }
    private static void AssertExited(int pid)
    {
        Assert.True(pid > 0);
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
    }
    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No model call is authorized.");
    }
}
