using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>Bridges isolated native agents into an awaited A2A task-event sink.</summary>
public sealed partial class A2AExecutor : IAsyncDisposable
{
    private readonly A2AExecutorOptions _options;
    private readonly A2AContextPool _contexts;
    private static readonly AsyncLocal<ExecutionScope?> Scope = new();
    private readonly object _disposalGate = new();
    private Task? _disposal;
    /// <summary>Configures factory ownership and bounded per-context caching without creating agents.</summary>
    public A2AExecutor(A2AExecutorOptions options)
    {
        Guard.NotNull(options, nameof(options)); Guard.NotNull(options.AgentFactory, nameof(options.AgentFactory));
        if (options.MaxContexts <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxContexts must be positive.");
        _options = options; _contexts = new(options);
    }
    /// <summary>Executes one message, preserving conversation state only within its explicit context.</summary>
    public async Task ExecuteAsync(A2ARequestContext context, IA2AEventSink sink, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context, nameof(context)); Guard.NotNull(sink, nameof(sink));
        if (ContainsScope(this)) throw new InvalidOperationException("An A2A executor cannot recursively execute itself.");
        var previous = Scope.Value; Scope.Value = new(this, previous);
        try
        {
            var responses = ReadInterruptResponses(context.Message);
            var content = responses is null ? A2AParts.FromParts(context.Message.GetProperty("parts").EnumerateArray(), A2AContentMode.Rich) : [];
            using var lease = await _contexts.AcquireAsync(context.ContextId, cancellationToken).ConfigureAwait(false);
            var agent = lease.Agent;
            BindRestoredPause(lease, context);
            ValidateResume(agent.InterruptState, lease.PendingTaskId, context.TaskId, responses);
            var options = _options.InvocationOptionsFactory?.Invoke(context) ?? new() { InvocationState = context.InvocationState };
            Guard.NotNull(options, nameof(options));
            if (options.InterruptResponses.Count != 0) throw new A2ARpcException(-32602, "Interrupt responses must be supplied by the protocol message.");
            if (responses is not null) options = options with { InterruptResponses = responses };
            await PublishAsync(sink, new { kind = "task", id = context.TaskId, contextId = context.ContextId, status = new { state = "working" } }, lease.CancellationToken).ConfigureAwait(false);
            try { await RunAgentAsync(agent, content, options, responses is not null, context, sink, lease.CancellationToken).ConfigureAwait(false); }
            finally { lease.PendingTaskId = agent.InterruptState is null ? null : context.TaskId; }
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested && error.CancellationToken != cancellationToken)
        { throw RestoreCallerCancellation(error, cancellationToken); }
        finally { Scope.Value = previous; }
    }
    private static async Task RunAgentAsync(Agent agent, IReadOnlyList<ContentBlock> content, InvocationOptions options, bool resume, A2ARequestContext context, IA2AEventSink sink, CancellationToken cancellationToken)
    {
        var artifactId = Guid.NewGuid().ToString("N");
        var streamed = false;
        AgentResult? result = null;
        Exception? primary = null;
        var stream = resume ? agent.StreamAsync(null, options, cancellationToken) : agent.StreamContentAsync(content, options, cancellationToken);
        var iterator = stream.GetAsyncEnumerator(cancellationToken);
        try
        {
            try
            {
                while (await iterator.MoveNextAsync().ConfigureAwait(false))
                {
                    var item = iterator.Current;
                    if (item is ModelStreamUpdateEvent { Event: ModelContentBlockDeltaEvent { Text: { } text } })
                    {
                        await PublishArtifactAsync(sink, context, artifactId, [new { kind = "text", text }], streamed, false, cancellationToken).ConfigureAwait(false);
                        streamed = true;
                    }
                    if (item is AgentResultEvent terminal) result = terminal.Result;
                    if (item is MessageAddedEvent { Message.Role: Role.Assistant } added)
                        foreach (var block in added.Message.Content.Where(IsRichOutput))
                            await PublishArtifactAsync(sink, context, Guid.NewGuid().ToString("N"), A2AParts.ToParts([block], A2AContentMode.Rich), false, true, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                try { await iterator.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { if (primary is null) throw; A2AHttp.AddCleanup(primary, cleanup); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null) throw new A2AProtocolException("The native agent produced no terminal result.");
            if (streamed || result.ToString().Length > 0)
                await PublishArtifactAsync(sink, context, artifactId, [new { kind = "text", text = streamed ? "" : result.ToString() }], streamed, true, cancellationToken).ConfigureAwait(false);
            if (result.StopReason == StopReasons.Interrupt) await PublishInputRequiredAsync(sink, context, result.Interrupts, cancellationToken).ConfigureAwait(false);
            else await PublishStatusAsync(sink, context, "completed", null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            try { await PublishStatusAsync(sink, context, error is OperationCanceledException && cancellationToken.IsCancellationRequested ? "canceled" : "failed", "Agent execution did not complete.", CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { if (!ReferenceEquals(error, cleanup)) A2AHttp.AddCleanup(error, cleanup); }
            throw;
        }
    }
    private static bool IsRichOutput(ContentBlock block) => block is ImageBlock or AudioBlock or VideoBlock or DocumentBlock or JsonBlock or ResourceLinkBlock or GuardContentBlock;
    private static ValueTask PublishArtifactAsync(IA2AEventSink sink, A2ARequestContext context, string artifactId, IReadOnlyList<object> parts, bool append, bool lastChunk, CancellationToken cancellationToken)
        => PublishAsync(sink, new { kind = "artifact-update", taskId = context.TaskId, contextId = context.ContextId, artifact = new { artifactId, parts }, append, lastChunk }, cancellationToken);
    private static ValueTask PublishStatusAsync(IA2AEventSink sink, A2ARequestContext context, string state, string? text, CancellationToken cancellationToken)
    {
        var status = new Dictionary<string, object?> { ["state"] = state };
        if (text is not null) status["message"] = new { kind = "message", messageId = Guid.NewGuid().ToString("N"), role = "agent", taskId = context.TaskId, contextId = context.ContextId, parts = new[] { new { kind = "text", text } } };
        return PublishAsync(sink, new { kind = "status-update", taskId = context.TaskId, contextId = context.ContextId, status, final = true }, cancellationToken);
    }
    private static ValueTask PublishAsync(IA2AEventSink sink, object value, CancellationToken cancellationToken)
        => sink.PublishAsync(JsonSerializer.SerializeToElement(value), cancellationToken);
    /// <summary>Stops execution and disposes transferred factory agents.</summary>
    public ValueTask DisposeAsync()
    {
        if (ContainsScope(this)) throw new InvalidOperationException("An A2A executor cannot be disposed from its own execution callbacks.");
        TaskCompletionSource<bool>? completion = null;
        lock (_disposalGate)
        {
            if (_disposal is null) { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task; }
        }
        if (completion is not null) _ = CompleteDisposalAsync(completion);
        return new(_disposal!);
    }
    private async Task CompleteDisposalAsync(TaskCompletionSource<bool> completion)
    {
        var previous = Scope.Value; Scope.Value = new(this, previous);
        try { await _contexts.DisposeAsync().ConfigureAwait(false); completion.TrySetResult(true); }
        catch (Exception error) { completion.TrySetException(error); }
        finally { Scope.Value = previous; }
    }
    private static bool ContainsScope(A2AExecutor owner)
    { for (var scope = Scope.Value; scope is not null; scope = scope.Parent) if (ReferenceEquals(scope.Owner, owner)) return true; return false; }
    private sealed record ExecutionScope(A2AExecutor Owner, ExecutionScope? Parent);
}
