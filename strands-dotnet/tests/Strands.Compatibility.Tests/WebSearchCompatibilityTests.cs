using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Testing;
using Strands.Harness.Tools;

namespace Strands.Compatibility.Tests;

public sealed class WebSearchCompatibilityTests
{
    [Fact]
    public async Task RealMcpExaSearchFormatsSourcesAndKeepsBorrowedHttpAlive()
    {
        await using var server = new McpTestServer { ToolName = "web_search_exa", ToolResponseText = "Title: Example\nURL: https://example.test\nHighlights:\nCrème\t🙂" };
        using var http = new HttpClient();
        var tool = WebSearch.CreateExa(new() { Endpoint = server.Endpoint, ApiKey = "fixture-key", HttpClient = http });
        Assert.Equal("Sources:\n1. Example — https://example.test\n   Crème 🙂", await InvokeAsync(tool, new { query = "question", max_results = 100 }));
        Assert.Equal("fixture-key", server.LastCallApiKey);
        Assert.Equal(10, server.LastArguments!.Value.GetProperty("numResults").GetInt32());
        await WithinAsync(server.Deleted.Task);
        using var response = await http.GetAsync(server.Endpoint);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMcpExaCallsHonorCancellationAndTimeout(bool timeout)
    {
        var server = new McpTestServer { ToolName = "web_search_exa", ToolResponseText = "", ToolCallGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var tool = WebSearch.CreateExa(new() { Endpoint = server.Endpoint, ApiKey = "fixture-key", Timeout = timeout ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30) });
        await WithOwnedInvocationAsync(token => InvokeAsync(tool, new { query = "question" }, token),
            server.ToolCallStarted.Task, () => server.ToolCallGate!.TrySetResult(true), async (operation, cancellation) =>
            {
                Exception? expectedCancellation = null;
                if (timeout)
                {
                    await WithinAsync(operation);
                    Assert.Equal("web_search failed: Exa search timed out.", await operation);
                }
                else
                {
                    cancellation.Cancel();
                    Assert.Same(operation, await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(5))));
                    expectedCancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
                    Assert.Equal(cancellation.Token, ((OperationCanceledException)expectedCancellation).CancellationToken);
                }
                await WithinAsync(server.Deleted.Task);
                return expectedCancellation;
            }, cleanup: () => server.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task RealMcpExaToolFailureAddsAnActionableKeylessHint()
    {
        await using var server = new McpTestServer { ToolName = "web_search_exa", ToolResponseText = "quota", ToolIsError = true };
        var tool = WebSearch.CreateExa(new() { Endpoint = server.Endpoint, ApiKey = "" });
        Assert.Equal("web_search failed: quota (Exa's keyless tier is rate limited; set EXA_API_KEY to lift it.)", await InvokeAsync(tool, new { query = "question", max_results = -1 }));
        Assert.Null(server.LastCallApiKey);
        Assert.Equal(1, server.LastArguments!.Value.GetProperty("numResults").GetInt32());
        await WithinAsync(server.Deleted.Task);
    }

    [Fact]
    public async Task AServerWithoutTheExaToolFailsWithoutCallingAnotherTool()
    {
        await using var server = new McpTestServer();
        var tool = WebSearch.CreateExa(new() { Endpoint = server.Endpoint, ApiKey = "fixture-key" });
        Assert.Equal("web_search failed: Exa's MCP server does not offer web_search_exa", await InvokeAsync(tool, new { query = "question" }));
        Assert.Equal(0, server.Calls);
        await WithinAsync(server.Deleted.Task);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadinessDiagnosesTheActualOperationBeforeAnAlreadyElapsedWatchdog(bool faulted)
    {
        var cause = new IOException("early fixture transport failure");
        var operation = faulted ? Task.FromException<string>(cause) : Task.FromResult("web_search failed: early fixture initialization result");
        try
        {
            var error = await Record.ExceptionAsync(() => WithOwnedInvocationAsync(_ => operation,
                NewSignal().Task, () => { }, (_, _) => Task.FromResult<Exception?>(null), Task.CompletedTask));

            if (faulted) Assert.Same(cause, error);
            else Assert.Contains("web_search failed: early fixture initialization result", Assert.IsType<InvalidOperationException>(error).Message);
        }
        finally { await Record.ExceptionAsync(() => operation); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAssertionFailureCancelsReleasesAndJoinsItsOwnedOperation(bool cleanupFails)
    {
        var primary = new IOException("primary fixture assertion");
        var secondary = new IOException("physical operation cleanup failure");
        var cancelled = NewSignal(); var release = NewSignal(); var releaseEntered = NewSignal();
        var finish = NewSignal(); var escape = NewSignal(); var joined = NewSignal();
        Task<string>? operation = null;
        async Task<string> Start(CancellationToken token)
        {
            using var registration = token.Register(() => cancelled.TrySetResult(true));
            await Task.WhenAny(cancelled.Task, escape.Task);
            await release.Task;
            await finish.Task;
            joined.TrySetResult(true);
            if (cleanupFails) throw secondary;
            return "physically joined";
        }
        try
        {
            var run = Record.ExceptionAsync(() => WithOwnedInvocationAsync(token => operation = Start(token), Task.CompletedTask,
                () => { releaseEntered.TrySetResult(true); release.TrySetResult(true); }, (_, _) => Task.FromException<Exception?>(primary)));
            await WithinAsync(Task.WhenAny(run, releaseEntered.Task));
            Assert.True(releaseEntered.Task.IsCompleted);
            Assert.False(run.IsCompleted, "The fixture returned while the physical invocation was still gated.");
            finish.TrySetResult(true);
            var failure = await run;

            Assert.Same(primary, failure);
            Assert.True(cancelled.Task.IsCompleted, "Owned invocation cancellation was never requested.");
            Assert.True(joined.Task.IsCompleted, "The fixture returned before its owned invocation physically joined.");
            if (cleanupFails) Assert.Same(secondary, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
        }
        finally
        {
            escape.TrySetResult(true); release.TrySetResult(true); finish.TrySetResult(true);
            if (operation is not null) await Record.ExceptionAsync(() => operation);
        }
    }

    [Fact]
    public async Task AReadinessWatchdogFailureStillCancelsAndPhysicallyJoinsTheInvocation()
    {
        var cancelled = NewSignal(); var release = NewSignal(); var releaseEntered = NewSignal();
        var finish = NewSignal(); var escape = NewSignal(); var joined = NewSignal();
        Task<string>? operation = null;
        async Task<string> Start(CancellationToken token)
        {
            using var registration = token.Register(() => cancelled.TrySetResult(true));
            await Task.WhenAny(cancelled.Task, escape.Task);
            await release.Task;
            await finish.Task;
            joined.TrySetResult(true);
            token.ThrowIfCancellationRequested();
            return "released for failed-test cleanup";
        }
        try
        {
            var run = Record.ExceptionAsync(() => WithOwnedInvocationAsync(token => operation = Start(token), NewSignal().Task,
                () => { releaseEntered.TrySetResult(true); release.TrySetResult(true); }, (_, _) => Task.FromResult<Exception?>(null), Task.CompletedTask));
            await WithinAsync(Task.WhenAny(run, releaseEntered.Task));
            Assert.True(releaseEntered.Task.IsCompleted);
            Assert.False(run.IsCompleted, "The watchdog returned while the physical invocation was still gated.");
            finish.TrySetResult(true);
            var failure = await run;

            Assert.Contains("tools/call readiness", Assert.IsType<TimeoutException>(failure).Message);
            Assert.True(cancelled.Task.IsCompleted);
            Assert.True(joined.Task.IsCompleted);
        }
        finally
        {
            escape.TrySetResult(true); release.TrySetResult(true); finish.TrySetResult(true);
            if (operation is not null) await Record.ExceptionAsync(() => operation);
        }
    }

    [Fact]
    public async Task DeliveredReadinessWinsWhenTheOperationHasAlreadyCompleted()
    {
        var asserted = false;
        await WithOwnedInvocationAsync(_ => Task.FromResult("already completed"), Task.CompletedTask, () => { }, async (operation, _) =>
        {
            Assert.Equal("already completed", await operation);
            asserted = true;
            return null;
        }, Task.CompletedTask);
        Assert.True(asserted);
    }

    [Fact]
    public async Task CleanupFailuresRetainThePrimaryAndStillRunEveryCleanupStep()
    {
        var primary = new IOException("primary fixture assertion");
        var releaseFailure = new IOException("release failure");
        var cleanupFailure = new IOException("server cleanup failure");
        var cleaned = false;
        var failure = await Record.ExceptionAsync(() => WithOwnedInvocationAsync(_ => Task.FromResult("complete"), Task.CompletedTask,
            () => throw releaseFailure, (_, _) => Task.FromException<Exception?>(primary), cleanup: () =>
            {
                cleaned = true;
                return Task.FromException(cleanupFailure);
            }));

        Assert.Same(primary, failure);
        Assert.True(cleaned);
        Assert.Equal(new Exception[] { releaseFailure, cleanupFailure }, Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"]));
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // This fixture owns both the invocation and its gated server until they physically
    // finish, including failures before the real tools/call request reaches the server.
    private static async Task WithOwnedInvocationAsync(Func<CancellationToken, Task<string>> start, Task ready, Action release,
        Func<Task<string>, CancellationTokenSource, Task<Exception?>> assertions, Task? readinessWatchdog = null, Func<Task>? cleanup = null)
    {
        using var cancellation = new CancellationTokenSource();
        Task<string>? operation = null;
        Exception? failure = null;
        Exception? expectedCancellation = null;
        try
        {
            operation = start(cancellation.Token);
            using var watchdogCancellation = new CancellationTokenSource();
            var watchdog = readinessWatchdog ?? Task.Delay(TimeSpan.FromSeconds(5), watchdogCancellation.Token);
            try
            {
                var first = await Task.WhenAny(ready, operation, watchdog);
                // Placing readiness first also handles tasks already complete on entry.
                if (ReferenceEquals(first, ready)) await ready;
                else if (ReferenceEquals(first, operation))
                {
                    var result = await operation;
                    throw new InvalidOperationException("Web search completed before tools/call readiness. Result: " + result);
                }
                else throw new TimeoutException("Web search did not reach tools/call readiness within the fixture watchdog; operation status: " + operation.Status + ".");
            }
            finally { watchdogCancellation.Cancel(); }
            expectedCancellation = await assertions(operation, cancellation);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            var pending = operation is not null && !operation.IsCompleted;
            try { cancellation.Cancel(); }
            catch (Exception error) { KeepFailure(ref failure, error); }
            try { release(); }
            catch (Exception error) { KeepFailure(ref failure, error); }
            if (operation is not null)
            {
                try { await operation; }
                catch (Exception error) when (ReferenceEquals(error, expectedCancellation) ||
                    pending && cancellation.IsCancellationRequested && error is OperationCanceledException canceled &&
                    canceled.CancellationToken == cancellation.Token && error.Data.Count == 0)
                { }
                catch (Exception error) { KeepFailure(ref failure, error); }
            }
            if (cleanup is not null)
            {
                try { await cleanup(); }
                catch (Exception error) { KeepFailure(ref failure, error); }
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void KeepFailure(ref Exception? primary, Exception error)
    {
        if (primary is null) { primary = error; return; }
        if (ReferenceEquals(primary, error)) return;
        var errors = primary.Data["Strands.CleanupErrors"] is IEnumerable<Exception> prior ? prior.ToList() : [];
        if (!errors.Any(existing => ReferenceEquals(existing, error))) errors.Add(error);
        primary.Data["Strands.CleanupErrors"] = errors;
    }

    private static async Task<string> InvokeAsync(ITool tool, object input, CancellationToken cancellationToken = default)
    {
        var arguments = JsonSerializer.SerializeToElement(input);
        return await tool.InvokeAsync(arguments, new(null!, new("search", tool.Spec.Name, arguments), new Dictionary<string, object?>()), cancellationToken);
    }
    private static async Task WithinAsync(Task operation)
    {
        Assert.Same(operation, await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(5))));
        await operation;
    }
}
