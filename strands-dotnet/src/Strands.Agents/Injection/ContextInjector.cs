using Strands.Agents.Middleware;

namespace Strands.Agents.Injection;

/// <summary>Appends freshly rendered text to the model input without changing durable conversation history.</summary>
public sealed class ContextInjector : IPlugin
{
    private readonly Func<InjectionContext, CancellationToken, ValueTask<string?>> _renderContent;
    private readonly ContextInjectorOptions _options;

    /// <summary>Creates an injector with an awaited text renderer.</summary>
    /// <param name="renderContent">Renders text verbatim; null or whitespace skips this call. The returned text may contain application data, so the renderer must escape untrusted fields as appropriate.</param>
    /// <param name="options">Optional plugin name and trigger policy.</param>
    public ContextInjector(Func<InjectionContext, CancellationToken, ValueTask<string?>> renderContent, ContextInjectorOptions? options = null)
    {
        Guard.NotNull(renderContent, nameof(renderContent));
        _renderContent = renderContent;
        _options = options ?? new();
        Guard.NotNullOrWhiteSpace(_options.Name, nameof(options));
        if (!Enum.IsDefined(typeof(InjectionTrigger), _options.Trigger)) throw new ArgumentOutOfRangeException(nameof(options));
        Name = _options.Name;
    }

    /// <inheritdoc />
    public string Name { get; }
    /// <inheritdoc />
    public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent));
        cancellationToken.ThrowIfCancellationRequested();
        agent.Middleware.RegisterInput(MiddlewareStages.InvokeModel, TransformAsync);
        return default;
    }

    private async ValueTask<InvokeModelContext> TransformAsync(InvokeModelContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var last = context.Messages.LastOrDefault();
        var injectionContext = new InjectionContext(context.Agent, context.Messages);
        bool triggered;
        try
        {
            triggered = _options.Predicate?.Invoke(injectionContext) ?? (_options.Trigger == InjectionTrigger.EveryTurn ||
                last?.Role == Role.User && !last.Content.OfType<ToolResultBlock>().Any());
        }
        catch (Exception error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Warn("Context injection trigger failed; skipping injection: " + error.Message);
            return context;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!triggered) return context;
        string? text;
        try { text = await _renderContent(injectionContext, cancellationToken).ConfigureAwait(false); }
        catch (Exception error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Warn("Context injection renderer failed; skipping injection: " + error.Message);
            return context;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) return context;
        var index = context.Messages.Count - 1;
        while (index >= 0 && context.Messages[index].Role != Role.User) index--;
        if (index < 0) return context;
        var original = context.Messages[index];
        var separator = original.Content.Count > 0 && !text!.StartsWith("\n", StringComparison.Ordinal) ? "\n\n" : "";
        var messages = context.Messages.ToArray();
        messages[index] = new(original.Role, original.Content.Concat(new[] { new TextBlock(separator + text) }).ToArray(), original.TrackingId, original.Metadata);
        return context with
        {
            Messages = messages,
            DynamicTrailingBlocks = checked(context.DynamicTrailingBlocks + (index == messages.Length - 1 ? 1 : 0))
        };
    }

    private static void Warn(string message)
    {
        try { SdkLogging.Warning("{Message}", message); }
        catch (Exception) { }
    }

    /// <inheritdoc />
    public IReadOnlyList<ITool> GetTools() => Array.Empty<ITool>();
}
