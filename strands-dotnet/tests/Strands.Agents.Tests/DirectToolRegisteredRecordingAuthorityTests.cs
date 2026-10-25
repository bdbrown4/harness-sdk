using System.Text.Json;
using Strands.Agents.Conversation;

namespace Strands.Agents.Tests;

public sealed class DirectToolRegisteredRecordingAuthorityTests
{
    [Fact]
    public async Task PythonRecordingUsesRegisteredNameSchemaWhenTheSelectedBodyIsUnregistered()
    {
        var registeredCalls = 0;
        var selectedCalls = 0;
        var model = new NoModel();
        var registered = new FunctionTool(new("reported", "Registered recording authority", Schema("allowed", "string")),
            (_, _, _) => { registeredCalls++; return new("registered"); });
        var selected = new FunctionTool(new("reported", "Unregistered execution replacement", Schema("extra", "integer")),
            (input, context, _) =>
            {
                selectedCalls++;
                Assert.Equal("yes", input.GetProperty("allowed").GetString());
                Assert.Equal(7, context.ToolUse.Input.GetProperty("extra").GetInt32());
                return new("selected");
            });
        await using var agent = new Agent(new() { Model = model, Tools = [registered] });
        using var hook = agent.Hooks.Register<BeforeToolCallEvent>((value, _) =>
        {
            value.SelectedTool = selected;
            return default;
        });
        var original = Json("{\"allowed\":\"yes\",\"extra\":7}");

        var result = await agent.InvokeToolAsync(new("source-call", "reported", original), null,
            new() { Profile = DirectToolCallProfile.Python }, default);

        Assert.Equal("selected", result.Content);
        Assert.Equal(0, registeredCalls);
        Assert.Equal(1, selectedCalls);
        ConversationHistory.Validate(agent.Messages);
        Assert.Equal(4, agent.Messages.Count);
        var recorded = Assert.IsType<ToolUseBlock>(Assert.Single(agent.Messages[1].Content));
        Assert.Equal("reported", recorded.Name);
        Assert.Equal("source-call", recorded.ToolUseId);
        Assert.Equal("allowed", Assert.Single(recorded.Input.EnumerateObject()).Name);
        Assert.Equal("yes", recorded.Input.GetProperty("allowed").GetString());
        var returned = Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages[2].Content));
        Assert.Equal(recorded.ToolUseId, returned.ToolUseId);
        Assert.Equal("selected", returned.Content);
        var text = Assert.IsType<TextBlock>(Assert.Single(agent.Messages[0].Content)).Text;
        const string prefix = "agent.tool.reported direct tool call.\nInput parameters: ";
        Assert.StartsWith(prefix, text);
        Assert.Equal("allowed", Assert.Single(Json(text.Substring(prefix.Length).TrimEnd('\n')).EnumerateObject()).Name);
        Assert.Equal(7, original.GetProperty("extra").GetInt32());
        Assert.Equal(0, model.Calls);
    }

    private static JsonElement Schema(string property, string type)
        => JsonSerializer.SerializeToElement(new { type = "object", properties = new Dictionary<string, object> { [property] = new { type } } });

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class NoModel : IModel
    {
        internal int Calls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Direct tools must not invoke a model.");
        }
    }
}
