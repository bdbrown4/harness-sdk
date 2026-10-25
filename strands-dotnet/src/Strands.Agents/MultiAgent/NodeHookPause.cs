using System.Text.Json;

namespace Strands.Agents.MultiAgent;

internal sealed record NodeHookPause
{
    public NodeHookPause(IReadOnlyList<Interrupt> interrupts, IReadOnlyList<InterruptResponse> responses)
    {
        Guard.NotNull(interrupts, nameof(interrupts)); Guard.NotNull(responses, nameof(responses));
        Interrupts = NodeResult.CopyInterrupts(interrupts);
        if (Interrupts.Count == 0 || Interrupts.Any(question => question.Source != InterruptSource.OrchestratorHook || question.IsBatch)
            || Interrupts.Select(question => question.Name).Distinct(StringComparer.Ordinal).Count() != Interrupts.Count)
            throw new ArgumentException("Node hook questions require distinct names and orchestration-hook correlation.", nameof(interrupts));
        Responses = Array.AsReadOnly(responses.Select(response => response is null ? throw new ArgumentException("Responses cannot be null.", nameof(responses)) : new InterruptResponse(response.InterruptId, response.Response)).ToArray());
        var ids = new HashSet<string>(Interrupts.Select(question => question.Id), StringComparer.Ordinal);
        if (Responses.Any(response => !ids.Contains(response.InterruptId)) || Responses.Select(response => response.InterruptId).Distinct(StringComparer.Ordinal).Count() != Responses.Count)
            throw new ArgumentException("Hook answers must uniquely target known questions.", nameof(responses));
    }
    public IReadOnlyList<Interrupt> Interrupts { get; }
    public IReadOnlyList<InterruptResponse> Responses { get; }
    internal IReadOnlyList<Interrupt> Unanswered => NodeResult.CopyInterrupts(Interrupts.Where(question => !Responses.Any(response => response.InterruptId == question.Id)).ToArray());
}

internal sealed class NodeHookScope(string nodeId, NodeState state, CancellationToken cancellationToken) : IDisposable
{
    private readonly HashSet<string> _requested = new(StringComparer.Ordinal);
    private int _active = 1;
    internal JsonElement Resolve(string name, JsonElement? reason, JsonElement? response)
    {
        if (Volatile.Read(ref _active) == 0) throw new InvalidOperationException("A node interrupt context cannot outlive its callback dispatch.");
        cancellationToken.ThrowIfCancellationRequested();
        Guard.NotNullOrWhiteSpace(name, nameof(name));
        if (!_requested.Add(name)) throw new InvalidOperationException("Node hook request names must be distinct within a callback dispatch.");
        var prior = state.HookPause;
        var question = prior?.Interrupts.FirstOrDefault(value => value.Name == name);
        if (question is not null && prior!.Responses.FirstOrDefault(value => value.InterruptId == question.Id) is { } accepted) return accepted.Response.Clone();
        if (response.HasValue) return InterruptJson.Copy(response, nameof(response))!.Value;
        question ??= new(Guid.NewGuid().ToString("N"), name, nodeId, reason, InterruptSource.OrchestratorHook);
        if (prior is null || !prior.Interrupts.Any(value => value.Id == question.Id)) state.HookPause = new((prior?.Interrupts ?? []).Concat([question]).ToArray(), prior?.Responses ?? []);
        throw new InterruptException(question);
    }
    public void Dispose() => Volatile.Write(ref _active, 0);
}
