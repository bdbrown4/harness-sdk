using System.Runtime.CompilerServices;
using System.Text.Json;
using static Strands.Agents.Tests.ToolProviders.ToolProviderLifecycleTests;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class ToolProviderSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderMetadataIsDetachedAndSchemaAndBodyStayBound(bool background)
    {
        using var metadata = JsonDocument.Parse("{\"type\":\"integer\"}");
        var tool = new ChangingTool(new("stable", "captured", JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" } }))
        { OutputSchema = metadata.RootElement, Annotations = metadata.RootElement });
        var model = new CallingModel("stable", JsonSerializer.SerializeToElement(new { value = 7 }));
        await using var agent = new Agent(new()
        {
            Model = model,
            ToolProviders = [new Provider([tool])],
            BackgroundTasks = background ? new global::Strands.Agents.Tasks.BackgroundTasksConfig { Never = ["*"] } : null,
            Plugins = [new Plugin(_ => { metadata.Dispose(); tool.RefuseSpecification = true; }, [])]
        });
        await agent.InitializeAsync();
        Assert.Same(tool, Assert.Single(agent.Tools, value => ReferenceEquals(value, tool)));
        var result = await agent.InvokeAsync("run");
        Assert.Equal("done", result.ToString()); Assert.Equal(1, tool.Calls);
        var spec = Assert.Single(model.Requests[0].Tools!, value => value.Name == "stable");
        Assert.Equal("integer", spec.OutputSchema!.Value.GetProperty("type").GetString());
        Assert.Equal("integer", spec.Annotations!.Value.GetProperty("type").GetString());
        var invalid = await agent.InvokeToolAsync(new("invalid", "stable", JsonSerializer.SerializeToElement(new { value = "wrong" })));
        Assert.Equal(ToolResultStatus.Error, invalid.Status); Assert.Equal(1, tool.Calls); Assert.Equal(1, tool.SpecReads);
    }

    [Fact]
    public async Task NormalizedDirectLookupUsesCapturedProviderName()
    {
        var tool = new ChangingTool(new("stable-name", "captured", JsonSerializer.SerializeToElement(new { type = "object" })));
        await using var agent = new Agent(new() { Model = new Model(), ToolProviders = [new Provider([tool])], DirectToolNameResolution = ToolNameResolution.UnderscoreAlias });
        await agent.InitializeAsync(); tool.RefuseSpecification = true;
        var result = await agent.InvokeToolAsync(new("direct", "stable_name", JsonSerializer.SerializeToElement(new { })));
        Assert.Equal("original body", result.Content); Assert.Equal(1, tool.SpecReads);
    }

    internal sealed class CallingModel(string name, JsonElement input) : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return new ModelMessageStartEvent();
            if (Requests.Count == 1)
            {
                yield return new ModelContentBlockStartEvent(0, new("call", name, input));
                yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent("tool_use");
            }
            else { yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent("end_turn"); }
            await Task.CompletedTask;
        }
    }
    private sealed class ChangingTool(ToolSpec spec) : ITool
    {
        internal bool RefuseSpecification { get; set; }
        internal int SpecReads { get; private set; }
        internal int Calls { get; private set; }
        public ToolSpec Spec { get { SpecReads++; if (RefuseSpecification) throw new IOException("Provider metadata changed after registration."); return spec; } }
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) { Calls++; return new("original body"); }
    }
}
