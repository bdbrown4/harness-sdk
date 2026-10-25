using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Strands.Agents.Goals;

/// <summary>Refines completed responses until a validator accepts them or an evaluation budget is exhausted.</summary>
public sealed class GoalLoop : IPlugin
{
    private static readonly ConditionalWeakTable<Agent, object> Attachments = new();
    private static readonly HashSet<string> UnboundedWarnings = new(StringComparer.Ordinal);
    private readonly GoalValidator? _validator;
    private readonly string? _goal;
    private readonly GoalLoopOptions _options;
    private readonly ConditionalWeakTable<Agent, RunState> _runs = new();
    /// <summary>Creates a goal evaluated by a fresh structured-output judge for every attempt.</summary>
    /// <param name="goal">The natural-language requirements.</param>
    /// <param name="options">Optional budgets and judge configuration.</param>
    public GoalLoop(string goal, GoalLoopOptions? options = null)
    {
        Guard.NotNull(goal, nameof(goal));
        _goal = goal;
        _options = ValidateOptions(options);
        Name = _options.Name;
    }

    /// <summary>Creates a goal evaluated by an application callback.</summary>
    /// <param name="validator">The awaited goal validator.</param>
    /// <param name="options">Optional budgets and retry configuration.</param>
    public GoalLoop(GoalValidator validator, GoalLoopOptions? options = null)
    {
        Guard.NotNull(validator, nameof(validator));
        _validator = validator;
        _options = ValidateOptions(options);
        Name = _options.Name;
    }

    /// <inheritdoc />
    public string Name { get; }

    private static GoalLoopOptions ValidateOptions(GoalLoopOptions? options)
    {
        var resolved = options ?? new();
        Guard.NotNullOrWhiteSpace(resolved.Name, nameof(options));
        if (resolved.MaxAttempts is <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxAttempts must be positive.");
        if (resolved.Timeout is { } timeout && timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive.");
        if (resolved.MaxAttempts is null && resolved.Timeout is null)
        {
            bool warn;
            lock (UnboundedWarnings) warn = UnboundedWarnings.Add(resolved.Name);
            if (warn) Warn("plugin=<" + resolved.Name + "> | execution is unbounded without maxAttempts or timeout");
        }
        return resolved;
    }

    /// <summary>Returns this agent's most recent completed goal run, or null before completion or while a new run is active.</summary>
    /// <param name="agent">The agent bound to this goal loop.</param>
    /// <returns>The completed evaluation summary, if any.</returns>
    public GoalResult? LastResult(Agent agent)
    {
        Guard.NotNull(agent, nameof(agent));
        return _runs.TryGetValue(agent, out var run) ? run.Result : null;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent));
        cancellationToken.ThrowIfCancellationRequested();
        lock (Attachments)
        {
            if (Attachments.TryGetValue(agent, out _))
                throw new InvalidOperationException("Only one GoalLoop can be attached to an agent. Compose requirements in one validator.");
            Attachments.Add(agent, new());
        }
        agent.Hooks.Register<BeforeInvocationEvent>((value, token) =>
        {
            if (_runs.TryGetValue(agent, out var previous) && previous.Result is null && ReferenceEquals(previous.InvocationIdentity, agent.GoalInvocationIdentity))
            {
                previous.Before = value;
            }
            else
            {
                _runs.Remove(agent);
                _runs.Add(agent, new(value, token, agent.GoalInvocationIdentity, _goal is null ? Array.Empty<Message>() : agent.Messages));
            }
            return default;
        });
        if (_goal is not null)
            agent.Hooks.Register<MessageAddedEvent>((value, _) =>
            {
                if (_runs.TryGetValue(agent, out var run) && run.Result is null) run.AppendTranscript(value.Message);
                return default;
            });
        if (!_options.PreserveContext)
            agent.Hooks.Register<BeforeModelCallEvent>((value, _) =>
            {
                if (_runs.TryGetValue(agent, out var run) && run.InitialSnapshot is null)
                {
                    run.InitialSnapshot = agent.CaptureGoalSnapshot(value);
                    if (_goal is not null) run.InitialTranscript = agent.Messages;
                }
                return default;
            });
        agent.Hooks.Register<AfterInvocationEvent>(AfterInvocationAsync);
        return default;
    }

    /// <inheritdoc />
    public IReadOnlyList<ITool> GetTools() => Array.Empty<ITool>();

