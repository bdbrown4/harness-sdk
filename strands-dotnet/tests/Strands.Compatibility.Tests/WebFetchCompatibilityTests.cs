using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness;
using Strands.Harness.Testing;
using Strands.Harness.Tools;
using HarnessFactory = Strands.Harness.Harness;

namespace Strands.Compatibility.Tests;

public sealed class WebFetchCompatibilityTests
{
    [Fact]
    public async Task IndependentHarnessesCannotReceiveAnotherHarnessesDirectSessionCookie()
    {
        var path = "/web-fetch-cookie-" + Guid.NewGuid().ToString("N") + "/";
        string? receivedCookie = null;
        await using var server = new WebTestServer(request =>
        {
            if (request.Target == path + "set")
                return new(Encoding.UTF8.GetBytes("session created")) { SetCookie = "web_fetch_session=first-harness; Path=" + path };
            receivedCookie = request.Headers.TryGetValue("Cookie", out var cookie) ? cookie : null;
            return new(Encoding.UTF8.GetBytes(receivedCookie is null ? "public page" : "private page for the first harness"));
        });
        await using var first = await HarnessFactory.CreateAsync(Options(new Model()) with { BackgroundTasks = null, Sandbox = new TextOnlySandbox(), WebFetch = new() { Transport = WebFetchTransport.Direct } });
        await using var independent = await HarnessFactory.CreateAsync(Options(new Model()) with { BackgroundTasks = null, Sandbox = new TextOnlySandbox(), WebFetch = new() { Transport = WebFetchTransport.Direct } });
        Assert.Equal("session created", await Bounded(InvokeAsync(Assert.Single(first.Tools), first, new Uri(server.Endpoint, path + "set").AbsoluteUri)));
        Assert.Equal("public page", await Bounded(InvokeAsync(Assert.Single(independent.Tools), independent, new Uri(server.Endpoint, path + "fresh").AbsoluteUri)));
        Assert.Null(receivedCookie);
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task SuppliedDirectCookieContainerSurvivesHarnessDisposal()
    {
        var path = "/web-fetch-cookie-" + Guid.NewGuid().ToString("N") + "/";
        string? receivedCookie = null;
        await using var server = new WebTestServer(request =>
        {
            if (request.Target == path + "set")
                return new(Encoding.UTF8.GetBytes("session created")) { SetCookie = "web_fetch_session=caller-owned; Path=" + path };
            receivedCookie = request.Headers.TryGetValue("Cookie", out var cookie) ? cookie : null;
            return new(Encoding.UTF8.GetBytes(receivedCookie is null ? "public page" : "caller authenticated page"));
        });
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = cookies };
        using var client = new HttpClient(handler);
        var fresh = new Uri(server.Endpoint, path + "fresh");
        {
            await using var first = await HarnessFactory.CreateAsync(Options(new Model()) with { BackgroundTasks = null, Sandbox = new TextOnlySandbox(), WebFetch = new() { Transport = WebFetchTransport.Direct, HttpClient = client } });
            await using var second = await HarnessFactory.CreateAsync(Options(new Model()) with { BackgroundTasks = null, Sandbox = new TextOnlySandbox(), WebFetch = new() { Transport = WebFetchTransport.Direct, HttpClient = client } });
            Assert.Equal("session created", await Bounded(InvokeAsync(Assert.Single(first.Tools), first, new Uri(server.Endpoint, path + "set").AbsoluteUri)));
            Assert.Equal("caller authenticated page", await Bounded(InvokeAsync(Assert.Single(second.Tools), second, fresh.AbsoluteUri)));
            Assert.Equal("web_fetch_session=caller-owned", receivedCookie);
        }
        Assert.True(handler.UseCookies);
        Assert.Same(cookies, handler.CookieContainer);
        Assert.Equal("caller-owned", cookies.GetCookies(fresh)["web_fetch_session"]?.Value);
        Assert.Equal("caller authenticated page", await Bounded(client.GetStringAsync(new Uri(server.Endpoint, path + "probe"))));
        Assert.Equal(3, server.Calls);
    }

