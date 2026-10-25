using System.Diagnostics;
using System.Globalization;

namespace Strands.Agents.Telemetry;

/// <summary>Collects local metrics and emits native .NET measurements without configuring a global exporter.</summary>
public sealed class AgentMeter
{
    /// <summary>The native .NET meter name applications can subscribe to or export.</summary>
    public const string MeterName = "Strands.Agents";
    private readonly object _gate = new();
    private readonly int? _maximumInvocationHistory;
    private readonly ContextSizePolicy _contextSizePolicy;
    private readonly MetricEmitter _emitter;
    private readonly List<InvocationState> _invocations = [];
    private readonly Dictionary<string, ToolMetrics> _tools = new(StringComparer.Ordinal);
    private long _cycleCount;
    private Usage _usage = new();
    private TimeSpan _latency;
    private TimeSpan _duration;
    private long? _latestContextSize;
    private long? _projectedContextSize;

    /// <summary>Creates a collector with bounded or unbounded invocation history.</summary>
    /// <param name="maximumInvocationHistory">Retained invocations; defaults to TypeScript's fifty, or null for unbounded Python-style history.</param>
    /// <param name="meter">A borrowed application meter, or null for the shared Strands meter. The collector never disposes it.</param>
    public AgentMeter(int? maximumInvocationHistory = 50, System.Diagnostics.Metrics.Meter? meter = null)
        : this(maximumInvocationHistory, meter, ContextSizePolicy.LastReportedUsage) { }

    /// <summary>Creates a collector with explicit context-size selection.</summary>
    /// <param name="maximumInvocationHistory">Retained invocations, or null for unbounded history.</param>
    /// <param name="meter">A borrowed application meter, or null for the shared Strands meter.</param>
    /// <param name="contextSizePolicy">The source of the context-size estimates.</param>
    public AgentMeter(int? maximumInvocationHistory, System.Diagnostics.Metrics.Meter? meter, ContextSizePolicy contextSizePolicy)
    {
        if (maximumInvocationHistory is <= 0) throw new ArgumentOutOfRangeException(nameof(maximumInvocationHistory));
        if (!Enum.IsDefined(typeof(ContextSizePolicy), contextSizePolicy)) throw new ArgumentOutOfRangeException(nameof(contextSizePolicy));
        _maximumInvocationHistory = maximumInvocationHistory;
        _contextSizePolicy = contextSizePolicy;
        _emitter = new MetricEmitter(meter);
    }

    /// <summary>Begins a new invocation without resetting lifetime totals.</summary>
    public void StartNewInvocation()
    {
        lock (_gate)
        {
            if (_maximumInvocationHistory is { } maximum && _invocations.Count >= maximum) _invocations.RemoveAt(0);
            _invocations.Add(new InvocationState());
        }
        _emitter.InvocationStarted();
    }

    /// <summary>Starts a cycle associated with the current invocation, if any.</summary>
    /// <returns>A handle that may be completed out of order.</returns>
    public AgentCycle StartCycle()
    {
        CycleState state;
        lock (_gate)
        {
            var count = checked(_cycleCount + 1);
            var invocation = LatestInvocation;
            state = new CycleState("cycle-" + count.ToString(CultureInfo.InvariantCulture), invocation);
            invocation?.Cycles.Add(state);
            _cycleCount = count;
        }
        _emitter.CycleStarted(state.Id);
        return new AgentCycle(this, state);
    }

    /// <summary>Adds usage and timing to the newest cycle, or only to lifetime totals if no cycle exists.</summary>
    /// <param name="usage">Additive provider usage, when available.</param>
    /// <param name="metrics">Provider timing, when available.</param>
    public void UpdateCycle(Usage? usage = null, ModelPerformanceMetrics? metrics = null) => Update(null, usage, metrics);

    /// <summary>Records a completed tool call.</summary>
    /// <param name="tool">The tool request, whose JSON data is copied.</param>
    /// <param name="duration">Nonnegative execution time.</param>
    /// <param name="success">Whether execution succeeded.</param>
    public void RecordToolCall(ToolUseBlock tool, TimeSpan duration, bool success)
    {
        Guard.NotNull(tool, nameof(tool));
        Guard.NotNullOrWhiteSpace(tool.Name, nameof(tool));
        Guard.NotNullOrWhiteSpace(tool.ToolUseId, nameof(tool));
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        var detached = tool with { Input = tool.Input.Clone(), ProviderMetadata = tool.ProviderMetadata?.Clone() };
        lock (_gate)
        {
            _tools.TryGetValue(tool.Name, out var prior);
            var count = checked((prior?.CallCount ?? 0) + 1);
            var successes = checked((prior?.SuccessCount ?? 0) + (success ? 1 : 0));
            var errors = checked((prior?.ErrorCount ?? 0) + (success ? 0 : 1));
            var totalTime = (prior?.TotalTime ?? TimeSpan.Zero) + duration;
            _tools[tool.Name] = new ToolMetrics(count, successes, errors, totalTime, detached);
        }
        _emitter.ToolCompleted(detached, duration, success);
    }

