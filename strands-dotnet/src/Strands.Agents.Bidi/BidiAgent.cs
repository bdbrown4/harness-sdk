using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Strands.Agents.Bidi;

/// <summary>A persistent bidirectional conversation composed with an explicit real core agent for tool execution.</summary>
public sealed partial class BidiAgent : IAsyncDisposable
{
    private readonly BidiAgentOptions _options;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Message> _messages;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private Conversation? _conversation;
    private bool _disposed;
    private Task? _disposal;
    private static readonly AsyncLocal<CallbackScope?> Scope = new();
    private static readonly AsyncLocal<CallbackScope?> RestartHookScope = new();
    private static readonly AsyncLocal<CallbackScope?> SendScope = new();
    /// <summary>Captures conversation configuration without opening a connection.</summary>
    public BidiAgent(BidiAgentOptions options) : this(options, DelayAsync) { }
    internal BidiAgent(BidiAgentOptions options, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        Hooks = options.Hooks ?? new();
        if (options.Model is null || options.ToolAgent is null || options.Messages is null) throw new ArgumentException("A model, real tool agent, and nonnull history are required.", nameof(options));
        if (options.SessionStore is not null && options.SessionManager is not null) throw new ArgumentException("Configure either SessionStore or SessionManager, not both.", nameof(options));
        if (options.SystemPrompt is not null && options.SystemPromptContent is not null) throw new ArgumentException("Configure either SystemPrompt or SystemPromptContent, not both.", nameof(options));
        if (options.SystemPromptContent is not null) ApplyPrompt(ValidatePrompt(options.SystemPromptContent, nameof(options)));
        if (options.SessionManager is null && options.SessionSaveStrategy == BidiSessionSaveStrategy.Trigger) throw new ArgumentException("Trigger persistence requires a snapshot catalog manager.", nameof(options));
        BidiSnapshotSessionManager.ValidateIdentifier(options.AgentId, nameof(options.AgentId));
        if (options.SessionManager is { } manager && options.SessionId is { } sessionId && manager.SessionId != sessionId) throw new ArgumentException("SessionId must match the configured snapshot manager.", nameof(options));
        _messages = options.Messages.Select(Copy).ToList();
        State = new(options.State);
        if (options.SessionId is not null && string.IsNullOrWhiteSpace(options.SessionId)) throw new ArgumentException("SessionId cannot be empty.", nameof(options));
        if (!Enum.IsDefined(typeof(BidiSessionSaveStrategy), options.SessionSaveStrategy)) throw new ArgumentOutOfRangeException(nameof(options));
        SessionId = options.SessionManager?.SessionId ?? options.SessionId ?? SessionId;
    }
    /// <summary>The selected display name, preserving every nonempty caller value.</summary>
    public string Name => string.IsNullOrEmpty(_options.Name) ? "Strands Agents" : _options.Name!;
    /// <summary>The configured description, independent of the supplied tool agent.</summary>
    public string? Description => _options.Description;
    /// <summary>Detached conversation history.</summary>
    public IReadOnlyList<Message> Messages { get { lock (_gate) return Array.AsReadOnly(_messages.Select(Copy).ToArray()); } }
    /// <summary>Awaited lifecycle callbacks for this bidirectional conversation.</summary>
    public BidiHookRegistry Hooks { get; }
    /// <summary>Starts background model reading and keeps caller invocation state by reference.</summary>
    public async Task StartAsync(IReadOnlyDictionary<string, object?>? invocationState = null, CancellationToken cancellationToken = default)
        => await StartConversationAsync(invocationState, cancellationToken).ConfigureAwait(false);
    private async Task<Conversation> StartConversationAsync(IReadOnlyDictionary<string, object?>? invocationState, CancellationToken cancellationToken, bool run = false)
    {
        RejectCallbackLifecycle();
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previous = Scope.Value; Scope.Value = new(this, previous);
        try
        {
            lock (_gate) { ThrowIfDisposed(); if (_conversation is not null) throw new InvalidOperationException("Stop the current bidirectional conversation before starting again."); }
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await EnsureInitializedAsync(operation.Token).ConfigureAwait(false);
            await RepairRecoveredHistoryAsync(operation.Token).ConfigureAwait(false);
            await _options.ToolAgent.InitializeAsync(operation.Token).ConfigureAwait(false);
            var session = CaptureSession();
            var telemetry = StartBidiSession(session);
            var connect = StartBidiConnect(telemetry);
            var started = false;
            Conversation? conversation = null;
            try
            {
                await _options.Model.StartAsync(session, operation.Token).ConfigureAwait(false); started = true;
                connect.End();
                operation.Token.ThrowIfCancellationRequested();
                conversation = new(invocationState ?? new Dictionary<string, object?>(), _lifetime.Token)
                { Telemetry = telemetry, Receiver = run ? 1 : 0, RunDrain = run ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null };
                foreach (var call in session.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>()) conversation.ToolIds.Add(call.ToolUseId);
                lock (_gate)
                {
                    if (_disposed) throw new OperationCanceledException("The bidirectional agent was disposed while starting.", _lifetime.Token);
                    _conversation = conversation;
                }
                conversation.Reader = RunModelAsync(conversation);
            }
            catch (Exception error)
            {
                connect.End(error: BidiTraceError(error, operation.Token));
                Exception? primary = cancellationToken.IsCancellationRequested ? new OperationCanceledException("Starting the bidirectional conversation was cancelled.", error, cancellationToken) : error;
                try
                {
                    if (started)
                    {
                        conversation?.Lifetime.Dispose();
                        try { await _options.Model.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception cleanup) { Add(ref primary, cleanup); }
                    }
                }
                finally { EndBidiSession(telemetry, BidiTraceError(primary, operation.Token)); }
                ExceptionDispatchInfo.Capture(primary!).Throw(); throw;
            }
            return conversation;
        }
        finally { Scope.Value = previous; _lifecycle.Release(); }
    }
    /// <summary>Sends text/image content or live audio; tool results belong to the internal tool runner.</summary>
    public async Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default)
    {
        if (input is not (BidiAudioDelta or BidiMessage)) throw new ArgumentException("Expected a user message or live audio delta.", nameof(input));
        if (input is BidiMessage message && message.Content.Any(block => block is ToolResultBlock)) throw new ArgumentException("The bidirectional tool runner supplies tool results.", nameof(input));
        for (var scope = RestartHookScope.Value; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope.Owner, this)) throw new InvalidOperationException("A restart callback cannot await a send on its own gated connection.");
        for (var scope = SendScope.Value; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope.Owner, this)) throw new InvalidOperationException("A model send callback cannot recursively send on its own connection.");
        cancellationToken.ThrowIfCancellationRequested();
        var conversation = Current();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, conversation.Token);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            await SendModelAsync(conversation, input, true, operation.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("Bidirectional input was cancelled.", error, cancellationToken); }
    }
    /// <summary>Receives normalized model output, complete blocks, and actual tool execution updates.</summary>
    public async IAsyncEnumerable<IAgentStreamEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RejectCallbackWait("A bidirectional callback cannot await output from its own conversation.");
        var conversation = Current();
        if (Interlocked.CompareExchange(ref conversation.Receiver, 1, 0) != 0) throw new InvalidOperationException("Only one receiver can consume a bidirectional conversation.");
        try
        {
            while (await conversation.Output.ReadAsync(cancellationToken).ConfigureAwait(false) is { } output)
            {
                yield return output;
                if (output is BidiConnectionStopEvent { Reason: "user_request" }) yield break;
            }
        }
        finally { Interlocked.Exchange(ref conversation.Receiver, 0); }
    }
    /// <summary>Stops the conversation, cancelling and joining its active tools and model reader.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        RejectCallbackLifecycle();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previous = Scope.Value; Scope.Value = new(this, previous);
        try { await StopCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { Scope.Value = previous; _lifecycle.Release(); }
    }
    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Conversation? conversation = null;
        try
        {
            lock (_gate) { conversation = _conversation; _conversation = null; }
            if (conversation is null) return;
            Exception? primary = null;
            try { conversation.Lifetime.Cancel(); } catch (Exception error) { primary = error; }
            try
            {
                await conversation.ConnectionGate.WaitAsync().ConfigureAwait(false);
                try { await _options.Model.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { conversation.ConnectionGate.Release(); }
            }
            catch (Exception error) { Add(ref primary, error); }
            try { await conversation.Reader.ConfigureAwait(false); }
            catch (Exception error) { Add(ref primary, error); }
            foreach (var task in conversation.Tools) try { await task.ConfigureAwait(false); } catch (Exception error) { Add(ref primary, error); }
            if (conversation.RunDrain is { } drain) await drain.Task.ConfigureAwait(false);
            conversation.Output.Complete(); conversation.Lifetime.Dispose();
            EndBidiSession(conversation.Telemetry);
            try { await Hooks.InvokeAsync(new BidiAgentStopEvent(this), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { Add(ref primary, error); }
            try { await SaveSessionCoreAsync(CancellationToken.None, true).ConfigureAwait(false); }
            catch (Exception error) { Add(ref primary, error); }
            if (primary is null && cancellationToken.IsCancellationRequested) primary = new OperationCanceledException(cancellationToken);
            if (primary is null) conversation.Stopped.TrySetResult(true);
            else { conversation.Stopped.TrySetException(primary); _ = conversation.Stopped.Task.Exception; }
            if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        }
        finally { EndBidiSession(conversation?.Telemetry); ResetCancellationRequest(); }
    }
    /// <summary>Terminates the conversation and releases explicitly transferred resources.</summary>
    public ValueTask DisposeAsync()
    {
        RejectCallbackLifecycle();
        TaskCompletionSource<bool>? completion = null;
        lock (_gate)
        {
            if (_disposal is null) { _disposed = true; completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task; }
        }
        if (completion is not null) _ = DisposeCoreAsync(completion);
        return new(_disposal!);
    }
    private async Task DisposeCoreAsync(TaskCompletionSource<bool> completion)
    {
        var previous = Scope.Value; Scope.Value = new(this, previous);
        Exception? primary = null;
        try { _lifetime.Cancel(); } catch (Exception error) { primary = error; }
        // A first-use save may still need the lifecycle semaphore for initialization.
        Task sessionWrites;
        lock (_gate) sessionWrites = _explicitSaveDrain?.Task ?? Task.CompletedTask;
        await sessionWrites.ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            Task? initializing;
            lock (_gate) initializing = _initialization is { IsCompleted: false } pending ? pending : null;
            if (initializing is not null)
                try { await initializing.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception error) { Add(ref primary, error); }
            try { await StopCoreAsync(default).ConfigureAwait(false); } catch (Exception error) { Add(ref primary, error); }
            if (_options.OwnsToolAgent) try { await _options.ToolAgent.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { Add(ref primary, error); }
            if (_options.OwnsModel) try { await _options.Model.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { Add(ref primary, error); }
        }
        finally { _lifecycle.Release(); _lifetime.Dispose(); ResetCancellationRequest(); Scope.Value = previous; }
        if (primary is null) completion.TrySetResult(true); else completion.TrySetException(primary);
    }
    private Conversation Current() { lock (_gate) { ThrowIfDisposed(); return _conversation ?? throw new InvalidOperationException("Start the bidirectional conversation before using it."); } }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(BidiAgent)); }
    private void RejectCallbackLifecycle()
        => RejectCallbackWait("A bidirectional agent cannot change its connection lifecycle from its own execution callback.");
    private void RejectCallbackWait(string message)
    {
        for (var scope = Scope.Value; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope.Owner, this)) throw new InvalidOperationException(message);
    }
    private static Message Copy(Message message) => message is null ? throw new ArgumentException("History contains a null message.") : new(message.Role, message.Content, message.TrackingId, message.Metadata);
    private static void Add(ref Exception? primary, Exception error)
    {
        if (primary is null) primary = error;
        else if (!ReferenceEquals(primary, error))
        {
            const string key = "Strands.CleanupErrors";
            if (primary.Data[key] is not List<Exception> failures) primary.Data[key] = failures = [];
            failures.Add(error);
        }
    }
    private static async Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var maximum = TimeSpan.FromMilliseconds(int.MaxValue);
        while (duration > maximum) { await Task.Delay(maximum, cancellationToken).ConfigureAwait(false); duration -= maximum; }
        await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
    }
    private sealed record CallbackScope(BidiAgent Owner, CallbackScope? Parent);
    private sealed class Conversation(IReadOnlyDictionary<string, object?> state, CancellationToken lifetime)
    {
        internal BidiTelemetry? Telemetry;
        internal IReadOnlyDictionary<string, object?> State { get; } = state;
        internal CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal CancellationToken Token => Lifetime.Token;
        internal BidiOutputQueue Output { get; } = new();
        internal Task Reader { get; set; } = Task.CompletedTask;
        internal TaskCompletionSource<bool>? RunDrain;
        internal TaskCompletionSource<bool> Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<Task> Tools { get; } = [];
        internal HashSet<string> ToolIds { get; } = new(StringComparer.Ordinal);
        internal HashSet<int> UnsentResultIndices { get; } = [];
        internal SemaphoreSlim ConnectionGate { get; } = new(1, 1);
        internal Task Ready { get; set; } = Task.CompletedTask;
        internal long Generation;
        internal string ConnectionId = "unknown";
        internal long ConnectionIdGeneration = -1;
        internal bool StopPublished;
        internal bool ResponseActive;
        internal bool AwaitingResponse;
        internal TaskCompletionSource<bool> TurnComplete { get; set; } = CompletedSignal();
        internal int Receiver;
    }
}
