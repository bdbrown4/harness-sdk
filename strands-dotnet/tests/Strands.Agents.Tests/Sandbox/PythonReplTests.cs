using System.Text.Json;
using Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class PythonReplTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("{\"code\":null}")]
    [InlineData("{\"code\":7}")]
    public async Task InvalidCodeInputNeverEntersTheSelectedBackend(string raw)
    {
        var sandbox = new SourceExecutionBackend();
        using var input = JsonDocument.Parse(raw);
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await PythonRepl.Create(sandbox).InvokeAsync(input.RootElement, Context()));
        Assert.Equal(0, sandbox.CommandCalls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"7\"")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1e100")]
    public async Task InvalidTimeoutInputNeverEntersTheSelectedBackend(string timeout)
    {
        var sandbox = new SourceExecutionBackend();
        using var input = JsonDocument.Parse("{\"code\":\"source\",\"timeout\":" + timeout + "}");
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await PythonRepl.Create(sandbox).InvokeAsync(input.RootElement, Context()));
        Assert.Equal(0, sandbox.CommandCalls);
    }

    [Theory]
    [InlineData("name-empty")]
    [InlineData("name-invalid")]
    [InlineData("name-long")]
    [InlineData("description-empty")]
    [InlineData("language-invalid")]
    public void InvalidFactoryConfigurationCannotAcquireBackendAuthority(string condition)
    {
        var options = condition switch
        {
            "name-empty" => new PythonReplOptions { Name = "" },
            "name-invalid" => new PythonReplOptions { Name = "python.repl" },
            "name-long" => new PythonReplOptions { Name = new string('a', 65) },
            "description-empty" => new PythonReplOptions { Description = "" },
            _ => new PythonReplOptions { Language = "python3;echo" },
        };
        var sandbox = new SourceExecutionBackend();
        Assert.ThrowsAny<ArgumentException>(() => PythonRepl.Create(sandbox, options));
        Assert.Equal(0, sandbox.CommandCalls);
    }

    [Fact]
    public async Task BoundFactoryRequiresExplicitSandboxAndContextWithoutHostFallback()
    {
        Assert.Throws<ArgumentNullException>(() => PythonRepl.Create(null!));
        var sandbox = new SourceExecutionBackend();
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await PythonRepl.Create(sandbox).InvokeAsync(Input(), null!));
        Assert.Equal(0, sandbox.CommandCalls);
    }

    [Fact]
    public async Task CustomNameDescriptionInterpreterAndFractionalTimeoutRemainExplicit()
    {
        var sandbox = new SourceExecutionBackend();
        var tool = PythonRepl.Create(sandbox, new() { Name = "chosen_repl", Description = "Chosen environment", Language = "python3.12" });
        await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { code = "", timeout = 0.25 }), Context());
        Assert.Equal("chosen_repl", tool.Spec.Name);
        Assert.Equal("Chosen environment", tool.Spec.Description);
        Assert.Equal("python3.12", sandbox.Command);
        Assert.Empty(sandbox.Options!.StandardInput!);
        Assert.Equal(TimeSpan.FromMilliseconds(250), sandbox.Options.Timeout);
        var schema = tool.Spec.InputSchema;
        Assert.Equal(new[] { "code", "timeout" }, schema.GetProperty("properties").EnumerateObject().Select(value => value.Name).ToArray());
        Assert.Equal(new[] { "code" }, schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal("number", schema.GetProperty("properties").GetProperty("timeout").GetProperty("type").GetString());
        Assert.Equal(0, schema.GetProperty("properties").GetProperty("timeout").GetProperty("exclusiveMinimum").GetInt32());
    }

    [Fact]
    public async Task OriginalJsonDocumentMayCloseAfterTheEnteredInputIsCaptured()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new ControlledSourceIterator { MoveRelease = release.Task };
        var sandbox = new SourceExecutionBackend { CodeStream = stream };
        var input = JsonDocument.Parse("{\"code\":\"entered雪\",\"timeout\":7}");
        var operation = PythonRepl.Create(sandbox).InvokeAsync(input.RootElement, Context()).AsTask();
        try
        {
            Assert.Same(stream.MoveEntered.Task, await Task.WhenAny(stream.MoveEntered.Task, Task.Delay(TimeSpan.FromSeconds(8))));
            input.Dispose();
            release.TrySetResult(true);
            using var result = JsonDocument.Parse(await operation);
            Assert.Equal("terminal", result.RootElement.GetProperty("output").GetString());
            Assert.Equal("entered雪", sandbox.CodeSource);
        }
        finally { input.Dispose(); release.TrySetResult(true); await operation; }
    }

    [Fact]
    public async Task AlreadyCanceledInvocationRetainsExactTokenBeforeInvalidInputValidation()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var sandbox = new SourceExecutionBackend();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () => await PythonRepl.Create(sandbox).InvokeAsync(default, Context(), caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(0, sandbox.CommandCalls);
    }

    [Fact]
    public async Task TypedSandboxAbortRemainsUnwrappedWithItsPartialResultAndCleanup()
    {
        using var caller = new CancellationTokenSource();
        var partial = new ExecutionResult(-1, "partial", "stderr");
        var error = new SandboxAbortException(caller.Token, partial);
        var cleanup = new IOException("abort cleanup");
        error.Data["Strands.CleanupErrors"] = new[] { cleanup };
        var sandbox = new SourceExecutionBackend { Failure = error, BeforeFailure = () => caller.Cancel() };
        var observed = await Assert.ThrowsAsync<SandboxAbortException>(async () => await PythonRepl.Create(sandbox).InvokeAsync(Input(), Context(), caller.Token));
        Assert.Same(error, observed);
        Assert.Same(partial, observed.PartialResult);
        Assert.Equal(caller.Token, observed.CancellationToken);
        Assert.Same(cleanup, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(observed.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public void UnusedTimeoutConstructorAndOriginalMessageRemainUnchanged()
    {
        var cause = new IOException("old constructor cause");
        var partial = new ExecutionResult(124, "out", "err");
        var error = new SandboxTimeoutException(TimeSpan.FromSeconds(2), partial, cause);
        Assert.Equal("Sandbox execution exceeded its deadline.", error.Message);
        Assert.Equal(TimeSpan.FromSeconds(2), error.Timeout);
        Assert.Same(partial, error.PartialResult);
        Assert.Same(cause, error.InnerException);
    }

    [Fact]
    public async Task RepeatedTimeoutUseAppendsValidOutputWithoutChangingItsDataIdentity()
    {
        var error = new SandboxTimeoutException(TimeSpan.FromSeconds(1), new(124, "quoted\"\n雪", "err"));
        var data = error.Data;
        var sandbox = new SourceExecutionBackend { Failure = error };
        var tool = PythonRepl.Create(sandbox);
        for (var call = 0; call < 2; call++)
            Assert.Same(error, await Record.ExceptionAsync(async () => await tool.InvokeAsync(Input(), Context())));
        Assert.Same(data, error.Data);
        var lines = error.Message.Split('\n');
        Assert.Equal(3, lines.Length);
        foreach (var line in lines.Skip(1))
        {
            using var output = JsonDocument.Parse(line);
            Assert.Equal("quoted\"\n雪", output.RootElement.GetProperty("output").GetString());
            Assert.Equal(124, output.RootElement.GetProperty("exit_code").GetInt32());
        }
    }

    [Fact]
    public async Task GenericCauseRetainsItsIndependentIteratorCleanupDiagnostics()
    {
        var primary = new IOException("body read");
        var cleanup = new IOException("body release");
        var stream = new ControlledSourceIterator { MoveFailure = primary, DisposeFailure = cleanup };
        var sandbox = new SourceExecutionBackend { CodeStream = stream };
        var observed = await Assert.ThrowsAsync<PythonReplException>(async () => await PythonRepl.Create(sandbox).InvokeAsync(Input(), Context()));
        Assert.Same(primary, observed.InnerException);
        Assert.Same(cleanup, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(observed.InnerException!.Data["Strands.CleanupErrors"])));
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task AgentAndFactoryBorrowTheBoundSandboxAndDoNotInvokeTheModel()
    {
        var sandbox = new SourceExecutionBackend();
        var model = new NeverCalledModel();
        var tool = PythonRepl.Create(sandbox);
        var agent = new Agent(new() { Model = model, Tools = [tool] });
        try
        {
            var result = await agent.InvokeToolAsync(new("call", "python_repl", Input()));
            Assert.Equal(ToolResultStatus.Success, result.Status);
        }
        finally { await agent.DisposeAsync(); }
        Assert.Equal(0, model.Calls);
        Assert.Equal(0, sandbox.DisposeCalls);
        using var output = JsonDocument.Parse(await tool.InvokeAsync(Input(), Context()));
        Assert.Equal("out", output.RootElement.GetProperty("output").GetString());
        await sandbox.DisposeAsync();
        Assert.Equal(1, sandbox.DisposeCalls);
    }

    [Fact]
    public async Task NonzeroCodeCompletionIsOrdinaryJsonWithSharedDefaults()
    {
        var sandbox = new SourceExecutionBackend { Result = new(19, "output\n", "traceback\n") };
        var tool = PythonRepl.Create(sandbox);
        var output = await tool.InvokeAsync(Input(), Context());
        using var json = JsonDocument.Parse(output);
        Assert.Equal("output\n", json.RootElement.GetProperty("output").GetString());
        Assert.Equal("traceback\n", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(19, json.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        Assert.Equal("python3", sandbox.Command);
        Assert.Equal(TimeSpan.FromSeconds(120), sandbox.Options!.Timeout);
        Assert.Equal("python_repl", tool.Spec.Name);
    }

    [Fact]
    public async Task TimeoutRetainsItsOriginalObjectCauseDataStackAndAppendsPartialJson()
    {
        var cause = new IOException("selected timeout cause");
        var error = new SandboxTimeoutException(TimeSpan.FromSeconds(3), new(-1, "partial雪\n", "stderr\n"), cause);
        var cleanup = new IOException("independent cleanup");
        error.Data["Strands.CleanupErrors"] = new Exception[] { cleanup };
        var originalMessage = error.Message;
        var sandbox = new SourceExecutionBackend { Failure = error };
        var observed = await Assert.ThrowsAsync<SandboxTimeoutException>(async () => await PythonRepl.Create(sandbox).InvokeAsync(Input(), Context()));
        Assert.Same(error, observed);
        Assert.Same(cause, observed.InnerException);
        Assert.Same(cleanup, Assert.IsType<Exception[]>(observed.Data["Strands.CleanupErrors"])[0]);
        Assert.Contains(nameof(SourceExecutionBackend.ThrowFromSelectedBackend), observed.StackTrace, StringComparison.Ordinal);
        Assert.StartsWith(originalMessage + "\n", observed.Message, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(observed.Message.Substring(originalMessage.Length + 1));
        Assert.Equal("partial雪\n", json.RootElement.GetProperty("output").GetString());
        Assert.Equal("stderr\n", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(124, json.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Equal(-1, observed.PartialResult.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRetainsBackendIdentityTokenStackAndCleanup(bool callerRequested)
    {
        using var caller = new CancellationTokenSource();
        using var independent = new CancellationTokenSource();
        var token = callerRequested ? caller.Token : independent.Token;
        var error = new OperationCanceledException("selected cancellation", null, token);
        var cleanup = new IOException("must remain causal");
        error.Data["Strands.CleanupErrors"] = new Exception[] { cleanup };
        var sandbox = new SourceExecutionBackend { Failure = error, BeforeFailure = callerRequested ? () => caller.Cancel() : null };
        // The selected backend requests the caller only after admission, retaining its actual failure.
        var observed = await Assert.ThrowsAsync<OperationCanceledException>(async () => await PythonRepl.Create(sandbox).InvokeAsync(Input(), Context(), caller.Token));
        Assert.Same(error, observed);
        Assert.Equal(token, observed.CancellationToken);
        Assert.Contains(nameof(SourceExecutionBackend.ThrowFromSelectedBackend), observed.StackTrace, StringComparison.Ordinal);
        Assert.Same(cleanup, Assert.IsType<Exception[]>(observed.Data["Strands.CleanupErrors"])[0]);
    }

    [Fact]
    public async Task NonCancellationTransportFailureRetainsOriginalCause()
    {
        var error = new IOException("selected transport");
        var sandbox = new SourceExecutionBackend { Failure = error };
        var observed = await Assert.ThrowsAsync<PythonReplException>(async () => await PythonRepl.Create(sandbox).InvokeAsync(Input(), Context()));
        Assert.Same(error, observed.InnerException);
        Assert.Equal(error.Message, observed.Message);
    }

    private static JsonElement Input() => JsonSerializer.SerializeToElement(new { code = "print('test')" });
    private static ToolContext Context() => new(null!, new("call", "python_repl", JsonSerializer.SerializeToElement(new { })), new Dictionary<string, object?>());

    private sealed class NeverCalledModel : IModel
    {
        internal int Calls;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Direct Python execution must not invoke the model.");
        }
    }
}
