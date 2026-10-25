using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Strands.Agents.Mcp;

// Observe the existing reader before the official session deliberately yields each message.
// This forwards one reader/send/disposal path; it creates no pump or RPC correlation table.
internal sealed class ToolChangeTransport(IClientTransport transport, ToolChangeWireState changes) : IClientTransport
{
    public string Name => transport.Name;
    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
        new Connection(await transport.ConnectAsync(cancellationToken).ConfigureAwait(false), changes);

    private sealed class Connection : ITransport
    {
        private readonly ITransport _transport;
        private readonly Lazy<ChannelReader<JsonRpcMessage>> _reader;
        internal Connection(ITransport transport, ToolChangeWireState changes)
        {
            _transport = transport;
            // Resolve the borrowed reader only after the official client owns this connection,
            // retaining its existing cleanup boundary if a custom transport's getter throws.
            _reader = new(() => new Reader(transport.MessageReader, changes));
        }
        public string? SessionId => _transport.SessionId;
        public ChannelReader<JsonRpcMessage> MessageReader => _reader.Value;
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => _transport.SendMessageAsync(message, cancellationToken);
        public ValueTask DisposeAsync() => _transport.DisposeAsync();
    }

    private sealed class Reader(ChannelReader<JsonRpcMessage> reader, ToolChangeWireState changes) : ChannelReader<JsonRpcMessage>
    {
        public override Task Completion => reader.Completion;
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => reader.WaitToReadAsync(cancellationToken);
        public override bool TryRead(out JsonRpcMessage item)
        {
            if (!reader.TryRead(out var message)) { item = null!; return false; }
            item = message;
            changes.Receiving(message);
            return true;
        }
    }
}

internal sealed class ToolChangeWireState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _subscriptions = new(StringComparer.Ordinal);

    internal Entry Register(string requestId)
    {
        lock (_gate)
        {
            var entry = new Entry(requestId);
            _subscriptions.Add(requestId, entry);
            return entry;
        }
    }

    internal void Remove(Entry entry)
    { lock (_gate) if (_subscriptions.TryGetValue(entry.Id, out var current) && ReferenceEquals(current, entry)) _subscriptions.Remove(entry.Id); }

    internal void Receiving(JsonRpcMessage message)
    {
        if (message is not JsonRpcNotification notification || notification.Method is not
            ("notifications/subscriptions/acknowledged" or "notifications/tools/list_changed" or "notifications/cancelled")) return;
        lock (_gate) if (_subscriptions.Count == 0) return;
        var observation = Read(notification);
        if (observation.Id is null) return;
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(observation.Id, out var entry)) return;
            var previous = entry.Current;
            if (previous.Failure is not null) return;
            Exception? failure = null;
            var acknowledged = previous.Acknowledged;
            if (notification.Method == "notifications/subscriptions/acknowledged")
            {
                if (acknowledged) failure = new InvalidDataException("MCP subscription acknowledgement was repeated.");
                else if (!observation.Granted)
                    failure = new InvalidDataException("MCP server did not grant tools/list_changed notifications.");
                else acknowledged = true;
            }
            else if (notification.Method == "notifications/tools/list_changed" && !acknowledged)
                failure = new InvalidDataException("MCP tool notification arrived before its wire acknowledgement.");
            else if (notification.Method == "notifications/cancelled")
                failure = new OperationCanceledException("MCP server cancelled the tool-change subscription.");
            entry.Set(new(acknowledged, failure));
        }
    }

    // Read only built-in scalar values. Serializing a custom JsonValue can execute its
    // borrowed converter on the official pump, so unknown/custom values are malformed.
    internal static Observation Read(JsonRpcNotification notification)
    {
        if (notification.Params is not JsonObject parameters) return new(null, false);
        JsonNode? id;
        if (notification.Method == "notifications/cancelled") parameters.TryGetPropertyValue("requestId", out id);
        else id = parameters["_meta"] is JsonObject metadata ? metadata["io.modelcontextprotocol/subscriptionId"] : null;
        var typedId = id is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var granted = parameters["notifications"] is JsonObject notifications &&
            notifications["toolsListChanged"] is JsonValue flag && flag.TryGetValue<bool>(out var enabled) && enabled;
        return new(typedId, granted);
    }

    internal sealed record Observation(string? Id, bool Granted);

    internal sealed class Entry(string id)
    {
        private Snapshot _current = new(false, null);
        internal string Id { get; } = id;
        internal Snapshot Current => Volatile.Read(ref _current);
        internal void Set(Snapshot snapshot) => Volatile.Write(ref _current, snapshot);
    }

    internal sealed record Snapshot(bool Acknowledged, Exception? Failure);
}
