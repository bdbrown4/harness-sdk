using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Strands.Agents.Models;

/// <summary>The timing context for a retry after a failed model attempt.</summary>
/// <param name="Attempt">The one-based failed attempt number.</param>
/// <param name="Elapsed">Elapsed time since the request began.</param>
/// <param name="LastDelay">The preceding delay, if any.</param>
public sealed record BackoffContext(int Attempt, TimeSpan Elapsed, TimeSpan? LastDelay = null);

/// <summary>Computes a bounded delay without executing or retrying a request.</summary>
public interface IBackoffStrategy
{
    /// <summary>Returns a nonnegative delay no greater than Int32.MaxValue milliseconds.</summary>
    TimeSpan GetDelay(BackoffContext context);
}

/// <summary>The randomization applied to a capped backoff.</summary>
public enum BackoffJitter
{
    /// <summary>Return the calculated delay.</summary>
    None,
    /// <summary>Choose uniformly between zero and the calculated delay.</summary>
    Full,
    /// <summary>Keep half the delay and randomize the remaining half.</summary>
    Equal,
    /// <summary>Choose between the base and three times the preceding delay, capped at the maximum.</summary>
    Decorrelated
}

/// <summary>A fixed retry delay, defaulting to one second.</summary>
public sealed class ConstantBackoff : IBackoffStrategy
{
    private readonly TimeSpan _delay;
    /// <summary>Constructs a fixed nonnegative delay.</summary>
    public ConstantBackoff(TimeSpan? delay = null) => _delay = BackoffMath.Validate(delay ?? TimeSpan.FromSeconds(1), nameof(delay));
    /// <inheritdoc/>
    public TimeSpan GetDelay(BackoffContext context) { BackoffMath.Validate(context); return _delay; }
}

/// <summary>Linear backoff, defaulting to a one-second base, thirty-second cap, and full jitter.</summary>
public sealed class LinearBackoff : IBackoffStrategy
{
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maximumDelay;
    private readonly BackoffJitter _jitter;
    /// <summary>Constructs a capped linear backoff.</summary>
    public LinearBackoff(TimeSpan? baseDelay = null, TimeSpan? maximumDelay = null, BackoffJitter jitter = BackoffJitter.Full)
    {
        _baseDelay = BackoffMath.Validate(baseDelay ?? TimeSpan.FromSeconds(1), nameof(baseDelay));
        _maximumDelay = BackoffMath.Validate(maximumDelay ?? TimeSpan.FromSeconds(30), nameof(maximumDelay));
        _jitter = BackoffMath.Validate(jitter);
    }
    /// <inheritdoc/>
    public TimeSpan GetDelay(BackoffContext context)
    {
        BackoffMath.Validate(context);
        return BackoffMath.Apply(Math.Min(_maximumDelay.TotalMilliseconds, _baseDelay.TotalMilliseconds * context.Attempt), _baseDelay, _maximumDelay, _jitter, context.LastDelay);
    }
}

/// <summary>Exponential backoff, defaulting to a one-second base, thirty-second cap, doubling, and full jitter.</summary>
public sealed class ExponentialBackoff : IBackoffStrategy
{
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maximumDelay;
    private readonly double _multiplier;
    private readonly BackoffJitter _jitter;
    /// <summary>Constructs a capped exponential backoff. The multiplier must be finite and at least one.</summary>
    public ExponentialBackoff(TimeSpan? baseDelay = null, TimeSpan? maximumDelay = null, double multiplier = 2, BackoffJitter jitter = BackoffJitter.Full)
    {
        _baseDelay = BackoffMath.Validate(baseDelay ?? TimeSpan.FromSeconds(1), nameof(baseDelay));
        _maximumDelay = BackoffMath.Validate(maximumDelay ?? TimeSpan.FromSeconds(30), nameof(maximumDelay));
        if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier < 1) throw new ArgumentOutOfRangeException(nameof(multiplier));
        _multiplier = multiplier;
        _jitter = BackoffMath.Validate(jitter);
    }
    /// <inheritdoc/>
    public TimeSpan GetDelay(BackoffContext context)
    {
        BackoffMath.Validate(context);
        var raw = _baseDelay == TimeSpan.Zero ? 0 : Math.Min(_maximumDelay.TotalMilliseconds, _baseDelay.TotalMilliseconds * Math.Pow(_multiplier, context.Attempt - 1));
        return BackoffMath.Apply(raw, _baseDelay, _maximumDelay, _jitter, context.LastDelay);
    }
}

/// <summary>Retry configuration; the default matches the upstream six-attempt throttling policy.</summary>
public sealed record RetryModelOptions
{
    /// <summary>Total attempts per request, including the first; defaults to six.</summary>
    public int MaxAttempts { get; init; } = 6;
    /// <summary>Delay policy; defaults to exponential full jitter with a four-second base and 240-second cap.</summary>
    public IBackoffStrategy Backoff { get; init; } = new ExponentialBackoff(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(240));
    /// <summary>Optional failure classifier; null retries only ModelThrottledException. Cancellation is never retried.</summary>
    public Func<Exception, bool>? IsRetryable { get; init; }
}

