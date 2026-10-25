using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents;

public sealed partial class Agent
{
    internal static bool IsDelegationTool(ITool tool) => tool is AgentToolAdapter { Delegate: true };

    private void ValidateDelegationRegistration(IEnumerable<ITool> tools)
    {
        if (IsStateful && tools.Any(IsDelegationTool))
            throw new ArgumentException("Delegation tools cannot be registered with a stateful model because early exit would leave server-owned tool calls unacknowledged.", nameof(tools));
    }

    private bool IsRegisteredDelegation(string name, InvocationProgress progress) =>
        progress.EphemeralTools.TryGetValue(name, out var temporary) ? IsDelegationTool(temporary) : _tools.TryGetValue(name, out var registered) && IsDelegationTool(registered.Tool);

    private static IReadOnlyList<ContentBlock>? DelegationContent(ToolBatch batch)
    {
        if (batch.DelegationToolUseId is not { } id || batch.Results.OfType<ToolResultBlock>().SingleOrDefault(result => result.ToolUseId == id) is not { Status: ToolResultStatus.Success } result)
            return null;
        var content = result.ContentBlocks.Count > 0 ? result.ContentBlocks : new ContentBlock[] { new TextBlock(result.Content) };
        return content.Count > 0 && content.Any(block => block is not TextBlock { Text: "" }) ? content : null;
    }

    private static Message ValidateEndTurn(IReadOnlyList<ContentBlock> content)
    {
        var message = new Message(Role.Assistant, content);
        if (message.Content.Any(block => block is ToolUseBlock or ToolResultBlock or TextBlock { Text: null }))
            throw new ArgumentException("EndTurn requires assistant content without tool protocol blocks or null text.", nameof(content));
        return message;
    }

