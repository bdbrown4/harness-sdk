using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Strands.Agents.Monty;

namespace Strands.Harness.ModuleFixture;

public static class Exports
{
    private static readonly string GenerationIdentity = Guid.NewGuid().ToString("N");

    [HarnessExport("generation")]
    public static ITool Generation { get; } = ToolFactory.Create((Func<string>)(() => GenerationIdentity),
        new() { Name = "generation", Description = "Read this image's initialized identity" });

    public static Action? OnRead { get; set; }

    [HarnessExport("reentrant")]
    public static ITool Reentrant { get { OnRead?.Invoke(); return Generation; } }

    [HarnessExport("generationStream")]
    public static ITool GenerationStream { get; } = new StreamingFunctionTool(
        new("generation_stream", "Return typed output from this image", JsonSerializer.SerializeToElement(new { type = "object" })), StreamGeneration);

    private static async IAsyncEnumerable<IToolStreamEvent> StreamGeneration(JsonElement input, ToolContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ToolStreamEvent(GenerationIdentity);
        yield return new ToolStreamResultEvent(new(context.ToolUse.ToolUseId,
            new ContentBlock[] { new TextBlock(GenerationIdentity), new JsonBlock(JsonSerializer.SerializeToElement(new { generation = GenerationIdentity })) }));
    }

    [HarnessExport("origin")]
    public static ITool Origin => ToolFactory.Create((Func<string>)(() =>
#if MODULE_CONFLICT
        "conflict"
#else
        "original"
#endif
    ), new() { Name = "origin", Description = "Identify the selected compiled module" });

    [HarnessExport]
    public static IReadOnlyList<ITool> Tools => ToolFactory.FromType(typeof(ToolMethods));

    [HarnessExport("model")]
    public static IModel Model { get; } = new FixtureModel();

    [HarnessExport("plugin")]
    public static IReadOnlyList<IPlugin> Plugins => new IPlugin[] { new FixturePlugin() };

    [HarnessExport("settings")]
    public static Func<AgentOptions, AgentOptions> Settings => core => core with { MaxTurns = 7 };

    [HarnessExport("agent")]
    public static Agent CreateAgent() => new(new() { Model = Model, Name = "assembly_specialist", Description = "Loaded specialist" });

    [HarnessExport("asyncAgent")]
    public static Task<Agent> CreateAsyncAgent() => throw new InvalidOperationException("Unsupported asynchronous factory body ran");

    [HarnessExport("asyncAgentDelegate")]
    public static Func<Task<Agent>> AsyncAgentDelegate => () => throw new InvalidOperationException("Unsupported asynchronous factory body ran");

    [HarnessExport("throwing")]
    public static ITool Throwing => throw new InvalidDataException("fixture export failed");

    [HarnessExport("null")]
    public static ITool? Null => null;

    [HarnessExport("wrong")]
    public static string Wrong => "not a tool";

    [HarnessExport("invalidTools")]
    public static IReadOnlyList<ITool> InvalidTools => new ITool[] { null! };

    [HarnessExport("borrowed")]
    public static ITool Borrowed { get; } = new BorrowedTool();

    [HarnessExport("initSetters")]
    public static ITool InitSetters
    {
        get
        {
            var harness = new HarnessOptions { Instructions = "native" };
            var bedrock = new BedrockModelOptions { ModelId = "fixture" };
            var monty = new ProgrammaticToolCallerOptions { MaxSuspensions = 3 };
            return new FunctionTool(new("init_setters", "Read options from a precompiled extension", JsonSerializer.SerializeToElement(new { type = "object" })),
                (_, _, _) => new ValueTask<string>(harness.Instructions + ":" + bedrock.ModelId + ":" + monty.MaxSuspensions));
        }
    }

    [HarnessExport("overloaded")]
    public static string Overloaded() => "zero";
    public static string Overloaded(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Tool("Return a value from the native fixture", Name = "assembly_echo")]
    [HarnessExport("echo")]
    public static string Echo(string value) => "native:" + value;
}

public sealed class BorrowedTool : ITool, IDisposable
{
    private bool _disposed;
    public ToolSpec Spec { get; } = new("borrowed", "Caller-owned fixture", JsonSerializer.SerializeToElement(new { type = "object" }));
    public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new(_disposed ? "disposed" : "alive");
    public void Dispose() => _disposed = true;
}

public static class ToolMethods
{
    [Tool("Return the supplied text", Name = "first")]
    public static string First(string value) => value;
    [Tool("Return a second response", Name = "second")]
    public static string Second() => "two";
}

[HarnessExport("toolClass")]
public static class ExportedTools
{
    [Tool("Return a class-discovered response", Name = "class_tool")]
    public static string Run() => "class";
}

public sealed class FixturePlugin : IPlugin
{
    public string Name => "fixture:plugin";
    public int InitializeCalls { get; private set; }
    public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { InitializeCalls++; agent.State.Set("assemblyPlugin", true); return default; }
    public IReadOnlyList<ITool> GetTools() => Array.Empty<ITool>();
}

public sealed class FixtureModel : IModel
{
    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => new Stream(cancellationToken);
    private sealed class Stream(CancellationToken cancellationToken) : IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
    {
        private int _index = -1;
        private readonly ModelStreamEvent[] _events = { new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: "loaded agent"), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn) };
        public ModelStreamEvent Current => _events[_index];
        public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() { cancellationToken.ThrowIfCancellationRequested(); return new ValueTask<bool>(++_index < _events.Length); }
        public ValueTask DisposeAsync() => default;
    }
}
