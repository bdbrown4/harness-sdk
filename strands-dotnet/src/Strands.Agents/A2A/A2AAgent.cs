using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>An invokable remote A2A agent using the real v0.3 HTTP protocol.</summary>
public sealed class A2AAgent
{
    private readonly A2AClient _client;
    private readonly A2AAgentOptions _options;
    private string? _name, _description;
    /// <summary>Creates an adapter borrowing the client and its transport.</summary>
    public A2AAgent(A2AClient client, A2AAgentOptions? options = null)
    {
        Guard.NotNull(client, nameof(client));
        _client = client; _options = options ?? new();
        if (!Enum.IsDefined(typeof(A2AContentMode), _options.ContentMode) || !Enum.IsDefined(typeof(A2ATaskStateMapping), _options.TaskStateMapping)) throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.Id is not null) Guard.NotNullOrWhiteSpace(_options.Id, nameof(options));
        if (_options.MaxResultBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxResultBytes must be positive.");
        Id = _options.Id ?? client.DiscoveryUrl.AbsoluteUri;
        _name = _options.Name; _description = _options.Description;
    }
    /// <summary>The explicit identity or discovery URL.</summary>
    public string Id { get; }
    /// <summary>The explicit name or discovered card name, null until discovery when omitted.</summary>
    public string? Name => Volatile.Read(ref _name);
    /// <summary>The explicit description or discovered card description, null until discovery when omitted.</summary>
    public string? Description => Volatile.Read(ref _description);
    /// <summary>Discovers the full card and populates omitted display metadata.</summary>
    public async Task<JsonElement> GetAgentCardAsync(CancellationToken cancellationToken = default)
    {
        var card = await _client.GetAgentCardAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.CompareExchange(ref _name, A2AJson.RequiredString(card, "name"), null);
        Interlocked.CompareExchange(ref _description, A2AJson.RequiredString(card, "description"), null);
        return card;
    }
    /// <summary>Sends a text prompt and consumes the complete remote stream.</summary>
    public Task<AgentResult> InvokeAsync(string prompt, A2AInvocationOptions? options = null, CancellationToken cancellationToken = default)
        => ConsumeAsync(StreamAsync(prompt, options, cancellationToken), cancellationToken);
    /// <summary>Sends supported detached typed content and consumes the complete remote stream.</summary>
    public Task<AgentResult> InvokeContentAsync(IReadOnlyList<ContentBlock> content, A2AInvocationOptions? options = null, CancellationToken cancellationToken = default)
        => ConsumeAsync(StreamContentAsync(content, options, cancellationToken), cancellationToken);
    /// <summary>Streams the remote exchange followed by exactly one terminal A2AResultEvent.</summary>
    public IAsyncEnumerable<IAgentStreamEvent> StreamAsync(string prompt, A2AInvocationOptions? options = null, CancellationToken cancellationToken = default)
    { Guard.NotNull(prompt, nameof(prompt)); return StreamContentAsync([new TextBlock(prompt)], options, cancellationToken); }
    /// <summary>Copies typed input and protocol extensions before discovery; caller invocation state stays local.</summary>
    public IAsyncEnumerable<IAgentStreamEvent> StreamContentAsync(IReadOnlyList<ContentBlock> content, A2AInvocationOptions? options = null, CancellationToken cancellationToken = default)
        => PrepareStream(content, options, _options.ContentMode, cancellationToken);
    internal IAsyncEnumerable<IAgentStreamEvent> StreamProtocolContentAsync(IReadOnlyList<ContentBlock> content, A2AInvocationOptions options, CancellationToken cancellationToken)
        => PrepareStream(content, options, A2AContentMode.Rich, cancellationToken);
    private IAsyncEnumerable<IAgentStreamEvent> PrepareStream(IReadOnlyList<ContentBlock> content, A2AInvocationOptions? options, A2AContentMode inputMode, CancellationToken cancellationToken)
    {
        Guard.NotNull(content, nameof(content));
        var selected = options ?? new();
        Guard.NotNull(selected.InvocationState, nameof(options));
        foreach (var value in new[] { selected.ContextId, selected.TaskId, selected.MessageId }) if (value is not null) Guard.NotNullOrWhiteSpace(value, nameof(options));
        foreach (var value in new[] { selected.Configuration, selected.Metadata }) if (value is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("Protocol configuration and metadata must be objects.", nameof(options));
        var parts = A2AParts.ToParts(new Message(Role.User, content).Content, inputMode);
        if (parts.Count == 0) throw new ArgumentException("A2A input requires at least one supported content part.", nameof(content));
        var message = new Dictionary<string, object?> { ["kind"] = "message", ["messageId"] = selected.MessageId ?? Guid.NewGuid().ToString("N"), ["role"] = "user", ["parts"] = parts };
        if (selected.ContextId is { } contextId) message["contextId"] = contextId;
        if (selected.TaskId is { } taskId) message["taskId"] = taskId;
        var parameters = new Dictionary<string, object?> { ["message"] = message };
        if (selected.Configuration is { } configuration) parameters["configuration"] = configuration.Clone();
        if (selected.Metadata is { } metadata) parameters["metadata"] = metadata.Clone();
        return StreamCoreAsync(JsonSerializer.SerializeToElement(parameters), selected.InvocationState, cancellationToken);
    }
    private async IAsyncEnumerable<IAgentStreamEvent> StreamCoreAsync(JsonElement parameters, IReadOnlyDictionary<string, object?> invocationState, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var card = await GetAgentCardAsync(cancellationToken).ConfigureAwait(false);
        var accumulator = new A2AResultAccumulator(_options);
        if (_options.Streaming && A2AJson.OptionalBoolean(A2AJson.RequiredObject(card, "capabilities"), "streaming"))
        {
            var iterator = _client.StreamCallAsync("message/stream", parameters, cancellationToken).GetAsyncEnumerator(cancellationToken);
            Exception? primary = null;
            try
            {
                while (true)
                {
                    JsonElement value;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!await iterator.MoveNextAsync().ConfigureAwait(false)) break;
                        value = iterator.Current;
                        ValidateContinuation(value, parameters);
                        accumulator.Observe(value);
                    }
                    catch (Exception error) { primary = error; throw; }
                    yield return new A2AStreamUpdateEvent(value);
                }
            }
            finally
            {
                try { await iterator.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { if (primary is null) throw; A2AHttp.AddCleanup(primary, cleanup); }
            }
        }
        else
        {
            var value = await _client.CallAsync("message/send", parameters, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _ = new A2AStreamState().Observe(value);
            ValidateContinuation(value, parameters);
            accumulator.Observe(value);
            yield return new A2AStreamUpdateEvent(value);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var terminal = accumulator.Build(invocationState);
        cancellationToken.ThrowIfCancellationRequested();
        yield return terminal;
    }
    private static void ValidateContinuation(JsonElement response, JsonElement parameters)
    {
        var message = A2AJson.RequiredObject(parameters, "message");
        if (A2AJson.OptionalString(message, "contextId") is { } context && A2AJson.OptionalString(response, "contextId") != context)
            throw new A2AProtocolException("The A2A response context does not match the requested continuation.");
        if (A2AJson.OptionalString(message, "taskId") is not { } task) return;
        var field = A2AJson.RequiredString(response, "kind") == "task" ? "id" : "taskId";
        if (A2AJson.OptionalString(response, field) != task) throw new A2AProtocolException("The A2A response task does not match the requested continuation.");
    }
    private static async Task<AgentResult> ConsumeAsync(IAsyncEnumerable<IAgentStreamEvent> stream, CancellationToken cancellationToken)
    {
        AgentResult? result = null;
        await foreach (var value in stream.WithCancellation(cancellationToken).ConfigureAwait(false)) if (value is A2AResultEvent final) result = final.Result;
        cancellationToken.ThrowIfCancellationRequested();
        return result ?? throw new A2AProtocolException("The remote invocation did not produce a terminal result.");
    }
}
