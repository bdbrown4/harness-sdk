using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class OptionNullConformanceTests
{
    [Fact]
    public void ExternalTwoStringConstructorStillUsesItsSecondArgumentAsTheValue()
    {
        var legacy = new Option("choice", "literal value");
        Assert.Equal("literal value", legacy.Value);
        Assert.Equal(string.Empty, legacy.Description);
        var described = new Option("choice", "literal value", "model guidance");
        Assert.Equal("literal value", described.Value);
        Assert.Equal("model guidance", described.Description);
        var factory = Option.Null("choice", "model guidance");
        Assert.Null(factory.Value);
        Assert.Equal("model guidance", factory.Description);
        Assert.NotEqual(described, factory);
    }

    [Fact]
    public async Task NullContextSelectionDoesNotShareParentHistoryOrState()
    {
        var parentMarker = JsonSerializer.SerializeToElement("parent only");
        AgentSpec? resolved = null;
        ModelRequest? request = null;
        var model = new CaptureModel(value => request = value);
        var tool = Subagent.Create(new()
        {
            Context = new Choice([Option.Null("fresh")]),
            Tools = new Fixed(Array.Empty<string>()),
            Builder = (specification, _, _) =>
            {
                resolved = specification;
                return new(new Agent(new()
                {
                    Model = model,
                    Messages = specification.Messages,
                    SystemPrompt = specification.Instructions
                }));
            }
        });
        await using var parent = new Agent(new()
        {
            Model = model,
            Messages = [Message.User("private parent history")],
            State = new Dictionary<string, JsonElement> { ["private"] = parentMarker }
        });
        var input = JsonSerializer.SerializeToElement(new { task = "fresh work", context = "fresh", last_messages = 1 });
        var result = await tool.InvokeResultAsync(input, new(parent, new("fresh-context", tool.Spec.Name, input), new Dictionary<string, object?>()));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.NotNull(resolved);
        Assert.Equal("none", resolved.Context);
        Assert.NotNull(request);
        Assert.DoesNotContain(request.Messages, value => value.Text.IndexOf("private parent history", StringComparison.Ordinal) >= 0);
        Assert.Equal("parent only", parent.State.Get<string>("private"));
        Assert.Equal("private parent history", Assert.Single(parent.Messages).Text);
    }

    private sealed class CaptureModel(Action<ModelRequest> capture) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            capture(request);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "complete");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
