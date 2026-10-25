using System.Runtime.CompilerServices;
using Strands.Agents;

namespace Strands.Harness.Tests;

public sealed class HarnessSystemPromptTests
{
    [Theory]
    [InlineData("rich")]
    [InlineData("empty")]
    [InlineData("cache-only")]
    [InlineData("empty-text")]
    public async Task ExplicitTypedInstructionsReachTheModelWithoutHarnessDefaults(string shape)
    {
        ContentBlock[] supplied = shape switch
        {
            "rich" => [new TextBlock("caller 雪"), new CachePointBlock("caller-ttl"), new TextBlock("last")],
            "cache-only" => [new CachePointBlock("caller-ttl")],
            "empty-text" => [new TextBlock("")],
            _ => []
        };
        var model = new RecordingModel();
        var core = new AgentOptions { Model = model, SystemPromptContent = supplied };
        await using var agent = await Harness.CreateAsync(Options(core));

        var result = await agent.InvokeAsync("question");

        Assert.Equal("answer", result.ToString());
        Assert.Same(model, agent.Model);
        Assert.Null(core.SystemPrompt);
        Assert.NotNull(agent.SystemPromptContent);
        Assert.Equal(supplied, agent.SystemPromptContent);
        var request = Assert.Single(model.Requests);
        Assert.NotNull(request.SystemPromptContent);
        Assert.Equal(supplied, request.SystemPromptContent);
        Assert.Equal(shape switch { "rich" => "caller 雪\nlast", "empty-text" => "", _ => null }, request.SystemPrompt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("caller prompt")]
    public async Task AbsentTypedInstructionsRetainStringPromptPrecedence(string? prompt)
    {
        var model = new RecordingModel();
        await using var agent = await Harness.CreateAsync(Options(new() { Model = model, SystemPrompt = prompt }));
        await agent.InvokeAsync("question");

        var request = Assert.Single(model.Requests);
        Assert.Null(request.SystemPromptContent);
        Assert.Equal(prompt ?? HarnessPrompt.Build("harness instructions"), request.SystemPrompt);
    }

    [Fact]
    public async Task ExplicitConflictingPromptRepresentationsRemainInvalid()
    {
        var model = new RecordingModel();
        var failure = await Assert.ThrowsAsync<ArgumentException>(() => Harness.CreateAsync(Options(new()
        {
            Model = model,
            SystemPrompt = "caller prompt",
            SystemPromptContent = [new TextBlock("typed prompt")]
        })));

        Assert.Contains("either SystemPrompt or SystemPromptContent", failure.Message);
        Assert.Empty(model.Requests);
    }

    private static HarnessOptions Options(AgentOptions core) => new()
    {
        AgentOptions = core,
        Instructions = "harness instructions",
        BackgroundTasks = null,
        BuiltinTools = [],
        BuiltinPlugins = [],
        Skills = new() { Enabled = false },
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new() { Enabled = false }
    };

    private sealed class RecordingModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
