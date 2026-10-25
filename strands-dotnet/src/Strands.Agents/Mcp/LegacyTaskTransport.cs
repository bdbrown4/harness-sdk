using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Strands.Agents.Mcp;

// Official 2.2 deliberately omits the old tasks capability from its typed ServerCapabilities.
// Observe the single handshake before that conversion, without owning RPC correlation or a second receive pump.
internal sealed class LegacyTaskCapabilities
{
    private readonly object _gate = new();
    private RequestId? _initialize;
    private bool _calls;
    private bool _cancel;

    internal bool Supports(bool cancellation) { lock (_gate) return cancellation ? _cancel : _calls; }
    internal void Sending(JsonRpcMessage message)
    {
        if (message is JsonRpcRequest { Method: "initialize" } request)
            lock (_gate) _initialize = request.Id;
    }
    internal void Receiving(JsonRpcMessage message)
    {
        lock (_gate)
        {
            if (_initialize is not { } id || message is not JsonRpcResponse response || !response.Id.Equals(id)) return;
            _initialize = null;
            if (response.Result is not JsonObject result || result["capabilities"] is not JsonObject capabilities || capabilities["tasks"] is not JsonObject tasks) return;
            _cancel = tasks["cancel"] is JsonObject;
            _calls = tasks["requests"] is JsonObject requests && requests["tools"] is JsonObject tools && tools["call"] is JsonObject;
        }
    }
}

internal sealed class LegacyTaskTransport(IClientTransport transport, LegacyTaskCapabilities capabilities) : IClientTransport
{
    public string Name => transport.Name;
    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
        new Connection(await transport.ConnectAsync(cancellationToken).ConfigureAwait(false), capabilities);

    private sealed class Connection : ITransport
    {
        private readonly ITransport _transport;
        private readonly LegacyTaskCapabilities _capabilities;
        internal Connection(ITransport transport, LegacyTaskCapabilities capabilities)
        { _transport = transport; _capabilities = capabilities; MessageReader = new Reader(transport.MessageReader, capabilities); }
        public string? SessionId => _transport.SessionId;
        public ChannelReader<JsonRpcMessage> MessageReader { get; }
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        { _capabilities.Sending(message); return _transport.SendMessageAsync(message, cancellationToken); }
        public ValueTask DisposeAsync() => _transport.DisposeAsync();
    }

    private sealed class Reader(ChannelReader<JsonRpcMessage> reader, LegacyTaskCapabilities capabilities) : ChannelReader<JsonRpcMessage>
    {
        public override Task Completion => reader.Completion;
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => reader.WaitToReadAsync(cancellationToken);
        public override bool TryRead(out JsonRpcMessage item)
        {
            if (!reader.TryRead(out var message)) { item = null!; return false; }
            item = message;
            capabilities.Receiving(message);
            return true;
        }
    }
}
