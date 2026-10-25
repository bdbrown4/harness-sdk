using System.Text.Json;

namespace Strands.Agents;

/// <summary>An item from a tool stream: a progress update or exactly one terminal rich result.</summary>
public interface IToolStreamEvent;

/// <summary>An ephemeral progress update. JSON elements and documents are detached; other CLR values remain caller-owned and must not be mutated while observers use them.</summary>
public sealed record ToolStreamEvent : IToolStreamEvent
{
    /// <summary>Creates a progress update with optional caller data.</summary>
    public ToolStreamEvent(object? data = null) => Data = data switch { JsonElement json => json.Clone(), JsonDocument document => document.RootElement.Clone(), _ => data };
    /// <summary>The detached JSON value or caller-owned ephemeral CLR payload.</summary>
    public object? Data { get; }
}

/// <summary>The final rich output of a tool stream. No further items may follow it.</summary>
public sealed record ToolStreamResultEvent : IToolStreamEvent
{
    /// <summary>Creates a terminal result and detaches its JSON content.</summary>
    public ToolStreamResultEvent(ToolResultBlock result) => Result = (ToolResultBlock)Message.CloneBlock(result ?? throw new ArgumentNullException(nameof(result)));
    /// <summary>The final result, whose identifier must match the executing tool use.</summary>
    public ToolResultBlock Result { get; }
}

/// <summary>An optional tool capability that reports progress before its exactly-one rich terminal result.</summary>
public interface IStreamingTool : ITool
{
    /// <summary>Streams zero or more progress events followed by exactly one terminal result, honoring cancellation and releasing its iterator on every exit.</summary>
    IAsyncEnumerable<IToolStreamEvent> StreamAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default);
}

/// <summary>A streaming tool backed by an asynchronous sequence delegate. The caller owns dependencies captured by the callback.</summary>
public sealed class StreamingFunctionTool(ToolSpec spec, Func<JsonElement, ToolContext, CancellationToken, IAsyncEnumerable<IToolStreamEvent>> callback) : IStreamingTool, IRichTool
{
    /// <inheritdoc />
    public ToolSpec Spec { get; } = spec ?? throw new ArgumentNullException(nameof(spec));
    private readonly Func<JsonElement, ToolContext, CancellationToken, IAsyncEnumerable<IToolStreamEvent>> _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    /// <inheritdoc />
    public IAsyncEnumerable<IToolStreamEvent> StreamAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => _callback(input, context, cancellationToken);
    /// <inheritdoc />
    public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => (await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false)).Content;
    /// <inheritdoc />
    public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => ToolStreamRunner.InvokeAsync(this, input, context, null, cancellationToken);
}
