using System.Runtime.CompilerServices;
using Strands.Agents;
using Strands.Harness.Plugins;

namespace Strands.Harness.Tests;

public sealed class EnvironmentCharacterCountingTests
{
    private const string Marker = "\n… (truncated — read the full file if you need the rest)";

    [Theory]
    [InlineData("astral-exact")]
    [InlineData("astral-truncated")]
    [InlineData("boundary-astral-exact")]
    [InlineData("boundary-astral-truncated")]
    public async Task UnicodeCodePointCapKeepsWholeAstralCharacters(string scenario)
    {
        var prefix = scenario.StartsWith("boundary", StringComparison.Ordinal)
            ? new string('a', EnvironmentContext.AgentsMarkdownCap - 1) + "🚀"
            : string.Concat(Enumerable.Repeat("🚀", EnvironmentContext.AgentsMarkdownCap));
        var truncated = scenario.EndsWith("truncated", StringComparison.Ordinal);
        var text = prefix + (truncated ? "suffix" : string.Empty);
        var sandbox = new SelectedEnvironment(text);
        var model = new RecordingModel();
        await using var agent = new Agent(new()
        {
            Model = model,
            Plugins = [new EnvironmentContext(sandbox, new() { CharacterCounting = EnvironmentCharacterCounting.UnicodeCodePoints })]
        });

        await agent.InvokeAsync("inspect");

        Assert.Equal(prefix + (truncated ? Marker : string.Empty), Instructions(model));
        Assert.Equal(1, sandbox.Reads);
        Assert.All(agent.Messages, message => Assert.DoesNotContain("<AGENTS.md>", message.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultAsciiCapRetainsTheExistingTypeScriptBoundary(bool truncated)
    {
        var prefix = new string('a', EnvironmentContext.AgentsMarkdownCap);
        var sandbox = new SelectedEnvironment(prefix + (truncated ? "suffix" : string.Empty));
        var model = new RecordingModel();
        await using var agent = new Agent(new() { Model = model, Plugins = [new EnvironmentContext(sandbox)] });

        await agent.InvokeAsync("inspect");

        Assert.Equal(prefix + (truncated ? Marker : string.Empty), Instructions(model));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("bmp-below")]
    [InlineData("bmp-exact")]
    [InlineData("bmp-truncated")]
    [InlineData("combining-exact")]
    [InlineData("combining-truncated")]
    [InlineData("crlf-exact")]
    [InlineData("crlf-truncated")]
    [InlineData("isolated-high-surrogate")]
    [InlineData("isolated-low-surrogate")]
    public async Task UnicodeCountingUsesCodePointsRatherThanGraphemesOrBytes(string scenario)
    {
        var cap = EnvironmentContext.AgentsMarkdownCap;
        var prefix = scenario switch
        {
            "empty" => string.Empty,
            "bmp-below" => new string('雪', cap - 1),
            "combining-exact" or "combining-truncated" => string.Concat(Enumerable.Repeat("e\u0301", cap / 2)),
            "crlf-exact" or "crlf-truncated" => string.Concat(Enumerable.Repeat("\r\n", cap / 2)),
            "isolated-high-surrogate" => new string('a', cap - 1) + "\ud800",
            "isolated-low-surrogate" => new string('a', cap - 1) + "\udc00",
            _ => new string('雪', cap)
        };
        var truncated = scenario.EndsWith("truncated", StringComparison.Ordinal) || scenario.StartsWith("isolated", StringComparison.Ordinal);
        var sandbox = new SelectedEnvironment(prefix + (truncated ? "suffix" : string.Empty));
        var model = new RecordingModel();
        await using var agent = new Agent(new()
        {
            Model = model,
            Plugins = [new EnvironmentContext(sandbox, new() { CharacterCounting = EnvironmentCharacterCounting.UnicodeCodePoints })]
        });

        await agent.InvokeAsync("inspect");

        if (prefix.Length == 0)
        {
            var input = string.Join("\n", Assert.Single(model.Requests).Messages.Select(message => message.Text));
            Assert.DoesNotContain("<AGENTS.md>", input);
        }
        else Assert.Equal(prefix + (truncated ? Marker : string.Empty), Instructions(model));
        Assert.All(agent.Messages, message => Assert.DoesNotContain("<AGENTS.md>", message.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utf16DefaultAndExplicitPolicyRetainTheTypeScriptSplitPairBoundary(bool explicitPolicy)
    {
        var prefix = new string('a', EnvironmentContext.AgentsMarkdownCap - 1);
        var sandbox = new SelectedEnvironment(prefix + "🚀suffix");
        var model = new RecordingModel();
        var plugin = explicitPolicy ? new EnvironmentContext(sandbox, new() { CharacterCounting = EnvironmentCharacterCounting.Utf16CodeUnits })
            : new EnvironmentContext(sandbox);
        await using var agent = new Agent(new() { Model = model, Plugins = [plugin] });

        await agent.InvokeAsync("inspect");

        Assert.Equal(prefix + "\ud83d" + Marker, Instructions(model));
    }

    [Fact]
    public void UnknownCountingPolicyRejectsBeforeAnySandboxRead()
    {
        var sandbox = new SelectedEnvironment("unread");
        Assert.Throws<ArgumentOutOfRangeException>(() => new EnvironmentContext(sandbox, new() { CharacterCounting = (EnvironmentCharacterCounting)91 }));
        Assert.Equal(0, sandbox.Reads);
    }

    [Fact]
    public void BlankPluginNameRetainsItsExistingArgumentIdentity()
    {
        var error = Assert.Throws<ArgumentException>(() => new EnvironmentContext(new SelectedEnvironment("unread"), new() { Name = " " }));
        Assert.Equal("name", error.ParamName);
    }

    private static string Instructions(RecordingModel model)
    {
        var input = string.Join("\n", Assert.Single(model.Requests).Messages.Select(message => message.Text));
        const string start = "<AGENTS.md>\n", end = "\n</AGENTS.md>";
        var index = input.IndexOf(start, StringComparison.Ordinal);
        Assert.True(index >= 0, "Expected selected-sandbox instructions in the model request.");
        var contentStart = index + start.Length;
        var contentEnd = input.IndexOf(end, contentStart, StringComparison.Ordinal);
        Assert.True(contentEnd >= contentStart);
        return input.Substring(contentStart, contentEnd - contentStart);
    }

    private sealed class SelectedEnvironment(string text) : IEnvironmentSandbox
    {
        internal int Reads { get; private set; }
        public ValueTask<string?> GetPlatformAsync(CancellationToken cancellationToken = default) => new("selected");
        public ValueTask<string> GetWorkingDirectoryAsync(CancellationToken cancellationToken = default) => new(".");
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("AGENTS.md", path);
            Reads++;
            return new(text);
        }
        public ValueTask<IReadOnlyList<SandboxEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
            => new(Array.Empty<SandboxEntry>());
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Environment discovery is read only.");
    }

    private sealed class RecordingModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "finished");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
