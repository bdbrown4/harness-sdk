using System.Text.Json;
using Strands.Agents.Conversation;

namespace Strands.Agents.Tests;

// Source-only characterizations for the frozen G2 review1 candidate. Not executed.
public sealed class DirectToolHistoryAdversarialCharacterizationTests
{
    [Fact]
    public async Task PythonNoRecordManagementCannotEraseAnActiveRecordedPrefix()
    {
        using var lifetime = new CancellationTokenSource();
        var manager = new Strands.Agents.Conversation.SlidingWindowConversationManager(new() { WindowSize = 0 });
        var model = new NoModel();
        var outerTool = Tool("recorded", "{}", (_, _, _) => new("outer-result"));
        var nestedTool = Tool("nested", "{}", (_, _, _) => new("nested-result"));
        await using var agent = new Agent(new() { Model = model, Tools = [outerTool, nestedTool], ConversationManager = manager });
        Task<ToolResultBlock>? outer = null;
        Task<ToolResultBlock>? nested = null;
        Exception? nestedFailure = null;
        var added = 0;
        using var hook = agent.Hooks.Register<MessageAddedEvent>(async (value, _) =>
        {
            added++;
            if (added != 1) return;
            Assert.IsType<ToolUseBlock>(Assert.Single(value.Message.Content));
            nested = agent.InvokeToolAsync(Call("nested"), null,
                new() { Profile = DirectToolCallProfile.Python, RecordHistory = false }, lifetime.Token).AsTask();
            nestedFailure = await Capture(nested);
        });
        try
        {
            outer = agent.InvokeToolAsync(Call("recorded"), null,
                new() { Profile = DirectToolCallProfile.TypeScript }, lifetime.Token).AsTask();
            var accepted = await Within(outer);

            // A policy may refuse the nested operation, or preserve the live prefix while
            // performing management. It must not silently erase the request and publish its result.
            Assert.Null(Record.Exception(() => ConversationHistory.Validate(agent.Messages)));
            Assert.Equal(3, agent.Messages.Count);
            var use = Assert.IsType<ToolUseBlock>(Assert.Single(agent.Messages[0].Content));
            var result = Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages[1].Content));
            Assert.Equal("recorded", use.Name);
            Assert.Equal(use.ToolUseId, result.ToolUseId);
            Assert.Equal("outer-result", accepted.Content);
            Assert.Equal(accepted.Content, result.Content);
            Assert.Equal(3, added);
            Assert.Equal(0, manager.RemovedMessageCount);
            if (nestedFailure is not null) Assert.IsType<ConcurrentInvocationException>(nestedFailure);
            Assert.Equal(0, model.Calls);
        }
        finally
        {
            lifetime.Cancel();
            if (nested is not null) await Capture(nested);
            if (outer is not null) await Capture(outer);
        }
    }

    [Fact]
    public async Task PythonRegisteredReplacementFiltersHistoryWithoutNarrowingBody()
    {
        var originalInput = Json("{\"allowed\":\"yes\",\"extra\":7}");
        var originalBodies = 0;
        var replacementBodies = 0;
        var model = new NoModel();
        var original = Tool("requested", "{\"originalOnly\":{\"type\":\"string\"}}", (_, _, _) =>
        { originalBodies++; throw new InvalidOperationException("The selected replacement owns this call."); });
        var replacement = Tool("effective", "{\"allowed\":{\"type\":\"string\"}}", (input, context, _) =>
        {
            replacementBodies++;
            Assert.Equal(7, input.GetProperty("extra").GetInt32());
            Assert.Equal("yes", context.ToolUse.Input.GetProperty("allowed").GetString());
            Assert.Equal(7, context.ToolUse.Input.GetProperty("extra").GetInt32());
            return new("replacement-result");
        });
        await using var agent = new Agent(new() { Model = model, Tools = [original, replacement] });
        using var before = agent.Hooks.Register<BeforeToolCallEvent>((value, _) =>
        {
            value.ToolUse = value.ToolUse with { Name = "effective" };
            value.SelectedTool = replacement;
            return default;
        });

        var result = await Within(agent.InvokeToolAsync(Call("requested", originalInput), null,
            new() { Profile = DirectToolCallProfile.Python }, default).AsTask());

        Assert.Equal("replacement-result", result.Content);
        Assert.Equal(0, originalBodies);
        Assert.Equal(1, replacementBodies);
        AssertFilteredPythonHistory(agent, ToolResultStatus.Success);
        Assert.Equal(7, originalInput.GetProperty("extra").GetInt32());
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task PythonDeniedRewrittenRegisteredCallStillFiltersRecordedInput()
    {
        var originalInput = Json("{\"allowed\":\"yes\",\"extra\":7}");
        var bodies = 0;
        var model = new NoModel();
        var original = Tool("requested", "{\"originalOnly\":{\"type\":\"string\"}}", (_, _, _) =>
        { bodies++; throw new InvalidOperationException("A denied call cannot run a body."); });
        var replacement = Tool("effective", "{\"allowed\":{\"type\":\"string\"}}", (_, _, _) =>
        { bodies++; throw new InvalidOperationException("A denied call cannot run a body."); });
        await using var agent = new Agent(new() { Model = model, Tools = [original, replacement] });
        using var before = agent.Hooks.Register<BeforeToolCallEvent>((value, _) =>
        {
            value.ToolUse = value.ToolUse with { Name = "effective" };
            value.SelectedTool = replacement;
            value.CancelReason = "policy denied";
            return default;
        });

        var result = await Within(agent.InvokeToolAsync(Call("requested", originalInput), null,
            new() { Profile = DirectToolCallProfile.Python }, default).AsTask());

        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Equal("policy denied", result.Content);
        Assert.Equal(0, bodies);
        AssertFilteredPythonHistory(agent, ToolResultStatus.Error);
        Assert.Equal(7, originalInput.GetProperty("extra").GetInt32());
        Assert.Equal(0, model.Calls);
    }

    private static void AssertFilteredPythonHistory(Agent agent, ToolResultStatus status)
    {
        ConversationHistory.Validate(agent.Messages);
        Assert.Equal(4, agent.Messages.Count);
        var use = Assert.IsType<ToolUseBlock>(Assert.Single(agent.Messages[1].Content));
        Assert.Equal("effective", use.Name);
        Assert.Equal("call", use.ToolUseId);
        Assert.Equal("allowed", Assert.Single(use.Input.EnumerateObject()).Name);
        Assert.Equal("yes", use.Input.GetProperty("allowed").GetString());
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages[2].Content));
        Assert.Equal(use.ToolUseId, result.ToolUseId);
        Assert.Equal(status, result.Status);
        var text = Assert.IsType<TextBlock>(Assert.Single(agent.Messages[0].Content)).Text;
        var prefix = "agent.tool.effective direct tool call.\nInput parameters: ";
        Assert.StartsWith(prefix, text);
        var advertised = Json(text.Substring(prefix.Length).TrimEnd('\n'));
        Assert.Equal("allowed", Assert.Single(advertised.EnumerateObject()).Name);
    }

    private static FunctionTool Tool(string name, string properties,
        Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> callback)
        => new(new(name, "Source-only direct history characterization", Json("{\"type\":\"object\",\"properties\":" + properties + "}")), callback);

    private static ToolUseBlock Call(string name, JsonElement? input = null) => new("call", name, input ?? Json("{}"));
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static async Task<Exception?> Capture(Task task) { try { await task; return null; } catch (Exception error) { return error; } }
    private static async Task<T> Within<T>(Task<T> task)
    {
        using var watchdog = new CancellationTokenSource();
        var timeout = Task.Delay(TimeSpan.FromSeconds(5), watchdog.Token);
        try { Assert.Same(task, await Task.WhenAny(task, timeout)); return await task; }
        finally { watchdog.Cancel(); try { await timeout; } catch (OperationCanceledException error) when (error.CancellationToken == watchdog.Token) { } }
    }

    private sealed class NoModel : IModel
    {
        internal int Calls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Direct calls must not invoke a model."); }
    }
}
