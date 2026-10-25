using System.Globalization;
using Strands.Agents.Storage;

namespace Strands.Agents.Context;

/// <summary>Runs context reduction strategies before model calls and recovers from provider context overflow.</summary>
public sealed class ContextManager : IPlugin
{
    private readonly ContextManagerOptions _options;
    private readonly ContextStrategy[] _strategies;
    private Agent? _agent;
    private ITool? _retrievalTool;
    private IReadOnlyList<ITool> _agenticTools = [];
    private readonly HashSet<string> _retrievalToolUseIds = new(StringComparer.Ordinal);
    private int _overflowRetries;

    /// <summary>Creates a manager. Each instance binds to one agent; create a separate instance for each conversation.</summary>
    public ContextManager(ContextManagerOptions? options = null)
    {
        _options = options is null ? new() : options with { Strategies = options.Strategies?.ToArray() };
        if (!Enum.IsDefined(typeof(ContextMode), _options.Mode)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown context mode.");
        if (!Enum.IsDefined(typeof(StashRestoreFailurePolicy), _options.StashRestoreFailurePolicy)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown stash restore failure policy.");
        _strategies = (_options.Strategies ?? Defaults(_options.Mode)).Concat(new ContextStrategy[] { new EmergencyTruncateStrategy() }).ToArray();
        if (_strategies.Any(strategy => strategy is null)) throw new ArgumentException("Context strategies cannot contain null.", nameof(options));
    }
    /// <summary>Resolves the exact preset names auto and agentic. Null disables context management.</summary>
    /// <param name="mode">The preset name, or null.</param>
    /// <returns>A new manager, or null when no mode is supplied.</returns>
    public static ContextManager? From(string? mode) => mode switch
    {
        null => null,
        "auto" => new(),
        "agentic" => new(new() { Mode = ContextMode.Agentic }),
        _ => throw new ArgumentException("Unknown context mode. Valid presets: auto, agentic.", nameof(mode))
    };

    private static ContextStrategy[] Defaults(ContextMode mode) =>
    [
        Offload.Truncate(OffloadTarget.ToolResults, new() { PreviewTokens = 750 }).When(new() { Threshold = mode == ContextMode.Agentic ? 8_000 : 1_500 }),
        Offload.Summarize().When(new() { Utilization = mode == ContextMode.Agentic ? 1 : 0.85, PreserveRecent = 4 })
    ];
    /// <inheritdoc />
    public string Name => "strands:context-manager";
    /// <summary>The original-content stash after initialization, or null when stashing is disabled.</summary>
    public Stash? Stash { get; private set; }
    /// <inheritdoc />
    public async ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent));
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _agent, agent, null) is { } previous)
        {
            if (!ReferenceEquals(previous, agent)) throw new InvalidOperationException("A context manager binds to one agent. Create a separate manager per agent.");
            return;
        }
        if (_options.StashEnabled)
        {
            var backend = _options.StashStorage ?? agent.Storage ?? new InMemoryStorage();
            Stash = new(backend, agent.SessionId ?? agent.Id, agent.Id, _options.StashStorage is not null);
            if (agent.TakeRestoredContextCheckpoint() is { } restored)
            {
                if (restored.StorageType is { } previousType && previousType != Stash.StorageTypeName)
                    StashCheckpoint.Warn($"Context stash storage type changed from '{previousType}' to '{Stash.StorageTypeName}'; originals may be inaccessible.");
                if (restored.Entries is { } entries)
                    try { await Stash.LoadSnapshotAsync(entries, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception error) when (_options.StashRestoreFailurePolicy == StashRestoreFailurePolicy.Continue)
                    { StashCheckpoint.Warn($"Failed to restore context stash; continuing without missing originals: {error.Message}"); }
            }
            foreach (var message in agent.Messages) await StashMessageAsync(message, cancellationToken).ConfigureAwait(false);
            agent.Hooks.Register<MessageAddedEvent>((value, token) => StashMessageAsync(value.Message, token));
            if (_options.RetrievalToolEnabled)
                _retrievalTool = new ContextRetrievalTool(new(), (owner, reference, token) =>
                    ReferenceEquals(owner, agent) ? Stash.RetrieveAsync(reference, token) : new((ContentBlock?)null));
        }
        foreach (var strategy in _strategies) await strategy.InitializeAsync(agent, Stash, cancellationToken).ConfigureAwait(false);
        if (_options.Mode == ContextMode.Agentic) _agenticTools = new AgenticContextTools(agent).Tools;
        agent.Hooks.Register<BeforeModelCallEvent>(async (value, token) =>
        {
            if (await ApplyAsync(value.Agent, value.Request, false, token).ConfigureAwait(false))
                value.Request = value.Request with { Messages = value.Agent.Messages };
        }, HookOrder.SdkFirst);
        if (_options.Mode == ContextMode.Agentic)
            agent.Hooks.Register<BeforeModelCallEvent>(AppendTokenStatusAsync, HookOrder.SdkLast);
        agent.Hooks.Register<BeforeInvocationEvent>((_, _) => { _overflowRetries = 0; return default; });
        agent.Hooks.Register<AfterModelCallEvent>(async (value, _) =>
        {
            if (value.Error is not ContextWindowOverflowException || value.InvocationCancellationToken.IsCancellationRequested)
            { _overflowRetries = 0; return; }
            if (_overflowRetries >= 3) { _overflowRetries = 0; return; }
            var request = new ModelRequest(value.Agent.Messages, value.Agent.SystemPrompt, value.Agent.Tools.Select(value.Agent.RegisteredToolSpec).ToArray())
            { SystemPromptContent = value.Agent.SystemPromptContent };
            if (!await ApplyAsync(value.Agent, request, true, value.InvocationCancellationToken).ConfigureAwait(false)) return;
            _overflowRetries++;
            value.Retry = true;
        });
    }
    /// <inheritdoc />
    public IReadOnlyList<ITool> GetTools() => _retrievalTool is { } retrieval ? _agenticTools.Concat(new[] { retrieval }).ToArray() : _agenticTools;

    internal ContextManager CreateForAgent() => new(_options);

    // Built-in strategies are immutable; custom strategy-owned mutable state has no restore contract.
    internal void ValidateCatalogRestore()
    {
        if (_strategies.Any(strategy => strategy is not (OffloadStrategy or EmergencyTruncateStrategy)))
            throw new NotSupportedException("Post-initialization catalog restore supports immutable built-in context strategies; custom strategies require an explicit restore contract.");
    }

    internal async ValueTask<HashSet<string>> PrepareCatalogRestoreAsync(StashCheckpoint? checkpoint, IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        ValidateCatalogRestore();
        if (checkpoint is not null && Stash is null) throw new InvalidDataException("The selected context manager has no stash for the checkpoint's originals.");
        if (checkpoint?.StorageType is { } previousType && previousType != Stash!.StorageTypeName)
            StashCheckpoint.Warn($"Context stash storage type changed from '{previousType}' to '{Stash.StorageTypeName}'; originals may be inaccessible.");
        if (checkpoint?.Entries is { } entries)
            try { await Stash!.LoadSnapshotAsync(entries, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (_options.StashRestoreFailurePolicy == StashRestoreFailurePolicy.Continue)
            { StashCheckpoint.Warn($"Failed to restore context stash; continuing without missing originals: {error.Message}"); }
        var retrievalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.Role == Role.Assistant)
            {
                retrievalIds.Clear();
                foreach (var call in message.Content.OfType<ToolUseBlock>().Where(call => call.Name == ContextRetrievalTool.ToolName)) retrievalIds.Add(call.ToolUseId);
            }
            if (Stash is not null) await Stash.StoreMessageAsync(message, retrievalIds, cancellationToken).ConfigureAwait(false);
            if (message.Role == Role.User && message.Content.OfType<ToolResultBlock>().Any()) retrievalIds.Clear();
        }
        return retrievalIds;
    }

    internal void CommitCatalogRestore(HashSet<string> retrievalIds)
    {
        _retrievalToolUseIds.Clear();
        _retrievalToolUseIds.UnionWith(retrievalIds);
        _overflowRetries = 0;
    }

    private async ValueTask AppendTokenStatusAsync(BeforeModelCallEvent value, CancellationToken cancellationToken)
    {
        var request = value.Request;
        if (request.Messages.Count == 0) return;
        var used = await value.Agent.Model.CountTokensAsync(request, cancellationToken).ConfigureAwait(false);
        var capacity = value.Agent.Model.GetLimits(_options.ModelMetadata).ContextWindowLimit!.Value;
        var remaining = Math.Max(0, capacity - used);
        var percent = (used / (double)capacity * 100).ToString("F1", CultureInfo.InvariantCulture);
        var status = "\n\n<context-status>\n<used>" + used.ToString("N0", CultureInfo.InvariantCulture) + " / " + capacity.ToString("N0", CultureInfo.InvariantCulture) + " tokens (" + percent + "%)</used>\n<remaining>~" + remaining.ToString("N0", CultureInfo.InvariantCulture) + " tokens</remaining>\n</context-status>";
        var messages = request.Messages.ToArray();
        var last = messages[messages.Length - 1];
        messages[messages.Length - 1] = new(last.Role, last.Content.Concat(new[] { new TextBlock(status) }).ToArray(), last.TrackingId, last.Metadata);
        value.Request = request with { Messages = messages, DynamicTrailingBlocks = checked(request.DynamicTrailingBlocks + (last.Role == Role.User ? 1 : 0)) };
    }

    private async ValueTask StashMessageAsync(Message message, CancellationToken cancellationToken)
    {
        if (message.Role == Role.Assistant)
        {
            _retrievalToolUseIds.Clear();
            foreach (var call in message.Content.OfType<ToolUseBlock>().Where(call => call.Name == ContextRetrievalTool.ToolName)) _retrievalToolUseIds.Add(call.ToolUseId);
        }
        await Stash!.StoreMessageAsync(message, _retrievalToolUseIds, cancellationToken).ConfigureAwait(false);
        if (message.Role == Role.User && message.Content.OfType<ToolResultBlock>().Any()) _retrievalToolUseIds.Clear();
    }

    private async ValueTask<bool> ApplyAsync(Agent agent, ModelRequest request, bool overflow, CancellationToken cancellationToken)
    {
        var count = await agent.Model.CountTokensAsync(request, cancellationToken).ConfigureAwait(false);
        var context = new ContextState(agent, agent.Messages.ToList(), agent.Model.EstimateUtilization(count, _options.ModelMetadata), overflow, Stash);
        var anyActed = false;
        foreach (var strategy in _strategies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (strategy is EmergencyTruncateStrategy && anyActed) context.Overflow = false;
            var previous = context.Messages.ToArray();
            try
            {
                if (!await strategy.ApplyAsync(context, cancellationToken).ConfigureAwait(false) || previous.SequenceEqual(context.Messages))
                { context.Messages.Clear(); context.Messages.AddRange(previous); continue; }
                agent.ValidateOwnedContextHistory(context.Messages);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                context.Messages.Clear(); context.Messages.AddRange(previous);
                SdkLogging.Warning(error, "Context strategy '{Strategy}' failed; continuing: {Error}", strategy.Name, error.Message);
                continue;
            }
            anyActed = true;
            count = await agent.Model.CountTokensAsync(request with { Messages = context.Messages }, cancellationToken).ConfigureAwait(false);
            context.Utilization = agent.Model.EstimateUtilization(count, _options.ModelMetadata);
        }
        if (anyActed) await agent.ReplaceContextAsync(context.Messages, cancellationToken).ConfigureAwait(false);
        return anyActed;
    }
}
