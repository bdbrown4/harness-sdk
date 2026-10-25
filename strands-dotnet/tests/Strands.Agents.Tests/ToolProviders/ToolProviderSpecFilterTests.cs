using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class ToolProviderSpecFilterTests
{
    [Fact]
    public async Task FrozenSpecFilterSeesTheCapturedGetterResultAndCanOmitItBeforePublication()
    {
        var tool = new AlternatingTool(); ToolSpec? captured = null;
        await using var agent = new Agent(new()
        {
            Model = new Model(),
            ToolProviders = [new Provider(tool)],
            ToolFilter = value => value.Spec.Name == "public_allowed",
            ProviderSpecFilter = (value, spec) => { Assert.Same(tool, value); captured = spec; return false; }
        });
        await agent.InitializeAsync(); Assert.NotNull(captured); Assert.Equal("captured_forbidden", captured.Name);
        Assert.Empty(agent.Tools); Assert.Equal(2, tool.SpecReads); Assert.Equal(0, tool.BodyCalls);
    }

    [Fact]
    public async Task ThePredicateSeesTheExactRegistrationSnapshotAndTheModelGetsDetachedMetadata()
    {
        using var metadata = JsonDocument.Parse("{\"hint\":true}");
        var tool = new FixedTool(new("stable", "stable", JsonSerializer.SerializeToElement(new { type = "object" })) { Annotations = metadata.RootElement });
        ToolSpec? captured = null; var model = new Model();
        await using var agent = new Agent(new()
        {
            Model = model,
            ToolProviders = [new Provider(tool)],
            ProviderSpecFilter = (value, spec) => { Assert.Same(tool, value); captured = spec; metadata.Dispose(); return true; }
        });
        await agent.InitializeAsync(); Assert.NotNull(captured); Assert.NotSame(tool.Spec, captured); Assert.Same(captured, agent.RegisteredToolSpec(tool));
        await agent.InvokeAsync("inspect"); var advertised = Assert.Single(model.Request!.Tools!); Assert.Equal(captured.Name, advertised.Name);
        Assert.True(advertised.Annotations!.Value.GetProperty("hint").GetBoolean());
        Assert.True(captured.Annotations!.Value.GetProperty("hint").GetBoolean()); Assert.Same(tool, Assert.Single(agent.Tools));
    }

    [Fact]
    public async Task PredicateFailurePublishesNoneOfTheBatchAndPreservesTheOriginalError()
    {
        var primary = new IOException("frozen admission failed"); var first = new FixedTool(new("first", "first", JsonSerializer.SerializeToElement(new { type = "object" })));
        var second = new FixedTool(new("second", "second", JsonSerializer.SerializeToElement(new { type = "object" }))); var provider = new Provider(first, second);
        await using var agent = new Agent(new()
        { Model = new Model(), ToolProviders = [provider], ProviderSpecFilter = (_, spec) => spec.Name == "first" ? true : throw primary });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync())); Assert.Empty(agent.Tools); Assert.Equal(1, provider.Removals);
    }

    private sealed class Provider(params ITool[] tools) : IToolProvider
    {
        internal int Removals { get; private set; }
        public ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => default;
        public ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => new(tools);
        public ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) { Removals++; return default; }
    }
    private sealed class AlternatingTool : ITool
    {
        internal int SpecReads { get; private set; }
        internal int BodyCalls { get; private set; }
        public ToolSpec Spec => new(++SpecReads == 1 ? "public_allowed" : "captured_forbidden", "alternating", JsonSerializer.SerializeToElement(new { type = "object" }));
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) { BodyCalls++; return new("body"); }
    }
    private sealed class FixedTool(ToolSpec spec) : ITool
    {
        public ToolSpec Spec => spec;
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new("body");
    }
    private sealed class Model : IModel
    {
        internal ModelRequest? Request { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Request = request; yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn); await Task.CompletedTask;
        }
    }
}
