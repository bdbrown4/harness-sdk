using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.Tests;

public sealed class ToolFactoryTests : IAsyncLifetime
{
    private readonly Agent _agent = new(new() { Model = new NoCallsModel() });
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _agent.DisposeAsync().AsTask();
    [Fact]
    public void DerivesMethodAndParameterMetadataWithOptionalDefaults()
    {
        var tool = ToolFactory.Create((Func<int, int, int>)Add);

        Assert.Equal("sum", tool.Spec.Name);
        Assert.Equal("Adds two integers.", tool.Spec.Description);
        var schema = tool.Spec.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(new[] { "left" }, schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("integer", schema.GetProperty("properties").GetProperty("left").GetProperty("type").GetString());
        Assert.Equal("First operand.", schema.GetProperty("properties").GetProperty("left").GetProperty("description").GetString());
        Assert.Equal(2, schema.GetProperty("properties").GetProperty("right").GetProperty("default").GetInt32());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task BindsOptionalArgumentsAndReturnsTypedJson()
    {
        var tool = ToolFactory.Create((Func<int, int, int>)Add);
        var result = await Assert.IsAssignableFrom<IRichTool>(tool).InvokeResultAsync(JsonSerializer.SerializeToElement(new { left = 3 }), Context(tool));

        Assert.Equal("call", result.ToolUseId);
        Assert.Equal(5, Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json.GetInt32());
        Assert.Equal("5", await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { left = 3 }), Context(tool)));
    }