/// <summary>Retries failed model calls only before any event escapes; completed tools are never replayed.</summary>
public sealed class RetryModel : IInvocationModel, IStatefulModel, IModelMetadataProvider, IModelTokenCounter
{
    /// <summary>The wrapped model's resolved identity and limits.</summary>
    public ModelMetadata Metadata => _model.GetLimits();
    /// <summary>Forwards the complete request to the wrapped counter, or its heuristic fallback, without retrying tokenization.</summary>
    public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default) => _model.CountTokensAsync(request, cancellationToken);
    /// <inheritdoc/>
    public bool Stateful => _model is IStatefulModel { Stateful: true };
    private readonly IModel _model;
    private readonly RetryModelOptions _options;
    /// <summary>Decorates a caller-owned model without taking ownership of its transport.</summary>
    public RetryModel(IModel model, RetryModelOptions? options = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _options = options ?? new();
        if (_options.MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(options));
        Guard.NotNull(_options.Backoff, nameof(options));
    }
    /// <inheritdoc/>
    public IModel CreateInvocationModel(IReadOnlyDictionary<string, object?> invocationState)
    {
        Guard.NotNull(invocationState, nameof(invocationState));
        return new RetryModel(_model is IInvocationModel scoped ? scoped.CreateInvocationModel(invocationState) : _model, _options);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? lastDelay = null;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = new ModelAttempt(_model, request, cancellationToken);
            try { while (await call.MoveNextAsync().ConfigureAwait(false)) yield return call.Current; }
            finally { await call.DisposeAsync().ConfigureAwait(false); }
            if (call.Error is null) yield break;
            if (call.Emitted || attempt >= _options.MaxAttempts || call.Error is OperationCanceledException || !(_options.IsRetryable?.Invoke(call.Error) ?? call.Error is ModelThrottledException))
                ExceptionDispatchInfo.Capture(call.Error).Throw();
            cancellationToken.ThrowIfCancellationRequested();
            lastDelay = BackoffMath.Validate(_options.Backoff.GetDelay(new(attempt, timer.Elapsed, lastDelay)), nameof(_options.Backoff));
            await Task.Delay(lastDelay.Value, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal static class BackoffMath
{
    private static readonly Random Random = new();
    internal static TimeSpan Validate(TimeSpan delay, string parameter)
    {
        if (delay < TimeSpan.Zero || delay.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(parameter, "Delay must be between zero and Int32.MaxValue milliseconds.");
        return delay;
    }
    internal static BackoffJitter Validate(BackoffJitter jitter) => Enum.IsDefined(typeof(BackoffJitter), jitter) ? jitter : throw new ArgumentOutOfRangeException(nameof(jitter));
    internal static void Validate(BackoffContext context)
    {
        Guard.NotNull(context, nameof(context));
        if (context.Attempt < 1 || context.Elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(context));
        if (context.LastDelay is { } delay) Validate(delay, nameof(context));
    }
    internal static TimeSpan Apply(double raw, TimeSpan baseDelay, TimeSpan maximumDelay, BackoffJitter jitter, TimeSpan? previous)
    {
        double sample;
        lock (Random) sample = Random.NextDouble();
        if (jitter == BackoffJitter.Decorrelated && previous is { } last)
        {
            var lower = Math.Min(baseDelay.TotalMilliseconds, maximumDelay.TotalMilliseconds);
            var upper = Math.Max(lower, Math.Min(maximumDelay.TotalMilliseconds, last.TotalMilliseconds * 3));
            return TimeSpan.FromMilliseconds(lower + sample * (upper - lower));
        }
        var value = jitter switch
        {
            BackoffJitter.None => raw,
            BackoffJitter.Equal => raw / 2 + sample * raw / 2,
            _ => sample * raw
        };
        return TimeSpan.FromMilliseconds(value);
    }
}

internal sealed class ModelAttempt : IAsyncDisposable
{
    private IAsyncEnumerator<ModelStreamEvent>? _iterator;
    private readonly ModelStreamAccumulator _accumulator = new();
    private readonly CancellationToken _cancellationToken;
    internal Exception? Error { get; private set; }
    internal bool Emitted { get; private set; }
    internal ModelStreamEvent Current { get; private set; } = null!;
    internal ModelAttempt(IModel model, ModelRequest request, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        try { _iterator = model.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken); }
        catch (Exception failure) { Error = failure; }
    }
    internal async ValueTask<bool> MoveNextAsync()
    {
        if (Error is not null || _iterator is null) return false;
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!await _iterator.MoveNextAsync().ConfigureAwait(false)) { _accumulator.Complete(); return false; }
            _cancellationToken.ThrowIfCancellationRequested();
            Current = _iterator.Current ?? throw new ModelException("The model emitted a null event.");
            _accumulator.Process(Current);
            Emitted = true;
            return true;
        }
        catch (Exception failure) { Error = failure; return false; }
    }
    public async ValueTask DisposeAsync()
    {
        try { if (_iterator is not null) await _iterator.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanup)
        {
            if (Error is null) { Error = cleanup; ExceptionDispatchInfo.Capture(cleanup).Throw(); }
            else
            {
                const string key = "Strands.CleanupErrors";
                if (Error.Data[key] is not List<Exception> errors) Error.Data[key] = errors = [];
                errors.Add(cleanup);
            }
        }
        finally { _iterator = null; }
    }
}
