using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.VendedTools;

/// <summary>Creates the optional stop tool with explicit Python cancellation or TypeScript end-turn behavior.</summary>
public static class Stop
{
    private static readonly ConditionalWeakTable<Agent, TypeScriptAgentRequests> TypeScriptAgents = new();
    /// <summary>The source-compatible fallback for an absent, null, or empty message.</summary>
    public const string DefaultMessage = "Agent loop stopped.";
    /// <summary>The shared pinned source description.</summary>
    public const string DefaultDescription = "Gracefully ends the agent loop when the task is complete. Call this tool once with an optional final message when no further work is needed. This is a cooperative stop, not an abort: any tools already requested in this turn still run.";

    /// <summary>Creates a tool with detached metadata and input schema. TypeScript hooks are installed lazily on the calling agent.</summary>
    public static ITool Create(StopOptions? options = null)
    {
        options ??= new();
        if (options.MaximumMessageLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumMessageLength must be positive.");
        if (options.Behavior is not StopBehavior.PythonCancellation and not StopBehavior.TypeScriptEndTurn)
            throw new ArgumentOutOfRangeException(nameof(options), "Behavior is not a supported source profile.");
        var message = new Dictionary<string, object?>
        {
            ["type"] = new[] { "string", "null" },
            ["description"] = "Optional final message summarizing why the loop is ending. Appears as the loop's last assistant turn.",
        };
        if (options.Behavior == StopBehavior.TypeScriptEndTurn) message["maxLength"] = options.MaximumMessageLength;
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object?> { ["message"] = message },
            required = Array.Empty<string>(),
        });
        var spec = new ToolSpec(options.Name, options.Description ?? DefaultDescription, schema);
        var maximumMessageLength = options.MaximumMessageLength;
        var behavior = options.Behavior;
        return new FunctionTool(spec, (input, context, cancellationToken) =>
        {
            Guard.NotNull(context, nameof(context));
            Guard.NotNull(context.Agent, nameof(context));
            var message = ReadMessage(input, maximumMessageLength, behavior);
            cancellationToken.ThrowIfCancellationRequested();
            context.CancelSignal.ThrowIfCancellationRequested();
            if (behavior == StopBehavior.TypeScriptEndTurn)
            {
                Guard.NotNull(context.ToolUse, nameof(context));
                Guard.NotNull(context.InvocationState, nameof(context));
                Guard.NotNullOrWhiteSpace(context.ToolUse.ToolUseId, nameof(context));
                TypeScriptAgents.GetValue(context.Agent, static agent => new TypeScriptAgentRequests(agent))
                    .Request(context.Agent, context.InvocationState, context.ToolUse.ToolUseId, message, cancellationToken, context.CancelSignal);
                return new(message);
            }
            context.Agent.Cancel(message, afterCurrentTools: true);
            return new(message);
        });
    }

    private static string ReadMessage(JsonElement input, int maximumLength, StopBehavior behavior)
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Stop input must be an object.", nameof(input));
        if (!input.TryGetProperty("message", out var value) || value.ValueKind == JsonValueKind.Null)
            return DefaultMessage;
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException("message must be a string or null.", nameof(input));
        var message = value.GetString()!;
        if (message.Length == 0) return DefaultMessage;
        var length = behavior == StopBehavior.TypeScriptEndTurn ? message.Length : CodePointLength(message);
        if (length > maximumLength)
            throw new ArgumentOutOfRangeException(nameof(input), "message exceeds MaximumMessageLength.");
        return message;
    }

    private static int CodePointLength(string value)
    {
        var length = 0;
        for (var index = 0; index < value.Length; index++)
        {
            length++;
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) index++;
        }
        return length;
    }

    private sealed class TypeScriptAgentRequests(Agent owner)
    {
        private readonly WeakReference<Agent> _owner = new(owner);
        private readonly object _gate = new();
        private ConditionalWeakTable<IReadOnlyDictionary<string, object?>, PendingSlot> _pending = new();
        private IDisposable[] _registrations = Array.Empty<IDisposable>();
        private bool _installed;
        private bool _closed;

        internal void Request(Agent agent, IReadOnlyDictionary<string, object?> state, string toolUseId, string message,
            CancellationToken operationToken, CancellationToken contextToken)
        {
            if (!Owns(agent)) throw new InvalidOperationException("A stop request belongs to its originating agent.");
            lock (_gate)
            {
                operationToken.ThrowIfCancellationRequested();
                contextToken.ThrowIfCancellationRequested();
                if (_closed) throw new ObjectDisposedException(nameof(Agent));
                EnsureInstalled(agent);
                operationToken.ThrowIfCancellationRequested();
                contextToken.ThrowIfCancellationRequested();
                _pending.GetValue(state, static _ => new PendingSlot()).Request = new(toolUseId, message);
            }
        }

        // Called only under _gate. Registration and built-in handle removal never invoke callbacks.
        private void EnsureInstalled(Agent agent)
        {
            if (_installed) return;
            var registrations = new IDisposable[4];
            var count = 0;
            try
            {
                registrations[count++] = agent.RegisterOwnedShutdownCallback(OnShutdown);
                registrations[count++] = agent.Hooks.Register<BeforeInvocationEvent>(OnBeforeInvocation);
                registrations[count++] = agent.Hooks.Register<AfterInvocationEvent>(OnAfterInvocation);
                registrations[count++] = agent.Hooks.Register<AfterToolsEvent>(OnAfterTools);
                _registrations = registrations;
                _installed = true;
            }
            catch (Exception failure)
            {
                for (var index = count - 1; index >= 0; index--)
                {
                    // Assignment can throw before the slot is populated; only acquired handles are removed.
                    if (registrations[index] is not { } registration) continue;
                    try { registration.Dispose(); }
                    catch (Exception cleanup) { AttachCleanup(failure, cleanup); }
                }
                throw;
            }
        }

        private bool Owns(Agent agent) => _owner.TryGetTarget(out var owner) && ReferenceEquals(owner, agent);

        private ValueTask OnBeforeInvocation(BeforeInvocationEvent value, CancellationToken cancellationToken)
        {
            if (Owns(value.Agent)) ClearPending();
            return default;
        }

        private ValueTask OnAfterInvocation(AfterInvocationEvent value, CancellationToken cancellationToken)
        {
            if (Owns(value.Agent)) ClearPending();
            return default;
        }

        private void ClearPending()
        {
            lock (_gate) _pending = new();
        }

        private ValueTask OnAfterTools(AfterToolsEvent value, CancellationToken cancellationToken)
        {
            if (!Owns(value.Agent)) return default;
            EndTurnRequest? request;
            lock (_gate)
            {
                if (_closed || !_pending.TryGetValue(value.InvocationState, out var slot) || slot.Request is null) return default;
                request = slot.Request;
            }
            // A caller-supplied result collection is inspected outside the private gate.
            if (!value.Results.Any(result => string.Equals(result.ToolUseId, request.ToolUseId, StringComparison.Ordinal))) return default;
            var content = new ContentBlock[] { new TextBlock(request.Message) };
            lock (_gate)
            {
                if (_closed || !_pending.TryGetValue(value.InvocationState, out var slot) || !ReferenceEquals(slot.Request, request)) return default;
                _pending.Remove(value.InvocationState);
            }
            value.EndTurn = content;
            return default;
        }

        private ValueTask OnShutdown(ShutdownEvent value, CancellationToken cancellationToken)
        {
            if (!Owns(value.Agent)) return default;
            IDisposable[] registrations;
            lock (_gate)
            {
                if (_closed) return default;
                _closed = true;
                _pending = new();
                registrations = _registrations;
                _registrations = Array.Empty<IDisposable>();
            }
            Exception? failure = null;
            for (var index = registrations.Length - 1; index >= 0; index--)
            {
                try { registrations[index].Dispose(); }
                catch (Exception cleanup)
                {
                    if (failure is null) failure = cleanup;
                    else AttachCleanup(failure, cleanup);
                }
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            return default;
        }

        private static void AttachCleanup(Exception primary, Exception cleanup)
        {
            if (ReferenceEquals(primary, cleanup)) return;
            const string key = "Strands.CleanupErrors";
            if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
            if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
        }

        private sealed class PendingSlot
        {
            internal EndTurnRequest? Request;
        }

        private sealed record EndTurnRequest(string ToolUseId, string Message);
    }
}
