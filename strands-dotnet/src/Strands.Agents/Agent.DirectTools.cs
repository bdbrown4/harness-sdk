using System.Runtime.ExceptionServices;
using System.Text.Json;
using Strands.Agents.SchemaImplementation;

namespace Strands.Agents;

public sealed partial class Agent
{
    private int _directToolCalls;
    /// <summary>Invokes one registered tool through before/after hooks without adding conversation messages. Human interruptions are refused; explicit preemptive answers remain supported. Direct calls copy the invocation-state dictionary and may run inside another tool. Explicit after-hook retries repeat the before hook and body; cancellation and failed cleanup prevent retries. This path does not run model middleware, tool middleware, or streamed update callbacks.</summary>
    public ValueTask<ToolResultBlock> InvokeToolAsync(ToolUseBlock toolUse, IReadOnlyDictionary<string, object?>? invocationState = null, CancellationToken cancellationToken = default)
        => InvokeDirectToolAsync(toolUse, invocationState, true, null, cancellationToken);

    internal ValueTask<ToolResultBlock> InvokeBidiToolAsync(ToolUseBlock toolUse, IReadOnlyDictionary<string, object?> invocationState,
        Func<ToolUseBlock, ToolStreamEvent, CancellationToken, ValueTask>? progress, CancellationToken cancellationToken)
        => InvokeDirectToolAsync(toolUse, invocationState, false, progress, cancellationToken);