    [Fact]
    public async Task Windows32BitShellFindsSystemCurlWithoutDependingOnTheHostPath()
    {
        if (Path.DirectorySeparatorChar != '\\' || !Environment.Is64BitOperatingSystem) return;
        var directory = Path.Combine(Path.GetTempPath(), "strands-curl-x86-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sandbox = new LocalFileSandbox(shellOptions: new()
            {
                ExecutablePath = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe"),
                WorkingDirectory = directory,
                Environment = new Dictionary<string, string> { ["PATH"] = directory },
                MaxOutputBytes = 8 * 1024 * 1024,
            });
            var architecture = await Bounded(sandbox.ExecuteAsync("[Console]::Write([IntPtr]::Size)").AsTask());
            Assert.Equal(0, architecture.ExitCode);
            Assert.Equal("4", architecture.StandardOutput);
            await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes("System curl from x86")));
            await using var agent = await HarnessFactory.CreateAsync(Options(new Model()) with { BackgroundTasks = null, Sandbox = sandbox });
            var result = await Bounded(InvokeAsync(Assert.Single(agent.Tools), agent, server.Endpoint.AbsoluteUri));
            Assert.True(result == "System curl from x86", result);
        }
        finally { Directory.Delete(directory); }
    }

    [Fact]
    public async Task DefaultCurlReadsMoreThanOneMiBThroughTheSelectedSandbox()
    {
        var html = "<script>" + new string('x', 2 * 1024 * 1024) + "</script><p>Framework café</p>";
        await using var server = new WebTestServer(request => request.Target == "/start"
            ? new(Encoding.UTF8.GetBytes("intermediate body"), Status: 302, Location: "/page") : new(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8"));
        await using var agent = await HarnessFactory.CreateAsync(Options(new Model()) with { BackgroundTasks = null });
        Assert.Equal("Framework café", await Bounded(InvokeAsync(Assert.Single(agent.Tools), agent, new Uri(server.Endpoint, "start").AbsoluteUri)));
    }

    [Fact]
    public async Task CurlCannotRaiseTheCallersCapturePolicy()
    {
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes(new string('x', 4096))));
        await using var agent = await HarnessFactory.CreateAsync(Options(new Model()) with { BackgroundTasks = null, Sandbox = new LocalFileSandbox(shellOptions: new() { MaxOutputBytes = 1024 }) });
        Assert.Contains("capture was truncated", await Bounded(InvokeAsync(Assert.Single(agent.Tools), agent, server.Endpoint.AbsoluteUri)));
    }

    [Fact]
    public async Task ExplicitDirectHttpUsesDeclaredCharsetAndFreshSummarizerConversations()
    {
        await using var server = new WebTestServer(request => request.Target == "/start" ? new(Array.Empty<byte>(), Status: 302, Location: "/page") : new(new byte[] { 99, 97, 102, 233 }, "text/plain; charset=windows-1252"));
        var model = new Model();
        await using var agent = await HarnessFactory.CreateAsync(Options(model) with { BackgroundTasks = null, Sandbox = new TextOnlySandbox(), WebFetch = new() { Transport = WebFetchTransport.Direct } });
        var tool = Assert.Single(agent.Tools);
        var url = new Uri(server.Endpoint, "start").AbsoluteUri;
        Assert.Equal("summary", await Bounded(InvokeAsync(tool, agent, url, "First")));
        Assert.Equal("summary", await Bounded(InvokeAsync(tool, agent, url, "Second")));
        Assert.Equal(2, server.Calls);
        Assert.Equal(2, model.Requests.Count);
        Assert.All(model.Requests, request => { Assert.Single(request.Messages); Assert.Contains("café", request.Messages[0].Text); });
        Assert.Contains("First", model.Requests[0].Messages[0].Text);
        Assert.Contains("Second", model.Requests[1].Messages[0].Text);
    }

    [Fact]
    public async Task CancellationTerminatesCurlWaitingForItsBody()
    {
        var headers = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes("never sent")) { HeadersSent = headers, BodyGate = gate, AllowPeerAbort = true });
        await using var agent = await HarnessFactory.CreateAsync(Options(new Model()) with { BackgroundTasks = null });
        using var cancellation = new CancellationTokenSource();
        var operation = InvokeAsync(Assert.Single(agent.Tools), agent, server.Endpoint.AbsoluteUri, cancellationToken: cancellation.Token);
        await Bounded(headers.Task);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(operation));
    }

    [Fact]
    public async Task DirectDeadlinesStopPendingLegacyBodyReads()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes("never sent")) { BodyGate = gate, AllowPeerAbort = true });
        await using var agent = await HarnessFactory.CreateAsync(Options(new Model()) with { BackgroundTasks = null, WebFetch = new() { Transport = WebFetchTransport.Direct, Timeout = TimeSpan.FromMilliseconds(500) } });
        Assert.Contains("gave up after 0.5s", await Bounded(InvokeAsync(Assert.Single(agent.Tools), agent, server.Endpoint.AbsoluteUri)));
    }

    [Fact]
    public async Task PortableWebConfigurationBindsModelModulesAndDoesNotGrantTextOnlyCommandAuthority()
    {
        var model = new Model();
        var summary = new Model();
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes("source facts")));
        var config = HarnessAgentConfig.Parse("{\"builtinTools\":{\"*\":false,\"web_fetch\":{\"transport\":\"direct\",\"model\":{\"module\":\"summary\"}}},\"builtinPlugins\":[],\"caching\":false,\"contextManager\":false,\"skills\":false,\"memory\":false,\"session\":false,\"sandbox\":{\"module\":\"text\"}}");
        await using var agent = await HarnessFactory.CreateAsync(config.ToOptions(new()
        {
            ModelResolver = _ => model,
            ModelModuleResolver = reference => { Assert.Equal("summary", reference.Module); return summary; },
            SandboxResolver = _ => new TextOnlySandbox(),
        }) with
        { BackgroundTasks = null });
        Assert.Equal("summary", await Bounded(InvokeAsync(Assert.Single(agent.Tools), agent, server.Endpoint.AbsoluteUri, "Explain")));
        Assert.Empty(model.Requests);
        Assert.Single(summary.Requests);
        await Assert.ThrowsAsync<ArgumentException>(() => HarnessFactory.CreateAsync(Options(model) with { BackgroundTasks = null, Sandbox = new TextOnlySandbox() }));
    }

    [Fact]
    public async Task PerCommandCaptureIsClampedOnTheLegacyProcessPath()
    {
        ICommandSandbox sandbox = new LocalFileSandbox(shellOptions: new() { MaxOutputBytes = 64 });
        var command = Path.DirectorySeparatorChar == '\\' ? "[Console]::Write('x' * 1024)" : "head -c 1024 /dev/zero | tr '\\000' x";
        var result = await Bounded(sandbox.ExecuteAsync(command, new() { MaxOutputBytes = 128 }).AsTask());
        Assert.Equal(64, result.StandardOutput.Length);
        Assert.True(result.OutputTruncated);
    }

    [Fact]
    public async Task CancellationCallbacksCannotLeakResponseDisposalExceptions()
    {
        var stream = new CancellationStream();
        var cleanup = new IOException("legacy content disposal failed");
        using var client = new HttpClient(new Handler(() => new(HttpStatusCode.OK) { Content = new ThrowingContent(stream, cleanup) }));
        await using var agent = await HarnessFactory.CreateAsync(Options(new Model()) with { BackgroundTasks = null, Sandbox = new TextOnlySandbox(), WebFetch = new() { Transport = WebFetchTransport.Direct, HttpClient = client } });
        using var cancellation = new CancellationTokenSource();
        var operation = InvokeAsync(Assert.Single(agent.Tools), agent, "http://fixture.test/page", cancellationToken: cancellation.Token);
        await Bounded(stream.Started.Task);
        var callbackFailure = Record.Exception(cancellation.Cancel);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(operation));
        Assert.Null(callbackFailure);
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(failure.Data["Strands.CleanupErrors"]));
    }

    private static HarnessOptions Options(IModel model) => new()
    {
        Model = model,
        BuiltinTools = ["web_fetch"],
        BuiltinPlugins = [],
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Skills = new() { Enabled = false },
        Session = new() { Enabled = false },
    };
    private static Task<string> InvokeAsync(ITool tool, Agent agent, string url, string? prompt = null, CancellationToken cancellationToken = default)
    {
        var input = JsonSerializer.SerializeToElement(new { url, prompt });
        return tool.InvokeAsync(input, new(agent, new("fetch", "web_fetch", input), new Dictionary<string, object?>()), cancellationToken).AsTask();
    }
    private static async Task<T> Bounded<T>(Task<T> operation)
    {
        Assert.Same(operation, await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(30))));
        return await operation;
    }
    private sealed class TextOnlySandbox : ISandbox
    {
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No host filesystem access.");
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No host filesystem access.");
    }
    private sealed class Model : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request); await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "summary");
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
    }
    private sealed class CancellationStream : MemoryStream
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { Started.TrySetResult(true); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
    private sealed class ThrowingContent(Stream stream, Exception failure) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => stream.CopyToAsync(destination);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) throw failure; }
    }
}
