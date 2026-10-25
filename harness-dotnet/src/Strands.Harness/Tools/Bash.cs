using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Internal;

namespace Strands.Harness.Tools;

/// <summary>Creates an optional persistent host Bash tool.</summary>
public static class Bash
{
    /// <summary>Creates an application-owned tool; it provides no execution isolation.</summary>
    public static BashTool Create(BashConfig? config = null) => new(config ?? new());
}

/// <summary>A host Bash tool whose sessions are independent for each borrowed Agent.</summary>
public sealed class BashTool : IRichTool, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<Agent, BashSession> _sessions = new();
    private readonly HashSet<BashSession> _owned = [];
    private readonly BashConfig _config;
    private Task? _disposal;

    internal BashTool(BashConfig config)
    {
        Guard.NotNull(config, nameof(config));
        if (config.CleanupTimeout <= TimeSpan.Zero || config.CleanupTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(config), "CleanupTimeout must be positive and within the native timer range.");
        var host = HostShellRunner.SnapshotOptions(new()
        {
            ExecutablePath = config.ExecutablePath,
            WorkingDirectory = config.WorkingDirectory,
            Environment = config.Environment,
            MaxOutputBytes = config.MaximumOutputBytes,
        });
        _config = config with { Environment = host.Environment, WorkingDirectory = host.WorkingDirectory ?? Environment.CurrentDirectory };
        Spec = new(config.Name, config.Description ?? "Executes bash commands in a persistent host session. Supports execute and restart modes. Variables and working directory persist within each Agent session.", JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                mode = new { type = "string", @enum = new[] { "execute", "restart" } },
                command = new { type = "string" },
                timeout = new { type = "number", exclusiveMinimum = 0 }
            },
            required = new[] { "mode" }
        }));
    }
    /// <inheritdoc />
    public ToolSpec Spec { get; }
    /// <inheritdoc />
    public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        => (await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false)).Content;
    /// <inheritdoc />
    public async ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context, nameof(context));
        Guard.NotNull(context.Agent, nameof(context));
        lock (_gate) if (_disposal is not null) throw new ObjectDisposedException(nameof(BashTool));
        cancellationToken.ThrowIfCancellationRequested();
        context.CancelSignal.ThrowIfCancellationRequested();
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String || mode.GetString() is not ("execute" or "restart"))
            throw new ArgumentException("mode must be execute or restart.", nameof(input));
        TimeSpan? timeout = null;
        if (input.TryGetProperty("timeout", out var configured))
        {
            if (configured.ValueKind != JsonValueKind.Number || !configured.TryGetDouble(out var seconds) || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0 || seconds * 1000 > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(input), "timeout must be positive, finite and within the native timer range.");
            timeout = TimeSpan.FromSeconds(seconds);
        }
        string? command = null;
        if (mode.GetString() == "execute")
        {
            if (!input.TryGetProperty("command", out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(command = value.GetString()))
                throw new ArgumentException("command is required when mode is execute.", nameof(input));
            if (command!.IndexOf('\0') >= 0) throw new ArgumentException("A Bash command cannot contain NUL.", nameof(input));
        }
        var session = GetSession(context.Agent, command is null ? null : timeout);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.CancelSignal);
        try
        {
            if (command is null)
            {
                await session.RestartAsync(lifetime.Token).ConfigureAwait(false);
                return new(context.ToolUse.ToolUseId, "Bash session restarted");
            }
            var output = await session.ExecuteAsync(command, timeout, lifetime.Token).ConfigureAwait(false);
            var result = JsonSerializer.SerializeToElement(new { output = Trim(output.Output), error = Trim(output.Error) });
            return new(context.ToolUse.ToolUseId, result.GetRawText()) { StructuredContent = result };
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            if (error.CancellationToken == cancellationToken) throw;
            var primary = new OperationCanceledException(error.Message, error, cancellationToken);
            BashSession.CopyPartialDiagnostics(error, primary);
            throw primary;
        }
        catch (OperationCanceledException error) when (context.CancelSignal.IsCancellationRequested)
        {
            if (error.CancellationToken == context.CancelSignal) throw;
            var primary = new OperationCanceledException(error.Message, error, context.CancelSignal);
            BashSession.CopyPartialDiagnostics(error, primary);
            throw primary;
        }
    }

    private BashSession GetSession(Agent agent, TimeSpan? firstTimeout)
    {
        lock (_gate)
        {
            if (_disposal is not null) throw new ObjectDisposedException(nameof(BashTool));
            if (_sessions.TryGetValue(agent, out var existing)) return existing;
            var weakOwner = new WeakReference<BashTool>(this);
            var session = new BashSession(_config, firstTimeout ?? TimeSpan.FromSeconds(120), closed =>
            {
                if (weakOwner.TryGetTarget(out var owner)) lock (owner._gate) owner._owned.Remove(closed);
            });
            // Agent admission and registration are one transaction; this callback cannot miss shutdown's snapshot.
            var registration = agent.RegisterOwnedShutdownCallback((_, _) => new ValueTask(session.CloseAsync()));
            session.SetRegistration(registration);
            _sessions.Add(agent, session);
            if (!session.IsClosed) _owned.Add(session);
            return session;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool> completion; BashSession[] sessions;
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task; sessions = _owned.ToArray();
        }
        _ = CompleteDisposalAsync(completion, sessions);
        return new(completion.Task);
    }

    private static async Task CompleteDisposalAsync(TaskCompletionSource<bool> completion, BashSession[] sessions)
    {
        Exception? primary = null;
        try
        {
            var tasks = sessions.Select(session => session.CloseAsync()).ToArray();
            foreach (var task in tasks)
                try { await task.ConfigureAwait(false); }
                catch (Exception error) { if (primary is null) primary = error; else BashSession.AttachCleanup(primary, error); }
            if (primary is null) completion.TrySetResult(true); else completion.TrySetException(primary);
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private static string Trim(string value)
    {
        var first = 0; var last = value.Length;
        while (first < last && IsSpace(value[first])) first++;
        while (last > first && IsSpace(value[last - 1])) last--;
        return value.Substring(first, last - first);
        static bool IsSpace(char c) => c is '\u0009' or '\u000A' or '\u000B' or '\u000C' or '\u000D' or '\u0020' or '\u00A0' or '\u1680' or >= '\u2000' and <= '\u200A' or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF';
    }
}