    private async ValueTask AfterInvocationAsync(AfterInvocationEvent value, CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(value.Agent, out var run) || !value.ContinuationAllowed || value.Error is not null || value.Result?.Message is not { } response ||
            value.Result.StopReason is StopReasons.Cancelled or StopReasons.Interrupt || run.Token.IsCancellationRequested || run.Before.CancelReason is not null)
            return;
        if (_options.Timeout is { } timeout && run.Elapsed.Elapsed >= timeout)
        {
            FinishRun(run, false, GoalStopReason.Timeout);
            return;
        }
        ValidationOutcome outcome;
        try
        {
            outcome = _validator is { } validator
                ? await validator(new(response.Role, response.Content, response.TrackingId, response.Metadata), value.Agent, run.Token).ConfigureAwait(false)
                : await JudgeAsync(value.Agent, run).ConfigureAwait(false);
            if (outcome is null) throw new InvalidOperationException("The validator returned no outcome.");
            run.Token.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (!run.Token.IsCancellationRequested)
        {
            Warn("plugin=<" + Name + ">, error=<" + error.Message + "> | validator threw");
            outcome = new(false, "Validator error: " + error.Message);
        }
        run.Attempts.Add(new(run.Attempts.Count + 1, outcome.Passed, outcome.Feedback));
        if (outcome.Passed || run.Attempts.Count >= _options.MaxAttempts)
        {
            FinishRun(run, outcome.Passed, outcome.Passed ? GoalStopReason.Satisfied : GoalStopReason.MaxAttempts);
            return;
        }
        if (run.InitialSnapshot is { } initial)
        {
            value.Agent.RestoreGoalSnapshot(value, initial);
            if (run.InitialTranscript is { } transcript) run.ReplaceTranscript(transcript);
        }
        value.Resume = _options.ResumePromptTemplate?.Invoke(outcome.Feedback?.Trim()) ?? new(DefaultResumePrompt(outcome.Feedback?.Trim()));
    }

    private static string DefaultResumePrompt(string? feedback) => string.IsNullOrEmpty(feedback)
        ? "Your previous attempt did not satisfy the goal. Produce a new, corrected response that fully satisfies it — do not restate or lightly edit the previous attempt."
        : "Your previous attempt did not satisfy the goal.\n\nFeedback on what was wrong:\n" + feedback +
          "\n\nAddress every point above and produce a new, corrected response that fully satisfies the goal. Do not restate or lightly edit the previous attempt — fix the specific problems called out.";

    private async ValueTask<ValidationOutcome> JudgeAsync(Agent host, RunState run)
    {
        var judge = new Agent(new()
        {
            Model = _options.JudgeModel ?? host.AuxModel,
            SystemPrompt = _options.JudgeSystemPrompt ?? GoalJudge.SystemPrompt
        });
        await using var judgeScope = judge.ConfigureAwait(false);
        var messages = host.Messages;
        var result = await judge.InvokeStructuredAsync(GoalJudge.BuildPrompt(_goal!, messages.Count == 0 ? run.CopyTranscript() : messages),
            new StructuredOutputOptions<ValidationOutcome> { Schema = GoalJudge.OutcomeSchema }, cancellationToken: run.Token).ConfigureAwait(false);
        return result.Output;
    }

    private static void Warn(string message)
    {
        try { SdkLogging.Warning("{Message}", message); }
        catch (Exception) { }
    }

    private static void FinishRun(RunState run, bool passed, GoalStopReason stopReason)
    {
        run.Result = new(passed, stopReason, run.Attempts);
        run.InitialSnapshot = null;
        run.InitialTranscript = null;
        run.ReplaceTranscript(Array.Empty<Message>());
    }

    private sealed class RunState(BeforeInvocationEvent before, CancellationToken token, object invocationIdentity, IReadOnlyList<Message> transcript)
    {
        private readonly List<Message> _transcript = transcript.Select(CopyMessage).ToList();
        internal BeforeInvocationEvent Before = before;
        internal CancellationToken Token { get; } = token;
        internal List<GoalAttempt> Attempts { get; } = [];
        internal Stopwatch Elapsed { get; } = Stopwatch.StartNew();
        internal object InvocationIdentity { get; } = invocationIdentity;
        internal AgentSnapshot? InitialSnapshot;
        internal IReadOnlyList<Message>? InitialTranscript;
        internal GoalResult? Result;

        internal void AppendTranscript(Message message) { lock (_transcript) _transcript.Add(CopyMessage(message)); }
        internal IReadOnlyList<Message> CopyTranscript() { lock (_transcript) return _transcript.ToArray(); }
        internal void ReplaceTranscript(IReadOnlyList<Message> messages)
        {
            var detached = messages.Select(CopyMessage).ToArray();
            lock (_transcript) { _transcript.Clear(); _transcript.AddRange(detached); }
        }
        private static Message CopyMessage(Message message) => new(message.Role, message.Content, message.TrackingId, message.Metadata);
    }
}