    /// <summary>A detached snapshot that remains unchanged as collection continues.</summary>
    public AgentMetrics Metrics
    {
        get
        {
            lock (_gate)
            {
                var history = _invocations.Select(invocation => new AgentInvocationMetrics(
                    invocation.Cycles.Select(cycle => new AgentLoopMetrics(cycle.Id, cycle.Duration, cycle.Usage)).ToArray(),
                    invocation.Usage)).ToArray();
                var context = _latestContextSize;
                var projected = _projectedContextSize;
                if (_contextSizePolicy == ContextSizePolicy.LatestInvocationCycle)
                {
                    var invocation = LatestInvocation;
                    var latest = invocation is { Cycles.Count: > 0 } ? invocation.Cycles[invocation.Cycles.Count - 1] : null;
                    context = latest is null ? null : TotalPromptTokens(latest.Usage);
                    projected = latest is null ? null : checked(context + latest.Usage.OutputTokens);
                }
                return new(_cycleCount, _usage, new(_latency), history, _tools, context, projected, _duration);
            }
        }
    }

    private InvocationState? LatestInvocation => _invocations.Count == 0 ? null : _invocations[_invocations.Count - 1];

    internal void Update(CycleState? state, Usage? usage, ModelPerformanceMetrics? metrics)
    {
        if (usage is not null && (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.CacheReadInputTokens < 0 ||
            usage.CacheWriteInputTokens < 0 || usage.ReportedTotalTokens < 0)) throw new ArgumentOutOfRangeException(nameof(usage));
        if (metrics is not null && (metrics.Latency < TimeSpan.Zero || metrics.TimeToFirstToken < TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(metrics));
        lock (_gate)
        {
            var invocation = state is null ? LatestInvocation : state.Invocation;
            if (state is null && invocation is not null && invocation.Cycles.Count != 0) state = invocation.Cycles[invocation.Cycles.Count - 1];
            // Compute every checked value before committing any part of the update.
            var accumulated = usage is null ? _usage : _usage + usage;
            var invocationUsage = usage is null || invocation is null ? null : invocation.Usage + usage;
            var cycleUsage = usage is null || state is null ? null : state.Usage + usage;
            var latency = _latency + (metrics?.Latency ?? TimeSpan.Zero);
            var context = usage is null ? _latestContextSize : TotalPromptTokens(usage);
            var projected = usage is null ? _projectedContextSize : checked(context + usage.OutputTokens);
            _usage = accumulated;
            if (invocationUsage is not null) invocation!.Usage = invocationUsage;
            if (cycleUsage is not null) state!.Usage = cycleUsage;
            _latency = latency;
            _latestContextSize = context;
            _projectedContextSize = projected;
        }
        _emitter.ModelUpdated(usage, metrics);
    }

    private static long TotalPromptTokens(Usage usage)
    {
        checked
        {
            return usage.InputTokens + usage.OutputTokens == usage.TotalTokens ? usage.InputTokens :
                usage.InputTokens + usage.CacheReadInputTokens + usage.CacheWriteInputTokens;
        }
    }

    internal void End(CycleState state, TimeSpan? duration)
    {
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        TimeSpan elapsed;
        lock (_gate)
        {
            if (state.Ended) return;
            elapsed = duration ?? TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - state.StartTimestamp) / Stopwatch.Frequency);
            var total = _duration + elapsed;
            state.Duration = elapsed;
            state.Ended = true;
            _duration = total;
        }
        _emitter.CycleCompleted(state.Id, elapsed);
    }

    internal sealed class InvocationState
    {
        internal List<CycleState> Cycles { get; } = [];
        internal Usage Usage { get; set; } = new();
    }

    internal sealed class CycleState
    {
        internal CycleState(string id, InvocationState? invocation)
        {
            Id = id;
            Invocation = invocation;
        }
        internal string Id { get; }
        internal InvocationState? Invocation { get; }
        internal long StartTimestamp { get; } = Stopwatch.GetTimestamp();
        internal Usage Usage { get; set; } = new();
        internal TimeSpan Duration { get; set; }
        internal bool Ended { get; set; }
    }
}
