using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Testing;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class WebFetchHarnessTests
{
    [Fact]
    public async Task DefaultCurlRejectsAnUnknownCommandLanguageBeforeExecutingAnything()
    {
        var sandbox = new UnknownCommandSandbox();
        await Assert.ThrowsAsync<NotSupportedException>(() => Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            Model = new TextModel(),
            BuiltinTools = ["web_fetch"],
            BuiltinPlugins = [],
            Sandbox = sandbox,
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Skills = new() { Enabled = false },
            Session = new() { Enabled = false },
        }));
        Assert.Equal(0, sandbox.Calls);
    }

    [Fact]
    public async Task DefaultDelegatesHaveIndependentFetchedPageCaches()
    {
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes("source facts")));
        var model = new TextModel();
        model.Turns.Enqueue(Call("web_fetch", new { url = server.Endpoint.AbsoluteUri }));
        model.Turns.Enqueue(Call("subagent", new { task = "inspect", tools = new[] { "web_fetch" } }));
        model.Turns.Enqueue(Call("web_fetch", new { url = server.Endpoint.AbsoluteUri }));
        await using var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            Model = model,
            BuiltinTools = ["web_fetch", "subagent"],
            BuiltinPlugins = [],
            WebFetch = new() { Transport = WebFetchTransport.Direct },
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Skills = new() { Enabled = false },
            Session = new() { Enabled = false },
        });
        await agent.InvokeAsync("delegate");
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task SelectedWebFetchUsesTheSelectedCommandSandboxAndItsBounds()
    {
        var sandbox = new CommandSandbox();
        await using var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            Model = new TextModel(),
            BuiltinTools = ["web_fetch"],
            BuiltinPlugins = [],
            Sandbox = sandbox,
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Skills = new() { Enabled = false },
            Session = new() { Enabled = false },
        });
        Assert.Equal("web_fetch", Assert.Single(agent.Tools).Spec.Name);
        var tool = agent.Tools[0];
        var input = JsonSerializer.SerializeToElement(new { url = "https://remote.example/page" });
        Assert.Equal("remote content", await tool.InvokeAsync(input, new(agent, new("fetch", "web_fetch", input), new Dictionary<string, object?>())));
        Assert.Equal(1, sandbox.Calls);
        Assert.Equal(TimeSpan.FromSeconds(35), sandbox.Options!.Timeout);
        Assert.Equal(8 * 1024 * 1024, sandbox.Options.MaxOutputBytes);
        Assert.Contains("head -c 5242880", sandbox.Command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PortableWebFetchBindsAnExplicitModelAndDirectTransport()
    {
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes("source facts")));
        var main = new TextModel();
        var summary = new TextModel();
        var resolved = new List<string>();
        var config = HarnessAgentConfig.Parse("{\"builtinTools\":{\"*\":false,\"web_fetch\":{\"transport\":\"direct\",\"model\":\"application/summary\"}},\"builtinPlugins\":[],\"caching\":false,\"skills\":false,\"memory\":false,\"session\":false,\"contextManager\":false}");
        await using var agent = await Harness.CreateAsync(config.ToOptions(new()
        {
            ModelResolver = name => { resolved.Add(name); return name == "application/summary" ? summary : main; },
        }) with
        { BackgroundTasks = null });
        var input = JsonSerializer.SerializeToElement(new { url = server.Endpoint.ToString(), prompt = "Explain" });
        Assert.Equal("answer", await Assert.Single(agent.Tools).InvokeAsync(input, new(agent, new("fetch", "web_fetch", input), new Dictionary<string, object?>())));
        Assert.Empty(main.Requests);
        Assert.Contains("source facts", Assert.Single(summary.Requests).Messages.Single().Text);
        Assert.Equal(new[] { Harness.DefaultModel, "application/summary" }, resolved);
    }

    private sealed class CommandSandbox : ICommandSandbox, IEnvironmentSandbox
    {
        public int Calls { get; private set; }
        public ShellExecutionOptions? Options { get; private set; }
        public string? Command { get; private set; }
        public ValueTask<SandboxExecutionResult> ExecuteAsync(string command, ShellExecutionOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++; Options = options; Command = command;
            return new(new SandboxExecutionResult("text/plain; charset=utf-8\nhttps://remote.example/page\n" + Convert.ToBase64String(Encoding.UTF8.GetBytes("remote content")), "", 0));
        }
        public ValueTask<string?> GetPlatformAsync(CancellationToken cancellationToken = default) => new("Linux remote");
        public ValueTask<string> GetWorkingDirectoryAsync(CancellationToken cancellationToken = default) => new("/remote");
        public ValueTask<IReadOnlyList<SandboxEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => new(Array.Empty<SandboxEntry>());
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class UnknownCommandSandbox : ICommandSandbox
    {
        public int Calls { get; private set; }
        public ValueTask<SandboxExecutionResult> ExecuteAsync(string command, ShellExecutionOptions? options = null, CancellationToken cancellationToken = default)
        { Calls++; return new(new SandboxExecutionResult("text/plain\nhttps://remote.example/\n", "", 0)); }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class TextModel : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public Queue<ModelStreamEvent[]> Turns { get; } = new();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request); await Task.CompletedTask;
            var events = Turns.Count > 0 ? Turns.Dequeue() : new ModelStreamEvent[] { new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: "answer"), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn) };
            foreach (var item in events) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }
    }
    private static ModelStreamEvent[] Call(string name, object input) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("call-" + name, name, JsonSerializer.SerializeToElement(input))),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse),
    ];
}