    [Fact]
    public async Task InvalidArgumentsDoNotInvokeUserCodeEvenWhenCalledDirectly()
    {
        var calls = 0;
        int Count(int amount) { calls++; return amount; }
        var tool = ToolFactory.Create((Func<int, int>)Count, new() { Name = "count" });

        await Assert.ThrowsAsync<ArgumentException>(async () => await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { amount = "invalid" }), Context(tool)));
        await Assert.ThrowsAsync<ArgumentException>(async () => await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { amount = 2, extra = true }), Context(tool)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task InjectsContextAndCancellationIntoAsyncCallback()
    {
        ToolContext? observed = null;
        CancellationToken token = default;
        async ValueTask<string> Greet(string name, ToolContext context, CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            observed = context;
            token = cancellationToken;
            return "Hello " + name;
        }
        var tool = ToolFactory.Create((Func<string, ToolContext, CancellationToken, ValueTask<string>>)Greet, new() { Name = "greet", Description = "Greets a person." });
        var context = Context(tool);
        using var cancellation = new CancellationTokenSource();

        Assert.Equal("Hello Ada", await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { name = "Ada" }), context, cancellation.Token));
        Assert.Same(context, observed);
        Assert.Equal(cancellation.Token, token);
        Assert.Equal(new[] { "name" }, tool.Spec.InputSchema.GetProperty("properties").EnumerateObject().Select(item => item.Name));
    }

    [Fact]
    public async Task NullableReferenceParameterAndNullDefaultRemainValid()
    {
        var tool = ToolFactory.Create((Func<string?, string>)NullableName);
        Assert.Equal("anonymous", await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { name = (string?)null }), Context(tool)));
        Assert.Equal("anonymous", await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { }), Context(tool)));
    }

    [Fact]
    public async Task NonGenericTaskDoesNotLeakRuntimeVoidTaskResult()
    {
        var tool = ToolFactory.Create((Func<Task>)Complete);
        var result = await Assert.IsAssignableFrom<IRichTool>(tool).InvokeResultAsync(JsonSerializer.SerializeToElement(new { }), Context(tool));
        Assert.Equal(JsonValueKind.Null, Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json.ValueKind);
    }

    [Fact]
    public async Task RecursiveDtoSchemasResolveWithinTheirOwnParameter()
    {
        var tool = ToolFactory.Create((Func<Branch, int>)Depth);
        var input = JsonSerializer.SerializeToElement(new { branch = new { name = "root", child = new { name = "leaf", child = (object?)null } } });
        Assert.Equal("2", await tool.InvokeAsync(input, Context(tool)));
    }

    [Fact]
    public async Task NullableDtoWrapperPreservesRecursiveAndRepeatedNestedReferences()
    {
        var tool = ToolFactory.Create((Func<Envelope?, int>)CountEnvelope);
        var input = JsonSerializer.SerializeToElement(new { envelope = new { first = new { name = "root", child = new { name = "leaf" } }, second = new { name = "other" } } });

        Assert.Equal("3", await tool.InvokeAsync(input, Context(tool)));
        Assert.Equal("0", await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { envelope = (object?)null }), Context(tool)));
    }

    [Fact]
    public async Task DiscoversOnlyAttributedPublicMethodsAndRetainsInstanceState()
    {
        var service = new Counter();
        var tools = ToolFactory.FromObject(service);
        Assert.Equal(new[] { "increment" }, tools.Select(tool => tool.Spec.Name));
        var tool = Assert.Single(tools);
        Assert.Equal("3", await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 3 }), Context(tool)));
        Assert.Equal("7", await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 4 }), Context(tool)));
        Assert.Equal(7, service.Value);
    }

    [Fact]
    public async Task TaskOfTUsesTheDeclaredSerializableResult()
    {
        var tool = ToolFactory.Create((Func<int, Task<Answer>>)GetAnswer);
        var result = await Assert.IsAssignableFrom<IRichTool>(tool).InvokeResultAsync(JsonSerializer.SerializeToElement(new { value = 8 }), Context(tool));
        Assert.Equal(8, Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task UserExceptionIsPreservedWithoutReflectionWrapping()
    {
        var expected = new FormatException("original");
        string Fail() => throw expected;
        var tool = ToolFactory.Create((Func<string>)Fail, new() { Name = "fail" });
        var actual = await Assert.ThrowsAsync<FormatException>(async () => await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { }), Context(tool)));
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task CancelledCallsAndDuplicatePropertiesNeverInvokeTheCallback()
    {
        var calls = 0;
        int Count(int value) { calls++; return value; }
        var tool = ToolFactory.Create((Func<int, int>)Count, new() { Name = "count" });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 1 }), Context(tool), cancellation.Token));
        using var duplicate = JsonDocument.Parse("{\"value\":1,\"value\":2}");
        await Assert.ThrowsAsync<ArgumentException>(async () => await tool.InvokeAsync(duplicate.RootElement, Context(tool)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SerializationOptionsAreCopiedAndAppliedToSchemaAndResult()
    {
        var settings = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        settings.Converters.Add(new JsonStringEnumConverter());
        var tool = ToolFactory.Create((Func<Mode, Mode>)EchoMode, new() { SerializerOptions = settings });
        settings.Converters.Clear();
        Assert.Equal("Fast", Assert.IsType<JsonBlock>(Assert.Single((await Assert.IsAssignableFrom<IRichTool>(tool).InvokeResultAsync(JsonSerializer.SerializeToElement(new { mode = "Fast" }), Context(tool))).ContentBlocks)).Json.GetString());
        Assert.Equal(new[] { "Fast", "Slow" }, tool.Spec.InputSchema.GetProperty("properties").GetProperty("mode").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task AttributedStreamingCallbackRetainsProgressAndRichTerminal()
    {
        var tool = ToolFactory.Create((Func<ToolContext, CancellationToken, IAsyncEnumerable<IToolStreamEvent>>)Progress);
        var streaming = Assert.IsAssignableFrom<IStreamingTool>(tool);
        var events = new List<IToolStreamEvent>();
        await foreach (var item in streaming.StreamAsync(JsonSerializer.SerializeToElement(new { }), Context(tool))) events.Add(item);
        Assert.Equal("working", Assert.IsType<ToolStreamEvent>(events[0]).Data);
        Assert.Equal("done", Assert.IsType<ToolStreamResultEvent>(events[1]).Result.Content);
        Assert.Equal("done", await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { }), Context(tool)));
    }

    [Fact]
    public async Task RequiredNullableReferenceStillRequiresItsProperty()
    {
        var tool = ToolFactory.Create((Func<string?, string>)RequiredNullableName);
        Assert.Equal("anonymous", await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { name = (string?)null }), Context(tool)));
        await Assert.ThrowsAsync<ArgumentException>(async () => await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { }), Context(tool)));
    }

    [Fact]
    public async Task NullTaskIsAProtocolErrorRatherThanSuccessfulJsonNull()
    {
        var tool = ToolFactory.Create((Func<Task<string>>)NullTask);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { }), Context(tool)));
    }

    [Fact]
    public async Task StaticDiscoveryAndExplicitMethodBindingDoNotInvokeDuringConstruction()
    {
        var tools = ToolFactory.FromType(typeof(StaticMethods));
        Assert.Equal(new[] { "identity" }, tools.Select(tool => tool.Spec.Name));
        Assert.Equal("4", await Assert.Single(tools).InvokeAsync(JsonSerializer.SerializeToElement(new { value = 4 }), Context(Assert.Single(tools))));
        var instance = new Counter();
        var method = typeof(Counter).GetMethod(nameof(Counter.Increment))!;
        Assert.Throws<ArgumentException>(() => ToolFactory.Create(method, null));
        var tool = ToolFactory.Create(method, instance);
        Assert.Equal(0, instance.Value);
        Assert.Equal("5", await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 5 }), Context(tool)));
    }

    [Fact]
    public void DuplicateNamesAndUnsafeSignaturesFailWithoutExecutingMethods()
    {
        Assert.Throws<ArgumentException>(() => ToolFactory.FromObject(new DuplicateNames()));
        Assert.Throws<ArgumentException>(() => ToolFactory.Create((RefCallback)RefMethod));
        Assert.Throws<ArgumentException>(() => ToolFactory.Create((Action)AsyncVoid));
        Action multiple = StaticMethods.Nothing;
        multiple += StaticMethods.Nothing;
        Assert.Throws<ArgumentException>(() => ToolFactory.Create(multiple));
    }

    [Fact]
    public async Task RichResultsOwnJsonAndUseTheExecutingCallIdentifier()
    {
        using var document = JsonDocument.Parse("{\"value\":2}");
        ToolResultBlock Rich() => new("foreign", new ContentBlock[] { new JsonBlock(document.RootElement) }, structuredContent: document.RootElement);
        var tool = ToolFactory.Create((Func<ToolResultBlock>)Rich, new() { Name = "rich" });
        var result = await Assert.IsAssignableFrom<IRichTool>(tool).InvokeResultAsync(JsonSerializer.SerializeToElement(new { }), Context(tool));
        document.Dispose();
        Assert.Equal("call", result.ToolUseId);
        Assert.Equal(2, Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json.GetProperty("value").GetInt32());
        Assert.Equal(2, result.StructuredContent!.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task RenamedParameterIsTheOnlyAcceptedInputName()
    {
        var tool = ToolFactory.Create((Func<int, int>)Renamed);
        Assert.Equal("3", await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 3 }), Context(tool)));
        await Assert.ThrowsAsync<ArgumentException>(async () => await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { internalName = 3 }), Context(tool)));
    }

    private static string RequiredNullableName(string? name) => name ?? "anonymous";
    private static Task<string> NullTask() => null!;
    private delegate int RefCallback(ref int value);
    private static int RefMethod(ref int value) => value;
    private static async void AsyncVoid() => await Task.CompletedTask;
    private static int Renamed([ToolParameter("The value.", Name = "value")] int internalName) => internalName;

    public static class StaticMethods
    {
        [Tool("Returns the supplied integer.", Name = "identity")]
        public static int Identity(int value) => value;
        public static void Nothing() { }
    }

    public sealed class DuplicateNames
    {
        [Tool("Duplicate one.", Name = "duplicate")]
        public string First() => throw new InvalidOperationException();
        [Tool("Duplicate two.", Name = "duplicate")]
        public string Second() => throw new InvalidOperationException();
    }

    private static string NullableName(string? name = null) => name ?? "anonymous";
    private static async Task Complete() => await Task.CompletedTask;
    private static int Depth(Branch branch) => 1 + (branch.Child is null ? 0 : Depth(branch.Child));
    private static int CountEnvelope(Envelope? envelope) => envelope is null ? 0 : Depth(envelope.First) + Depth(envelope.Second);
    private static Task<Answer> GetAnswer(int value) => Task.FromResult(new Answer(value));
    private static Mode EchoMode(Mode mode) => mode;
    private static async IAsyncEnumerable<IToolStreamEvent> Progress(ToolContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ToolStreamEvent("working");
        yield return new ToolStreamResultEvent(new(context.ToolUse.ToolUseId, "done"));
    }

    public sealed record Branch(string Name, Branch? Child = null);
    public sealed record Envelope(Branch First, Branch Second);
    public sealed record Answer(int Value);
    public enum Mode { Fast, Slow }
    public sealed class Counter
    {
        public int Value { get; private set; }
        [Tool("Increments an instance counter.", Name = "increment")]
        public int Increment(int value) => Value += value;
        public void Ignore() => throw new InvalidOperationException();
    }

    [Tool("Adds two integers.", Name = "sum")]
    private static int Add([ToolParameter("First operand.")] int left, int right = 2) => left + right;

    private ToolContext Context(ITool tool) => new(_agent, new("call", tool.Spec.Name, JsonSerializer.SerializeToElement(new { })), new Dictionary<string, object?>());
    private sealed class NoCallsModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("A direct method tool must not invoke a model.");
    }
}
