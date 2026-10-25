using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Strands.Agents.Logging;

internal sealed class LogDispatcher
{
    private static readonly ConcurrentDictionary<(LogLevel Level, string Template, int Count), LogWriter> Writers = new();
    private static readonly LogDefineOptions DefinitionOptions = new() { SkipEnabledCheck = true };
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);
    private ILogger _logger = new TraceConsoleLogger();
    internal ILogger Logger => Volatile.Read(ref _logger);
    internal void Configure(ILogger logger) => Interlocked.Exchange(ref _logger, logger);

    internal void WarnOnce(string message)
    {
        Guard.NotNull(message, nameof(message));
        // Reserve before invoking host code so a same-message callback cannot recurse.
        if (!_warned.TryAdd(message, 0)) return;
        if (!Write(LogLevel.Warning, null, "{Message}", [message])) _warned.TryRemove(message, out _);
    }

    internal bool Write(LogLevel level, Exception? error, string template, object?[] values)
        => Write(level, error, template, values, out _);

    internal bool Write(LogLevel level, Exception? error, string template, object?[] values, out Exception? observerFailure)
    {
        observerFailure = null;
        var logger = Logger;
        try
        {
            if (!logger.IsEnabled(level)) return true;
            var detached = values.Select(value => value is Array array ? array.Clone() : value).ToArray();
            Writers.GetOrAdd((level, template, detached.Length), CreateWriter)(logger, detached, error);
            return true;
        }
        catch (Exception failure) { observerFailure = failure; return false; }
    }

    private delegate void LogWriter(ILogger logger, object?[] values, Exception? error);
    private static LogWriter CreateWriter((LogLevel Level, string Template, int Count) key)
    {
        var id = new EventId(0);
        switch (key.Count)
        {
            case 0:
                var zero = LoggerMessage.Define(key.Level, id, key.Template, DefinitionOptions);
                return (logger, _, error) => zero(logger, error);
            case 1:
                var one = LoggerMessage.Define<object?>(key.Level, id, key.Template, DefinitionOptions);
                return (logger, values, error) => one(logger, values[0], error);
            case 2:
                var two = LoggerMessage.Define<object?, object?>(key.Level, id, key.Template, DefinitionOptions);
                return (logger, values, error) => two(logger, values[0], values[1], error);
            case 3:
                var three = LoggerMessage.Define<object?, object?, object?>(key.Level, id, key.Template, DefinitionOptions);
                return (logger, values, error) => three(logger, values[0], values[1], values[2], error);
            default: throw new ArgumentException("An SDK diagnostic supports at most three structured values.", nameof(key));
        }
    }

    private sealed class TraceConsoleLogger : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            Exception? failure = null;
            try
            {
                if (logLevel >= LogLevel.Error) Trace.TraceError(message);
                else if (logLevel == LogLevel.Warning) Trace.TraceWarning(message);
                else Trace.TraceInformation(message);
            }
            catch (Exception error) { failure = error; }
            try { if (logLevel >= LogLevel.Warning) Console.Error.WriteLine(message); }
            catch (Exception error) { failure ??= error; }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
