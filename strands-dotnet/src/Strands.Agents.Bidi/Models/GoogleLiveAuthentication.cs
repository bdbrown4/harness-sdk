using System.Runtime.ExceptionServices;

namespace Strands.Agents.Bidi.Models;

public sealed partial class GoogleGeminiLiveModel
{
    private readonly AsyncLocal<AuthenticationFrame?> _authentication = new();

    private void RejectAuthenticationReentry()
    {
        if (_authentication.Value is { } frame && Volatile.Read(ref frame.Active) != 0) throw new InvalidOperationException("A credential callback cannot await lifecycle operations on its own Gemini Live model.");
    }

    private async Task<string?> ResolveAccessTokenAsync(GoogleGeminiLiveModelOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.UseVertexAi) return null;
        var previous = _authentication.Value;
        var frame = new AuthenticationFrame();
        _authentication.Value = frame;
        string token;
        try { token = await options.AccessTokenProvider!(cancellationToken).ConfigureAwait(false); }
        finally { Volatile.Write(ref frame.Active, 0); _authentication.Value = previous; }
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(token) || token.Any(character => !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '.' or '_' or '~' or '+' or '/' or '='))) throw new ArgumentException("AccessTokenProvider must return a nonempty bearer token containing valid token characters.");
        return token;
    }

    private async Task StartWithAuthenticationAsync(PreparedSession prepared, CancellationToken caller, bool replacing = false, PreparedSession? fallback = null)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GoogleGeminiLiveModel));
            if (!replacing && _connection is not null) throw new InvalidOperationException("The Live model is already started.");
        }
        using var deadline = new CancellationTokenSource();
        deadline.CancelAfter(prepared.Options.SetupTimeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token, deadline.Token);
        var usingFallback = false;
        try
        {
            var accessToken = await ResolveAccessTokenAsync(prepared.Options, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (replacing) await StopPreviousAsync(caller, operation.Token).ConfigureAwait(false);
            try { await StartCoreAsync(prepared, caller, operation.Token, accessToken, replacing ? _pendingTools : null).ConfigureAwait(false); }
            catch (ModelException resumeError) when (fallback is not null && !caller.IsCancellationRequested && !_lifetime.IsCancellationRequested && !deadline.IsCancellationRequested && resumeError.Data["Strands.CleanupErrors"] is null)
            {
                usingFallback = true;
                try { await StartWithAuthenticationAsync(fallback, caller, replacing: true).ConfigureAwait(false); }
                catch (Exception freshError) { Cleanup(freshError, resumeError); throw; }
            }
        }
        catch (Exception error)
        {
            var primary = caller.IsCancellationRequested ? new OperationCanceledException("Gemini Live was cancelled.", error, caller)
                : _lifetime.IsCancellationRequested ? new OperationCanceledException("The Gemini Live model was disposed.", error, _lifetime.Token)
                : !usingFallback && deadline.IsCancellationRequested ? new ModelException("Gemini Live credentials or setup acknowledgement timed out.", error)
                : error;
            ExceptionDispatchInfo.Capture(primary).Throw(); throw;
        }
    }

    private async Task StopPreviousAsync(CancellationToken caller, CancellationToken operation)
    {
        LiveConnection? previous;
        lock (_gate) previous = _connection;
        if (previous is null) return;
        await previous.StateGate.WaitAsync(operation).ConfigureAwait(false);
        try
        {
            operation.ThrowIfCancellationRequested();
            _pendingTools = new(previous.State.ToolNames, StringComparer.Ordinal);
            await StopCoreAsync(caller).ConfigureAwait(false);
        }
        finally { previous.StateGate.Release(); }
    }

    private sealed class AuthenticationFrame
    {
        internal int Active = 1;
    }
}
