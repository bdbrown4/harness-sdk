using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ModelContextProtocol.Client;
using Strands.Agents.Files;
using Strands.Agents.SchemaImplementation;
using Strands.Agents.Tests.Mcp;
using Client = Strands.Agents.Mcp.McpClient;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests;

public sealed class JsonSchemaEntrypointTests
{
    public static IEnumerable<object[]> Sources()
    {
        foreach (var source in new[] { "provider", "updates", "sandbox" })
            foreach (var admitted in new[] { false, true }) yield return [source, admitted];
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task ProviderUpdateAndSandboxPublicationsUseTheSameExactSchema(string source, bool admitted)
    {
        var tool = new RecordingTool();
        var provider = new Provider(tool);
        var sandbox = new VendingSandbox(tool);
        var model = new NeverModel();
        await using var agent = new Agent(new()
        {
            Model = model,
            ToolProviders = source == "sandbox" ? [] : [provider],
            EnableToolProviderUpdates = source == "updates",
            Sandbox = source == "sandbox" ? sandbox : null
        });
        await agent.InitializeAsync();
        if (source == "updates") provider.Publish(tool);
        using var input = Input(admitted);
        var result = await agent.InvokeToolAsync(new("entry", tool.Spec.Name, input.RootElement));
        Assert.Equal(admitted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        Assert.Equal(admitted ? 1 : 0, tool.Calls);
        Assert.Same(tool, Assert.Single(agent.Tools));
        Assert.Equal(source == "updates" ? 1 : 0, provider.Watches);
        Assert.Equal(source == "provider" ? 1 : 0, provider.Loads);
        Assert.Equal(0, model.Calls);
        Assert.Equal(0, sandbox.EnvironmentCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredOutputValidatesBeforeCustomDeserialization(bool admitted)
    {
        using var schema = JsonDocument.Parse(Schema);
        var deserializations = 0;
        var tool = new StructuredOutputTool<JsonElement>(new() { Schema = schema.RootElement, Deserializer = value => { deserializations++; return value.Clone(); } });
        var model = new NeverModel();
        await using var agent = new Agent(new() { Model = model });
        using var input = Input(admitted);
        var context = new ToolContext(agent, new("output", tool.Spec.Name, input.RootElement), new Dictionary<string, object?>());
        if (admitted) await tool.InvokeAsync(input.RootElement, context);
        else await Assert.ThrowsAsync<StructuredOutputException>(() => tool.InvokeAsync(input.RootElement, context).AsTask());
        Assert.Equal(admitted, tool.HasResult);
        Assert.Equal(admitted ? 1 : 0, deserializations);
        if (admitted) Assert.Equal(new string('9', 400), tool.Output.GetProperty("value").GetRawText());
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpDiscoveryAndOutputValidationUseTheReviewedSchemaCompiler(bool admitted)
    {
        using var server = new McpTaskClientTests.TaskServer();
        using var outputSchema = JsonDocument.Parse(Schema);
        using var value = Input(admitted);
        server.Respond = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Method switch
        {
            "tools/list" => McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new { tools = new[] { new { name = "echo", description = "An in-memory schema capability.", inputSchema = new { type = "object" }, outputSchema = outputSchema.RootElement } } }),
            "tools/call" => McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new { content = new[] { new { type = "text", text = "result" } }, structuredContent = value.RootElement }),
            _ => null
        });
        using var http = new HttpClient(server);
        await using var client = await Client.CreateHttpAsync(http,
            new() { Endpoint = new Uri("https://schema.invalid/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
            new() { ProtocolVersion = "2026-07-28", InitializationTimeout = TimeSpan.FromSeconds(5) });
        Assert.Single(await client.ListToolsAsync());
        using var input = JsonDocument.Parse("{}");
        if (admitted) Assert.Equal(new string('9', 400), (await client.CallToolAsync("echo", input.RootElement)).StructuredContent!.Value.GetProperty("value").GetRawText());
        else await Assert.ThrowsAsync<InvalidDataException>(() => client.CallToolAsync("echo", input.RootElement).AsTask());
        Assert.Single(server.Requests, request => request.Method == "tools/call");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvocationLocalToolsUseTheSameSchemaWithoutLeakingRegistration(bool admitted)
    {
        using var input = Input(admitted);
        var tool = new RecordingTool();
        var model = new CallingModel(new("call", tool.Spec.Name, input.RootElement));
        await using var agent = new Agent(new() { Model = model });
        await agent.InvokeAsync("selected", new() { EphemeralTools = [tool] });
        var result = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal(admitted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        Assert.Equal(admitted ? 1 : 0, tool.Calls);
        Assert.Empty(agent.Tools);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task GeneratedDelegateSchemasRetainNativeClrBindingAndOrdinaryInvalidInputRejection()
    {
        var calls = 0;
        var tool = ToolFactory.Create((Func<long, long>)(number => { calls++; return number; }), new ToolFactoryOptions { Name = "generated_schema" });
        await using var agent = new Agent(new() { Model = new NeverModel() });
        using var valid = JsonDocument.Parse("{\"number\":9223372036854775807}");
        var context = new ToolContext(agent, new("factory", tool.Spec.Name, valid.RootElement), new Dictionary<string, object?>());
        Assert.Equal("9223372036854775807", await tool.InvokeAsync(valid.RootElement, context));
        using var invalid = JsonDocument.Parse("{\"number\":\"2\"}");
        Assert.NotNull(await Record.ExceptionAsync(() => tool.InvokeAsync(invalid.RootElement, context).AsTask()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void LocalCompilationDoesNotReplaceVendorGlobalDefaultsOrOfficialDialectBehavior()
    {
        var defaults = BuildOptions.Default;
        var dialect = Dialect.Default;
        var optionsDialect = defaults.Dialect;
        var schemas = defaults.SchemaRegistry;
        var vocabularies = defaults.VocabularyRegistry;
        var dialects = defaults.DialectRegistry;
        const string schema = "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"integer\"}";
        using var input = JsonDocument.Parse("1e-400");
        var vendorBefore = JsonSchema.FromText(schema).Evaluate(input.RootElement).IsValid;
        using var declaration = JsonDocument.Parse(schema);
        Assert.False(ToolSchema.Compile(declaration.RootElement).Evaluate(input.RootElement).IsValid);
        Assert.Equal(vendorBefore, JsonSchema.FromText(schema).Evaluate(input.RootElement).IsValid);
        Assert.Same(defaults, BuildOptions.Default);
        Assert.Same(dialect, Dialect.Default);
        Assert.Same(optionsDialect, defaults.Dialect);
        Assert.Same(schemas, defaults.SchemaRegistry);
        Assert.Same(vocabularies, defaults.VocabularyRegistry);
        Assert.Same(dialects, defaults.DialectRegistry);
    }

    private const string Schema = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"}},\"required\":[\"value\"],\"additionalProperties\":false}";
    private static JsonDocument Input(bool admitted) => JsonDocument.Parse("{\"value\":" + new string('9', 400) + (admitted ? "" : ".5") + "}");

    private sealed class RecordingTool : ITool
    {
        internal int Calls;
        internal RecordingTool() { using var schema = JsonDocument.Parse(Schema); Spec = new("entrypoint", "A selected nonprocess capability.", schema.RootElement); }
        public ToolSpec Spec { get; }
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) { Calls++; return new("accepted"); }
    }

    private sealed class Provider(ITool tool) : IToolProviderUpdates
    {
        private IObserver<IReadOnlyList<ITool>>? _observer;
        internal int Loads, Watches;
        public ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => default;
        public ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default) { Loads++; return new((IReadOnlyList<ITool>)[tool]); }
        public ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => default;
        public ValueTask<IAsyncDisposable> WatchToolsAsync(ToolProviderContext context, IObserver<IReadOnlyList<ITool>> observer, CancellationToken cancellationToken = default)
        { Watches++; _observer = observer; observer.OnNext([tool]); return new((IAsyncDisposable)new Lease(() => _observer = null)); }
        internal void Publish(ITool selected) => _observer!.OnNext([selected]);
    }

    private sealed class Lease(Action close) : IAsyncDisposable { public ValueTask DisposeAsync() { close(); return default; } }
    private sealed class VendingSandbox(ITool tool) : Core.Sandbox
    {
        internal int EnvironmentCalls;
        private Exception Refused() { EnvironmentCalls++; return new InvalidOperationException("Selected schema tests never access an environment."); }
        public override IReadOnlyList<ITool> GetTools() => [tool];
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) => throw Refused();
        public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw Refused();
        public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw Refused();
        public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw Refused();
        public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw Refused();
        public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw Refused();
    }
    private sealed class NeverModel : IModel
    {
        internal int Calls;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) { Calls++; throw new InvalidOperationException("No model execution is selected."); }
    }
    private sealed class CallingModel(ToolUseBlock call) : IModel
    {
        internal int Calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            yield return new ModelMessageStartEvent();
            if (Calls == 1) { yield return new ModelContentBlockStartEvent(0, call); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent("tool_use"); }
            else { yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent("end_turn"); }
            await Task.CompletedTask;
        }
    }
}
