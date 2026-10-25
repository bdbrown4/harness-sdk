using System.Globalization;
using System.Text.Json;

namespace Strands.Agents.Interventions.Steering;

/// <summary>Retention settings for an agent-local tool activity ledger.</summary>
public sealed record ToolLedgerProviderOptions
{
    /// <summary>Maximum retained calls, defaulting to TypeScript's 100. Null selects Python's unbounded retention; zero retains none.</summary>
    public int? MaxEntries { get; init; } = 100;
    /// <summary>The provider's stable identifier.</summary>
    public string Name { get; init; } = "strands:steering:toolLedger";
}

/// <summary>Creates independent ledgers observing tool input, results, status, and timestamps.</summary>
public sealed class ToolLedgerProvider : ISteeringContextProvider
{
    private readonly int? _maximum;
    /// <summary>Copies and validates ledger retention settings.</summary>
    public ToolLedgerProvider(ToolLedgerProviderOptions? options = null)
    {
        options ??= new(); Guard.NotNullOrWhiteSpace(options.Name, nameof(options));
        if (options.MaxEntries is < 0) throw new ArgumentOutOfRangeException(nameof(options), "Ledger retention cannot be negative.");
        Name = options.Name; _maximum = options.MaxEntries;
    }
    /// <inheritdoc />
    public string Name { get; }
    /// <inheritdoc />
    public ValueTask<ISteeringContext> CreateAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent)); cancellationToken.ThrowIfCancellationRequested();
        return new((ISteeringContext)new Ledger(agent, _maximum));
    }

    private sealed class Ledger : ISteeringContext
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly object _gate = new();
        private readonly List<Dictionary<string, object?>> _calls = [];
        private readonly IDisposable _before;
        private readonly IDisposable _after;
        private readonly int? _maximum;
        private bool _disposed;
        internal Ledger(Agent agent, int? maximum)
        {
            _maximum = maximum;
            _before = agent.Hooks.Register<BeforeToolCallEvent>((value, _) => { Before(value); return default; });
            _after = agent.Hooks.Register<AfterToolCallEvent>((value, _) => { After(value); return default; });
        }
        public JsonElement Context
        {
            get
            {
                lock (_gate)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(ISteeringContext));
                    return JsonSerializer.SerializeToElement(new { type = "toolLedger", calls = _calls }, Json);
                }
            }
        }
        private void Before(BeforeToolCallEvent value)
        {
            var input = value.ToolUse.Input.Clone();
            lock (_gate)
            {
                if (_disposed) return;
                _calls.Add(new(StringComparer.Ordinal)
                {
                    ["startTime"] = Timestamp(),
                    ["id"] = value.ToolUse.ToolUseId,
                    ["name"] = value.ToolUse.Name,
                    ["args"] = input,
                    ["status"] = "pending"
                });
                if (_maximum is { } maximum && _calls.Count > maximum) _calls.RemoveRange(0, _calls.Count - maximum);
            }
        }
        private void After(AfterToolCallEvent value)
        {
            var blocks = value.Result.ContentBlocks.Count == 0 ? new List<ContentBlock> { new TextBlock(value.Result.Content) } : value.Result.ContentBlocks.ToList();
            if (value.Result.StructuredContent is { } structured && !blocks.OfType<JsonBlock>().Any(block => JsonElement.DeepEquals(block.Json, structured))) blocks.Add(new JsonBlock(structured));
            var result = JsonSerializer.SerializeToElement(blocks, Json);
            lock (_gate)
            {
                if (_disposed) return;
                var call = _calls.LastOrDefault(row => (string?)row["id"] == value.ToolUse.ToolUseId && (string?)row["status"] == "pending");
                if (call is null) return;
                call["endTime"] = Timestamp(); call["status"] = value.Result.Status == ToolResultStatus.Success ? "success" : "error";
                call["result"] = result; call["error"] = value.Result.Error?.Message;
            }
        }
        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_disposed) return default;
                _disposed = true; _calls.Clear();
            }
            _after.Dispose(); _before.Dispose(); return default;
        }
        private static string Timestamp() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }
}