    /// <summary>Exposes this borrowed agent using source-compatible reset/preserve and delegation options. The input property is input; the legacy string overload retains its task schema and fresh-child behavior.</summary>
    public ITool AsTool(AgentToolOptions options)
    {
        Guard.NotNull(options, nameof(options));
        lock (_initializationGate)
        {
            ValidateSnapshotAccess();
            if (!options.PreserveContext && _options.SessionStore is not null)
                throw new ArgumentException("An agent with a session store requires PreserveContext=true when exposed through the typed tool options.", nameof(options));
            var name = options.Name ?? new string(Name.Take(64).Select(value => Guard.IsAsciiLetterOrDigit(value) || value is '_' or '-' ? value : '_').ToArray());
            var description = options.Description ?? Description ?? $"Use the {name} agent by providing a natural language input.";
            if (options.Delegate) description += " Calling this tool will return its response directly to the user as the final answer. It should be the only tool called in the turn.";
            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { input = new { type = "string", description = "The natural language input to send to the agent." } },
                required = new[] { "input" },
                additionalProperties = false
            });
            AgentSnapshot? baseline = null;
            if (!options.PreserveContext)
            {
                var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["messages"] = JsonSerializer.SerializeToElement(Messages, SnapshotJson),
                    ["state"] = JsonSerializer.SerializeToElement(State.GetAll(), SnapshotJson),
                    ["modelState"] = JsonSerializer.SerializeToElement(ModelState.GetAll(), SnapshotJson),
                    ["systemPrompt"] = SystemPromptContent is { } content ? JsonSerializer.SerializeToElement(content, SnapshotJson) : JsonSerializer.SerializeToElement(SystemPrompt, SnapshotJson),
                    ["interruptState"] = JsonSerializer.SerializeToElement(InterruptState, SnapshotJson)
                };
                if (UsesNativeStatefulHistory) data["modelHistoryCount"] = JsonSerializer.SerializeToElement(_modelHistoryCount);
                var inputReceipt = UsesNativeStatefulHistory ? _modelInputReceipt : null;
                if (inputReceipt is not null) data["modelInputReceipt"] = JsonSerializer.SerializeToElement(inputReceipt, SnapshotJson);
                baseline = new(data, schemaVersion: inputReceipt is null ? "1.0" : "2.0");
                if (inputReceipt is not null) _ = PrepareSnapshot(baseline);
            }
            return new AgentToolAdapter(this, new(name, description, schema), options.Delegate, baseline);
        }
    }

    // Harness owns this already-fresh child. Reuse the durable nested-call journal while
    // keeping its nullable prepared prompt and terminal error policy out of the public AsTool API.
    internal IStreamingTool AsFreshChildTool(ToolSpec specification, string? firstPrompt, Func<AgentResult, ToolContext, ToolResultBlock> mapResult)
    {
        Guard.NotNull(specification, nameof(specification));
        Guard.NotNull(mapResult, nameof(mapResult));
        lock (_initializationGate)
        {
            ValidateSnapshotAccess();
            return new AgentToolAdapter(this, specification, false, null, true, firstPrompt, mapResult, true);
        }
    }

    private sealed class AgentToolAdapter(Agent agent, ToolSpec specification, bool delegation, AgentSnapshot? baseline,
        bool fixedPrompt = false, string? firstPrompt = null, Func<AgentResult, ToolContext, ToolResultBlock>? mapResult = null,
        bool checkpointOnPause = false) : IStreamingTool, IRichTool
    {
        private int _busy;
        public ToolSpec Spec { get; } = specification;
        internal bool Delegate { get; } = delegation;
        public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            (await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false)).Content;
        public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            ToolStreamRunner.InvokeAsync(this, input, context, null, cancellationToken);

        public async IAsyncEnumerable<IToolStreamEvent> StreamAsync(JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                yield return new ToolStreamResultEvent(new(context.ToolUse.ToolUseId, $"Agent '{Spec.Name}' is already processing a request.", ToolResultStatus.Error));
                yield break;
            }
            try
            {
                AgentResult? result = null;
                IAsyncEnumerator<IAgentStreamEvent>? iterator = null;
                Exception? primary = null;
                try
                {
                    try
                    {
                        var resuming = context.AgentToolInterrupts?.HasForwarded == true;
                        if (resuming) context.AgentToolInterrupts!.RequireAnyAnswer();
                        var stored = context.AgentToolInterrupts?.GetTurn(context.ToolUse.ToolUseId);
                        IReadOnlyList<InterruptResponse>? answers = null;
                        if (stored is not null && resuming)
                        {
                            var pending = stored.Data["interruptState"].Deserialize<InterruptState>(AgentToolInterruptJournal.Json)!;
                            answers = CollectChildAnswers(context.AgentToolInterrupts!, pending);
                            if (answers.Count == 0) RaiseChildInterrupts(context.AgentToolInterrupts!, Unanswered(pending));
                        }
                        if (stored is not null)
                        {
                            try { await agent.LoadSnapshotAsync(stored, cancellationToken).ConfigureAwait(false); }
                            catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                            {
                                TryTrace(() => SdkLogging.Error(failure, "Agent '{Agent}' could not restore its interrupted turn: {Error}", Spec.Name, failure.Message));
                                var pending = stored.Data["interruptState"].Deserialize<InterruptState>(AgentToolInterruptJournal.Json)!;
                                var answered = new HashSet<string>(pending.Responses.Select(answer => answer.InterruptId), StringComparer.Ordinal);
                                context.AgentToolInterrupts!.Reopen(pending.Interrupts.Where(question => !answered.Contains(question.Id)).ToArray());
                                throw;
                            }
                        }
                        else if (baseline is not null) await agent.LoadSnapshotAsync(baseline, cancellationToken).ConfigureAwait(false);
                        else await agent.InitializeAsync(cancellationToken).ConfigureAwait(false);
                        var childState = agent.InterruptState;
                        if (resuming && childState is null) throw new InvalidOperationException($"Agent '{Spec.Name}' did NOT run and the human's response was NOT applied: its interrupted turn is not available. Do not report the requested action as completed; tell the user it failed and ask them to respond again. PreserveContext requires its own session to recover after restart.");
                        answers ??= resuming ? CollectChildAnswers(context.AgentToolInterrupts!, childState!) : Array.Empty<InterruptResponse>();
                        if (resuming && answers.Count == 0) RaiseChildInterrupts(context.AgentToolInterrupts!, Unanswered(childState!));
                        iterator = agent.StreamAsync(resuming ? null : fixedPrompt ? firstPrompt : input.GetProperty("input").GetString()!, new() { InvocationState = context.InvocationState, InterruptResponses = answers }, cancellationToken).GetAsyncEnumerator(cancellationToken);
                    }
                    catch (Exception failure) { primary = failure; throw; }
                    while (true)
                    {
                        bool moved;
                        try { moved = await iterator.MoveNextAsync().ConfigureAwait(false); }
                        catch (Exception failure) { primary = failure; throw; }
                        if (!moved) break;
                        if (iterator.Current is AgentResultEvent completed) result = completed.Result;
                        else if (iterator.Current is ToolStreamUpdateEvent progress) yield return progress.Event;
                        else yield return new ToolStreamEvent(iterator.Current);
                    }
                }
                finally
                {
                    if (iterator is not null)
                        try { await iterator.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception cleanup) { if (primary is null) throw; if (!ReferenceEquals(primary, cleanup)) AttachCleanupError(primary, cleanup); }
                }
                if (result is null) throw new InvalidOperationException("The wrapped agent did not produce a result.");
                if (result.StopReason == StopReasons.Interrupt)
                {
                    var journal = context.AgentToolInterrupts ?? throw new InvalidOperationException("Nested human input requires a live framework tool-call context.");
                    if (baseline is not null || checkpointOnPause)
                        journal.SetTurn(context.ToolUse.ToolUseId, await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session }, cancellationToken).ConfigureAwait(false));
                    else if (agent._options.SessionStore is null)
                        TryTrace(() => SdkLogging.Warning("Agent '{Agent}' interrupted with PreserveContext=true and no session store; it cannot recover its interrupted turn after restart.", Spec.Name));
                    RaiseChildInterrupts(journal, result.Interrupts);
                }
                context.AgentToolInterrupts?.SetTurn(context.ToolUse.ToolUseId, null);
                cancellationToken.ThrowIfCancellationRequested();
                if (mapResult is not null)
                {
                    yield return new ToolStreamResultEvent(mapResult(result, context));
                    yield break;
                }
                if (result.StopReason == StopReasons.Cancelled)
                {
                    yield return new ToolStreamResultEvent(new(context.ToolUse.ToolUseId, $"Agent '{Spec.Name}' cancelled.", ToolResultStatus.Error));
                    yield break;
                }
                var content = Delegate ? result.Message?.Content.Where(block => block is not ToolUseBlock and not ToolResultBlock).Select(Message.CloneBlock).ToArray() : null;
                yield return new ToolStreamResultEvent(content is { Length: > 0 }
                    ? new ToolResultBlock(context.ToolUse.ToolUseId, content)
                    : new ToolResultBlock(context.ToolUse.ToolUseId, result.ToString()));
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        private static IReadOnlyList<InterruptResponse> CollectChildAnswers(AgentToolCallContext journal, InterruptState child)
        {
            var answered = new HashSet<string>(child.Responses.Select(answer => answer.InterruptId), StringComparer.Ordinal);
            var responses = new List<InterruptResponse>();
            foreach (var question in child.Interrupts.Where(question => !answered.Contains(question.Id)))
                try { responses.Add(new(question.Id, journal.Resolve(question))); }
                catch (InterruptException) { /* Partial answers are accepted by the child before its next pause is forwarded. */ }
            return responses;
        }

        private static IReadOnlyList<Interrupt> Unanswered(InterruptState state)
        {
            var answered = new HashSet<string>(state.Responses.Select(answer => answer.InterruptId), StringComparer.Ordinal);
            return state.Interrupts.Where(question => !answered.Contains(question.Id)).ToArray();
        }

        private static void TryTrace(Action diagnostic)
        {
            try { diagnostic(); }
            catch (Exception) { /* Application diagnostics must not consume a human pause or its recovery state. */ }
        }

        private static void RaiseChildInterrupts(AgentToolCallContext journal, IReadOnlyList<Interrupt> questions)
        {
            var interruptions = new List<Interrupt>();
            foreach (var question in questions)
                try { journal.Resolve(question); }
                catch (InterruptException pause) { interruptions.AddRange(pause.Interrupts); }
            if (interruptions.Count == 0) throw new InvalidOperationException("The wrapped agent interrupted without an outstanding input request.");
            throw new InterruptException(interruptions);
        }
    }
}
