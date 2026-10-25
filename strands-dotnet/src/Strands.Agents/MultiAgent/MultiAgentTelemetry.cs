using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Strands.Agents.Telemetry;

namespace Strands.Agents.MultiAgent;

internal sealed class MultiAgentTelemetry
{
    private readonly Tracer _tracer = new();
    private readonly TraceConventions _conventions = new();
    private readonly string _serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") is { Length: > 0 } name ? name : "strands-agents";
    private readonly MultiAgentTelemetryOptions _options;
    internal MultiAgentTelemetryOptions Options => CopyOptions(_options);

    internal MultiAgentTelemetry(MultiAgentTelemetryOptions options)
    {
        Guard.NotNull(options, nameof(options));
        if (!Enum.IsDefined(typeof(MultiAgentTelemetryProfile), options.Profile)) throw new ArgumentException("Unknown multi-agent telemetry profile.", nameof(options));
        _options = CopyOptions(options);
    }

    private static MultiAgentTelemetryOptions CopyOptions(MultiAgentTelemetryOptions options) => options with
    {
        TraceAttributes = options.TraceAttributes is { } attributes
                ? new ReadOnlyDictionary<string, object?>(attributes.ToDictionary(pair => pair.Key,
                    pair => pair.Value is Array array ? array.Clone() : pair.Value, StringComparer.Ordinal)) : null
    };

