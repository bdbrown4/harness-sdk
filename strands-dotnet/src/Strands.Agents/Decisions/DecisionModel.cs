using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Decisions;

/// <summary>Answers one or more closed questions over detached input state.</summary>
public abstract class DecisionModel
{
    /// <summary>Creates a custom decision model.</summary>
    protected DecisionModel() { }
    /// <summary>Asks closed questions over one user text message.</summary>
    /// <param name="state">The user text to evaluate.</param>
    /// <param name="questions">The question identifiers and allowed answer shapes.</param>
    /// <param name="cancellationToken">The caller's cancellation signal.</param>
    /// <returns>The answer map and operation accounting.</returns>
    public ValueTask<DecisionResult> AskAsync(string state, IReadOnlyDictionary<string, DecisionQuestion> questions, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(state, nameof(state));
        return AskMessagesAsync(new[] { Message.User(state) }, questions, cancellationToken);
    }
    /// <summary>Asks questions over one typed user message; an empty content list remains one empty user message.</summary>
    /// <param name="state">The ordered typed user content.</param>
    /// <param name="questions">The question identifiers and allowed answer shapes.</param>
    /// <param name="cancellationToken">The caller's cancellation signal.</param>
    /// <returns>The answer map and operation accounting.</returns>
    public ValueTask<DecisionResult> AskContentAsync(IReadOnlyList<ContentBlock> state, IReadOnlyDictionary<string, DecisionQuestion> questions, CancellationToken cancellationToken = default)
        => AskMessagesAsync(new[] { new Message(Role.User, state) }, questions, cancellationToken);
    /// <summary>Asks questions over a complete message history; an empty history sends no messages.</summary>
    /// <param name="state">The ordered conversation.</param>
    /// <param name="questions">The question identifiers and allowed answer shapes.</param>
    /// <param name="cancellationToken">The caller's cancellation signal.</param>
    /// <returns>The answer map and operation accounting.</returns>
    public ValueTask<DecisionResult> AskMessagesAsync(IReadOnlyList<Message> state, IReadOnlyDictionary<string, DecisionQuestion> questions, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard.NotNull(state, nameof(state)); Guard.NotNull(questions, nameof(questions));
        if (questions.Count == 0) throw new ArgumentException("A decision requires at least one question.", nameof(questions));
        var messages = Array.AsReadOnly(state.Select(message => message is null
            ? throw new ArgumentException("State cannot contain null messages.", nameof(state))
            : new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray());
        var copied = new ReadOnlyDictionary<string, DecisionQuestion>(questions.ToDictionary(pair => pair.Key, pair => CopyQuestion(pair.Value), StringComparer.Ordinal));
        return ExecuteAsync(messages, copied, cancellationToken);
    }

    private async ValueTask<DecisionResult> ExecuteAsync(IReadOnlyList<Message> messages, IReadOnlyDictionary<string, DecisionQuestion> questions, CancellationToken cancellationToken)
    {
        var tracer = new Tracer();
        using var span = tracer.StartSpan("decision", attributes: new Dictionary<string, object?> { ["gen_ai.operation.name"] = "decision" });
        RecordEvent(span, "gen_ai.decision.questions", "ids", questions.Keys.ToArray());
        try
        {
            // Starting the asynchronous operation inside the scope propagates its execution context without converting ValueTask-source cancellation to Task.
            var result = await span.WithContext(() => AskCoreAsync(messages, questions, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null) throw new InvalidOperationException("The decision implementation returned no result.");
            span.SetAttribute("gen_ai.decision.latency_ms", result.Metadata.Latency.TotalMilliseconds);
            RecordEvent(span, "gen_ai.decision.answers", "answers", JsonSerializer.Serialize(result.Answers));
            span.End(usage: result.Usage);
            return result;
        }
        catch (Exception error) { span.End(error: error); throw; }
    }

    private static void RecordEvent(TraceSpan span, string name, string key, object value)
    {
        try
        {
            if (span.Context is not { } context) return;
            span.WithContext(() =>
            {
                if (Activity.Current is { } activity && activity.Context.Equals(context))
                    activity.AddEvent(new ActivityEvent(name, tags: new ActivityTagsCollection { [key] = value }));
                return true;
            });
        }
        catch (Exception) { /* Observability is not part of the decision outcome. */ }
    }

    private static DecisionQuestion CopyQuestion(DecisionQuestion question)
    {
        if (question is null || string.IsNullOrWhiteSpace(question.Text))
            throw new ArgumentException("Each decision question must have nonempty text.", nameof(question));
        var choices = question.Choices?.ToArray();
        if (choices is not null && (choices.Length == 0 || choices.Any(string.IsNullOrEmpty) ||
            choices.Distinct(StringComparer.Ordinal).Count() != choices.Length || choices.Contains("uncertain", StringComparer.Ordinal)))
            throw new ArgumentException("Choices must be distinct nonempty strings; 'uncertain' is reserved.", nameof(question));
        if (question.MinConfidence is { } confidence && (!Guard.IsFinite(confidence) || confidence < 0 || confidence > 1))
            throw new ArgumentException("MinConfidence must be finite and in [0,1].", nameof(question));
        return question with { Choices = choices is null ? null : Array.AsReadOnly(choices) };
    }
    /// <summary>Implements one decision operation for validated questions and detached messages.</summary>
    /// <param name="state">The detached conversation.</param>
    /// <param name="questions">The validated, copied question map.</param>
    /// <param name="cancellationToken">The caller's cancellation signal.</param>
    /// <returns>A complete answer map and operation accounting.</returns>
    protected abstract ValueTask<DecisionResult> AskCoreAsync(IReadOnlyList<Message> state, IReadOnlyDictionary<string, DecisionQuestion> questions, CancellationToken cancellationToken);
}
