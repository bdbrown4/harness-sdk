namespace Strands.Agents;

/// <summary>When each model attempt observes the agent's current ready-model selection.</summary>
public enum ModelSelectionTiming
{
    /// <summary>Capture before BeforeModelCall hooks, matching TypeScript and the native default.</summary>
    BeforeModelHooks = 0,
    /// <summary>Observe the selection after BeforeModelCall hooks, matching Python.</summary>
    AfterModelHooks = 1
}

/// <summary>How an agent projects retained history and chooses its post-invocation history-clear authority.</summary>
public enum ModelHistoryBehavior
{
    /// <summary>Preserve native construction-time capability, acknowledgement cursors and pending-input recovery. Replacements keep the same capability.</summary>
    Native = 0,
    /// <summary>Send retained local history and clear after invocation according to the constructor model reference's current stateful capability.</summary>
    TypeScript = 1,
    /// <summary>Send retained local history and clear after invocation according to the then-current model's stateful capability.</summary>
    Python = 2
}

public sealed partial class Agent
{
    private IModel _model;
    private readonly bool _modelStateful;
    private bool _restoredSourceHistoryDirty;
    private bool UsesNativeStatefulHistory => _options.ModelHistoryBehavior == ModelHistoryBehavior.Native && IsStateful;

    private void ValidateRetainedHistoryMetadata(int? modelHistoryCount, ModelInputReceipt? inputReceipt)
    {
        if (_options.ModelHistoryBehavior != ModelHistoryBehavior.Native && (modelHistoryCount is not null || inputReceipt is not null))
            throw new InvalidDataException("Retained-history profiles cannot import a native acknowledgement cursor or input receipt.");
    }

    private ValueTask ClearRetainedModelHistory(AfterInvocationEvent value, CancellationToken cancellationToken)
    {
        // Imported native pairs retain committed history; source-origin pauses follow source clearing.
        if (!_restored || value.Result?.Checkpoint is not null) return default;
        lock (_historyGate)
            if (_interruptState is { AssistantMessageIsIndependent: false, SourceHistoryOwned: false }) return default;
        var authority = _options.ModelHistoryBehavior == ModelHistoryBehavior.TypeScript ? _options.Model : Model;
        if (authority is not IStatefulModel { Stateful: true }) return default;
        var changed = false;
        lock (_historyGate)
        {
            if (_interruptState is { AssistantMessageIsIndependent: false, SourceHistoryOwned: false }) return default;
            if (_modelHistoryCount != 0 || _modelInputReceipt is not null)
                throw new InvalidOperationException("Retained-history clearing cannot discard native acknowledgement metadata.");
            if (_interruptState is { AssistantMessageIsIndependent: false, SourceHistoryOwned: true } pending)
            {
                _interruptState = pending with { AssistantMessageIsIndependent = true };
                changed = true;
            }
            if (_messages.Count != 0) { _messages.Clear(); changed = true; }
        }
        if (changed) MarkAcceptedHistory(CancellationToken.None, Volatile.Read(ref _invocationCancellation));
        return default;
    }

    /// <summary>Selects a borrowed ready model for subsequent model-attempt captures.</summary>
    /// <param name="model">The nonnull ready model. Native history requires the configured server-history capability; explicit retained-history profiles permit a different capability.</param>
    /// <remarks>Does not retarget an entered provider operation or transfer resource ownership.</remarks>
    public void SetModel(IModel model)
    {
        Guard.NotNull(model, nameof(model));
        lock (_initializationGate) ThrowIfDisposed();
        var stateful = _options.ModelHistoryBehavior == ModelHistoryBehavior.Native && model is IStatefulModel { Stateful: true };
        lock (_initializationGate)
        {
            ThrowIfDisposed();
            if (_options.ModelHistoryBehavior == ModelHistoryBehavior.Native && stateful != _modelStateful)
                throw new ArgumentException("A replacement model must preserve the agent's configured server-history capability.", nameof(model));
            Volatile.Write(ref _model, model);
        }
    }

    private IModel ResolveInvocationModel(InvocationProgress progress, IModel owner)
    {
        if (ReferenceEquals(progress.ModelOwner, owner) && progress.Model is { } current) return current;
        var selected = owner is IInvocationModel scoped ? scoped.CreateInvocationModel(progress.InvocationState) : owner;
        if (selected is null || _options.ModelHistoryBehavior == ModelHistoryBehavior.Native && (selected is IStatefulModel { Stateful: true }) != _modelStateful)
            throw new InvalidOperationException("An invocation model must preserve the configured server-history capability.");
        progress.ModelOwner = owner;
        progress.Model = selected;
        return selected;
    }
}