    private async ValueTask<ToolResultBlock> InvokeDirectToolAsync(ToolUseBlock toolUse, IReadOnlyDictionary<string, object?>? invocationState,
        bool copyInvocationState, Func<ToolUseBlock, ToolStreamEvent, CancellationToken, ValueTask>? progress, CancellationToken cancellationToken, DirectToolCallOptions? directOptions = null)
    {
        Guard.NotNull(toolUse, nameof(toolUse));
        Guard.NotNullOrWhiteSpace(toolUse.Name, nameof(toolUse));
        Guard.NotNullOrWhiteSpace(toolUse.ToolUseId, nameof(toolUse));
        cancellationToken.ThrowIfCancellationRequested();
        var call = (ToolUseBlock)Message.CloneBlock(toolUse);
        if (directOptions is { Profile: DirectToolCallProfile.Python, RecordHistory: true } && call.Input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Python direct-tool recording requires object input.", nameof(toolUse));
        var state = copyInvocationState ? invocationState?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? new Dictionary<string, object?>()
            : invocationState ?? new Dictionary<string, object?>();
        lock (_initializationGate)
        {
            ThrowIfDisposed();
            if (_abortingInterrupts || _catalogOperation is not null) throw new ConcurrentInvocationException();
            if (_initialization is { IsCompleted: false }) throw new InvalidOperationException("Direct tools cannot run while agent initialization is active.");
            ValidateDirectToolHistoryAdmission(directOptions);
        }
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        Action? providerUpdateReport;
        DirectToolOperation? directOperation = null;
        lock (_initializationGate)
        {
            ThrowIfDisposed();
            if (_abortingInterrupts || _catalogOperation is not null) throw new ConcurrentInvocationException();
            ValidateDirectToolHistoryAdmission(directOptions);
            providerUpdateReport = PublishPendingProviderUpdatesUnderGate();
            _directToolCalls++;
            if (directOptions is not null) { directOperation = new(this, directOptions); _directToolOperations.Add(directOperation); }
        }
        if (directOperation is null) providerUpdateReport?.Invoke();
        Exception? directFailure = null;
        try
        {
            if (directOperation is not null) providerUpdateReport?.Invoke();
            if (copyInvocationState) call = call with { Name = ResolvePublicDirectToolName(call.Name) };
            using var scope = new DirectToolScope(call.Name);
            while (true)
            {
                ToolResultBlock result;
                OperationCanceledException? cancellation = null;
                Exception? bodyError = null;
                ITool? attemptedTool = null;
                JsonElement? recordingSchema = null;
                var before = new BeforeToolCallEvent(this, call, state) { InterruptHandler = scope.Interrupt };
                try { await Hooks.InvokeAsync(before, cancellationToken).ConfigureAwait(false); }
                catch (DirectToolInterruptException) { }
                catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) { cancellation = error; }
                if (cancellation is not null || cancellationToken.IsCancellationRequested)
                {
                    cancellation ??= new OperationCanceledException(cancellationToken);
                    result = CancelledTool(call) with { Error = cancellation };
                }
                else
                {
                    call = (ToolUseBlock)Message.CloneBlock(before.ToolUse with { ToolUseId = toolUse.ToolUseId });
                    if (directOptions is { Profile: DirectToolCallProfile.Python, RecordHistory: true } &&
                        call.Name is not null && _tools.TryGetValue(call.Name, out var registered))
                        recordingSchema = RegisteredToolSpec(registered.Tool).InputSchema.Clone();
                    if (scope.Refusal is { } refusal) result = new(call.ToolUseId, refusal, ToolResultStatus.Error);
                    else if (before.CancelReason is { } denied) result = new(call.ToolUseId, denied, ToolResultStatus.Error);
                    else
                    {
                        var tool = before.SelectedTool ?? Tools.FirstOrDefault(value => RegisteredToolSpec(value).Name == call.Name);
                        attemptedTool = tool;
                        if (tool is null) result = new(call.ToolUseId, "Tool not found: " + call.Name + ".", ToolResultStatus.Error);
                        else if (!RegisteredToolSchema(tool).Evaluate(call.Input).IsValid)
                            result = new(call.ToolUseId, "Input does not match the schema for tool " + RegisteredToolSpec(tool).Name + ".", ToolResultStatus.Error);
                        else
                        {
                            try { result = await InvokeDirectToolBodyAsync(tool, call, state, scope, progress, cancellationToken).ConfigureAwait(false); }
                            catch (Exception pipelineFailure) when (progress is not null) { bodyError = pipelineFailure; result = new(call.ToolUseId, pipelineFailure.Message, ToolResultStatus.Error) { Error = pipelineFailure }; }
                        }
                        if (result.Error is OperationCanceledException error && cancellationToken.IsCancellationRequested) cancellation = error;
                    }
                }
                var after = new AfterToolCallEvent(this, call, result, state);
                try { await Hooks.InvokeAsync(after, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanup) when (cancellation is not null || bodyError is not null) { AttachCleanupError((Exception?)cancellation ?? bodyError!, cleanup); }
                if (bodyError is not null) ExceptionDispatchInfo.Capture(bodyError).Throw();
                if (cancellation is not null) ExceptionDispatchInfo.Capture(cancellation).Throw();
                if (scope.Refusal is null && ShouldRetryTool(after, result, cancellationToken))
                {
                    if (attemptedTool is IStructuredOutputAttempt structuredAttempt) structuredAttempt.DiscardResult(toolUse.ToolUseId);
                    continue;
                }
                var accepted = NormalizeToolResult(after.Result, toolUse.ToolUseId);
                if (scope.Refusal is { } finalRefusal) accepted = new(toolUse.ToolUseId, finalRefusal, ToolResultStatus.Error) { Error = result.Error, IteratorCleanupFailed = result.IteratorCleanupFailed };
                if (directOperation is not null)
                    await CompleteDirectToolAsync(directOperation, call, accepted, recordingSchema, state, cancellationToken).ConfigureAwait(false);
                return accepted;
            }
        }
        catch (Exception failure) { directFailure = failure; throw; }
        finally
        {
            Action? report;
            lock (_initializationGate)
            {
                if (directOperation is not null) _directToolOperations.Remove(directOperation);
                _directToolCalls--;
                report = PublishPendingProviderUpdatesUnderGate();
            }
            try { report?.Invoke(); }
            catch (Exception cleanup) when (directOperation is not null && directFailure is not null) { AttachDirectStreamCleanup(directFailure, cleanup); }
        }
    }

    private async ValueTask<ToolResultBlock> InvokeDirectToolBodyAsync(ITool tool, ToolUseBlock call, IReadOnlyDictionary<string, object?> state, DirectToolScope scope,
        Func<ToolUseBlock, ToolStreamEvent, CancellationToken, ValueTask>? progress, CancellationToken cancellationToken)
    {
        ToolResultBlock result;
        Exception? progressError = null;
        var iteratorCleanupFailed = false;
        var context = new ToolContext(this, call, state) { InterruptHandler = scope.Interrupt, AgentToolInterrupts = scope.AgentToolContext(), ExecutionCancelSignal = cancellationToken };
        try
        {
            if (tool is IStreamingTool streaming) result = await ToolStreamRunner.InvokeAsync(streaming, call.Input, context, progress is null ? null : ForwardProgressAsync, cancellationToken, () => iteratorCleanupFailed = true).ConfigureAwait(false);
            else if (tool is IRichTool rich) result = await rich.InvokeResultAsync(call.Input, context, cancellationToken).ConfigureAwait(false);
            else result = new(call.ToolUseId, await tool.InvokeAsync(call.Input, context, cancellationToken).ConfigureAwait(false));
            if (result.ToolUseId != call.ToolUseId) throw new InvalidOperationException("A rich tool returned a different tool-use identifier.");
            result = NormalizeToolResult(result, call.ToolUseId) with { IteratorCleanupFailed = iteratorCleanupFailed };
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) { return CancelledTool(call) with { Error = error, IteratorCleanupFailed = iteratorCleanupFailed }; }
        catch (Exception error) when (ReferenceEquals(error, progressError)) { throw; }
        catch (Exception error) { result = new(call.ToolUseId, error.Message, ToolResultStatus.Error) { Error = error, IteratorCleanupFailed = iteratorCleanupFailed }; }
        return scope.Refusal is { } refusal ? new(call.ToolUseId, refusal, ToolResultStatus.Error) { Error = result.Error, IteratorCleanupFailed = result.IteratorCleanupFailed } : result;

        async ValueTask ForwardProgressAsync(ToolStreamEvent update, CancellationToken token)
        {
            try { await progress!(call, update, token).ConfigureAwait(false); }
            catch (Exception error) { progressError = error; throw; }
        }
    }

    private sealed class DirectToolScope(string name) : IDisposable
    {
        private readonly object _gate = new();
        private bool _closed;
        private string? _refusal;
        private readonly Dictionary<string, JsonElement> _agentToolContext = new(StringComparer.Ordinal);
        internal AgentToolCallContext AgentToolContext() => new(_gate, _agentToolContext,
            () => { if (_closed) throw new InvalidOperationException("The direct tool call has completed."); },
            (question, _) => Interrupt(question.Name, question.Reason, null), () => false, () => { });
        public string? Refusal { get { lock (_gate) return _refusal; } }
        public JsonElement Interrupt(string approval, JsonElement? reason, JsonElement? response)
        {
            lock (_gate)
            {
                if (_closed) throw new InvalidOperationException("A direct tool interrupt context cannot outlive its call.");
                if (string.IsNullOrWhiteSpace(approval))
                {
                    _refusal ??= "Tool '" + name + "' supplied an invalid approval name and cannot prompt from a direct call.";
                    Guard.NotNullOrWhiteSpace(approval, nameof(approval));
                }
                if (response.HasValue)
                {
                    try { return InterruptJson.Copy(response, nameof(response))!.Value; }
                    catch { _refusal ??= "Tool '" + name + "' supplied an invalid approval response and cannot prompt from a direct call."; throw; }
                }
                // Establish the refusal before inspecting caller-owned JSON, whose document may already be disposed.
                _refusal ??= "Tool '" + name + "' needs approval, which a direct tool call cannot prompt for; call it as a normal model tool instead.";
                var detail = reason?.ToString() ?? approval;
                _refusal = "Tool '" + name + "' needs approval (" + detail + "), which a direct tool call cannot prompt for; call it as a normal model tool instead.";
                throw new DirectToolInterruptException();
            }
        }
        public void Dispose() { lock (_gate) _closed = true; }
    }
    private sealed class DirectToolInterruptException : InvalidOperationException;
}
