namespace Strands.Agents;

public sealed partial class Agent
{
    private readonly Interventions.InterventionRegistry _interventions;
    /// <summary>The ordered intervention handlers inherited by default children. Instances remain caller-owned.</summary>
    public IReadOnlyList<Interventions.InterventionHandler> Interventions => _interventions.Handlers;

    internal async ValueTask<Message> AppendInterventionGuidanceAsync(string feedback, IReadOnlyDictionary<string, object?> invocationState, CancellationToken cancellationToken)
    {
        var message = Message.User(feedback);
        if (_options.GuideFeedbackDelivery == GuideFeedbackDelivery.DirectAppend)
        {
            var repositoryAppend = PrepareRepositoryAppend(new[] { message });
            lock (_historyGate) _messages.Add(message);
            MarkAcceptedHistory(CancellationToken.None, Volatile.Read(ref _invocationCancellation));
            repositoryAppend?.Accept();
            return message;
        }
        await AddMessageAsync(message, invocationState, cancellationToken).ConfigureAwait(false);
        return message;
    }
}