    internal async IAsyncEnumerable<IMultiAgentStreamEvent> ObserveAsync(string kind, string id, MultiAgentInput input,
        Func<Invocation, CancellationToken, IAsyncEnumerable<IMultiAgentStreamEvent>> stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var invocation = new Invocation(this, kind, id, input);
        var iterator = stream(invocation, cancellationToken).GetAsyncEnumerator(cancellationToken);
        Exception? primary = null;
        try
        {
            while (true)
            {
                bool moved;
                try { moved = await invocation.RunAsync(iterator.MoveNextAsync).ConfigureAwait(false); }
                catch (Exception error) { primary = error; throw; }
                if (!moved) break;
                yield return iterator.Current;
            }
        }
        finally
        {
            var failure = primary;
            try { await invocation.RunAsync(async () => { await iterator.DisposeAsync().ConfigureAwait(false); return true; }).ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                if (failure is null) failure = cleanup;
                else if (!ReferenceEquals(failure, cleanup))
                {
                    if (failure.Data["Strands.CleanupErrors"] is not List<Exception> errors) failure.Data["Strands.CleanupErrors"] = errors = [];
                    if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
                }
            }
            invocation.End(failure, cancellationToken);
            if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private Dictionary<string, object?> Attributes(string operation) => new(StringComparer.Ordinal)
    {
        ["gen_ai.operation.name"] = operation,
        [_conventions.Latest ? "gen_ai.provider.name" : "gen_ai.system"] = _serviceName
    };

    private void ApplyCustom(Dictionary<string, object?> attributes)
    {
        if (_options.TraceAttributes is { } custom) foreach (var entry in custom) attributes[entry.Key] = entry.Value;
    }

    internal sealed class Invocation(MultiAgentTelemetry owner, string kind, string id, MultiAgentInput input)
    {
        private TraceSpan? _span;
        internal MultiAgentState? State { get; set; }
        internal bool Python => owner._options.Profile == MultiAgentTelemetryProfile.Python;
        internal void Begin(bool afterBeforeHook = false)
        {
            if (_span is not null || Python != afterBeforeHook) return;
            var operation = "invoke_" + kind;
            var name = Python ? operation : operation + " " + id;
            var attributes = owner.Attributes(operation);
            if (!Python)
            {
                attributes["gen_ai.agent.name"] = kind;
                attributes["gen_ai.agent.id"] = id;
                attributes["name"] = name;
                try
                {
                    if (!input.IsTextInput || input.Text.Length > 0) attributes["gen_ai.agent.input"] = MultiAgentTraceContent.Serialize(input);
                }
                catch (Exception) { /* Observing a caller payload cannot prevent orchestration. */ }
            }
            owner.ApplyCustom(attributes);
            _span = owner._tracer.StartOperationSpan(name, Python ? ActivityKind.Client : ActivityKind.Internal, null, attributes);
            if (Python)
                try
                {
                    var value = owner._conventions.Content("gen_ai.input.messages", () => MultiAgentTraceContent.Serialize(input, python: true, latest: owner._conventions.Latest));
                    _span.RecordEvent(owner._conventions.Latest ? "gen_ai.client.inference.operation.details" : "gen_ai.user.message",
                        owner._conventions.Latest ? "gen_ai.input.messages" : "content", value,
                        owner._conventions.Latest && owner._conventions.AttributesOnly);
                }
                catch (Exception) { /* Observing a caller payload cannot prevent orchestration. */ }
        }

        internal ValueTask<T> RunAsync<T>(Func<ValueTask<T>> callback) => Python && _span is { } span ? span.WithValueTaskContextAsync(callback) : callback();
        internal T Run<T>(Func<T> callback) => Python && _span is { } span ? span.WithContext(callback) : callback();

        internal Visit BeginVisit(Node node)
        {
            TraceSpan? span = null;
            if (!Python)
                try
                {
                    var name = "node " + node.Id;
                    var attributes = owner.Attributes("execute_node");
                    attributes["gen_ai.agent.id"] = node.Id;
                    attributes["gen_ai.agent.node_type"] = node.Type;
                    attributes["name"] = name;
                    owner.ApplyCustom(attributes);
                    span = owner._tracer.StartOperationSpan(name, ActivityKind.Internal, _span, attributes);
                }
                catch (Exception) { /* A virtual node descriptor cannot make tracing fail execution. */ }
            return new(this, span);
        }

        internal void EndBeforeAfter(Exception? error, bool completed)
        {
            if (Python || _span is null) return;
            try
            {
                if (State is { } state) _span.SetAttribute("gen_ai.agent.execution_time", (DateTimeOffset.UtcNow - state.StartTime).TotalMilliseconds);
                Usage? usage = null;
                if (completed && State is { } complete)
                    try { usage = complete.Results.Aggregate(new Usage(), (sum, result) => sum + result.Usage); }
                    catch (Exception) { /* Optional accounting cannot mask an orchestration failure. */ }
                _span.End(usage: usage, error: error);
            }
            finally { _span.Dispose(); }
        }

        internal void End(Exception? error, CancellationToken caller)
        {
            if (_span is null) return;
            if (Python) _span.EndContext(error is OperationCanceledException && caller.IsCancellationRequested ? null : error);
            else EndBeforeAfter(error, false);
        }
    }

    internal sealed class Visit(Invocation invocation, TraceSpan? span) : IDisposable
    {
        private NodeResult? _result;
        private Exception? _error;
        private bool _hookGated;
        private bool _completed;
        internal T Run<T>(Func<T> callback) => span is not null ? span.WithContext(callback) : invocation.Run(callback);
        internal ValueTask<T> RunAsync<T>(Func<ValueTask<T>> callback) => span is not null ? span.WithValueTaskContextAsync(callback) : invocation.RunAsync(callback);
        internal void End(NodeResult result, Exception? error = null, bool hookCancelled = false)
        {
            DeferEnd(result, error, hookCancelled);
            Complete();
        }
        internal void DeferEnd(NodeResult result, Exception? error, bool hookGated)
        {
            _result = result;
            _error = error;
            _hookGated = hookGated;
        }
        internal void Complete(Exception? error = null)
        {
            if (_completed) return;
            _completed = true;
            if (_result is { } result)
            {
                span?.SetAttribute("gen_ai.agent.status", result.Status.ToString().ToLowerInvariant());
                span?.SetAttribute("gen_ai.agent.execution_time", _hookGated ? 0d : result.Duration.TotalMilliseconds);
                span?.End(usage: _hookGated ? null : result.Usage, error: _error ?? error);
            }
        }
        public void Dispose() { Complete(); span?.Dispose(); }
    }
}
