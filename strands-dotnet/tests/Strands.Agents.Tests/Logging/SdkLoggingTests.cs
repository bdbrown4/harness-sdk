using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Strands.Agents.Logging;
using Strands.Agents.Memory;

namespace Strands.Agents.Tests.Logging;

[CollectionDefinition("SDK logging", DisableParallelization = true)]
public sealed class SdkLoggingCollection { }

[Collection("SDK logging")]
public sealed class SdkLoggingTests : IDisposable
{
    private readonly ILogger _original = SdkLogging.Logger;
    public void Dispose() => SdkLogging.ConfigureLogging(_original);

    [Fact]
    public void SuccessfulWarningDeduplicationSurvivesLoggerReplacement()
    {
        var first = new CaptureLogger();
        var replacement = new CaptureLogger();
        var message = "warning with literal {braces} " + Guid.NewGuid();
        SdkLogging.ConfigureLogging(first);
        SdkLogging.WarnOnce(message);
        SdkLogging.WarnOnce(message);
        SdkLogging.ConfigureLogging(replacement);
        SdkLogging.WarnOnce(message);

        Assert.Equal(message, Assert.Single(first.Entries).Message);
        Assert.Empty(replacement.Entries);
    }

    [Fact]
    public void ADisabledWarningIsStillDeduplicatedAfterEnablingOrReplacingTheLogger()
    {
        var logger = new CaptureLogger { Enabled = false };
        var message = "disabled warning " + Guid.NewGuid();
        SdkLogging.ConfigureLogging(logger);
        SdkLogging.WarnOnce(message);
        logger.Enabled = true;
        SdkLogging.WarnOnce(message);
        var replacement = new CaptureLogger();
        SdkLogging.ConfigureLogging(replacement);
        SdkLogging.WarnOnce(message);
        Assert.Empty(logger.Entries);
        Assert.Empty(replacement.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyAThrownEmissionRemainsRetryable(bool failEnabledCheck)
    {
        var logger = new CaptureLogger();
        var message = "retry warning " + Guid.NewGuid();
        var failure = new IOException("host logger failed");
        if (failEnabledCheck) logger.EnabledFailure = failure;
        else logger.LogFailure = failure;
        SdkLogging.ConfigureLogging(logger);
        SdkLogging.WarnOnce(message);
        logger.EnabledFailure = null;
        logger.LogFailure = null;
        SdkLogging.WarnOnce(message);
        SdkLogging.WarnOnce(message);
        Assert.Equal(message, Assert.Single(logger.Entries).Message);
    }

    [Fact]
    public void SameMessageReentryTerminatesAndLoggerCallbacksCanReplaceTheLogger()
    {
        var replacement = new CaptureLogger();
        var message = "reentrant warning " + Guid.NewGuid();
        var logger = new CaptureLogger { OnLog = () => { SdkLogging.WarnOnce(message); SdkLogging.ConfigureLogging(replacement); } };
        SdkLogging.ConfigureLogging(logger);
        SdkLogging.WarnOnce(message);
        SdkLogging.WarnOnce(message);
        Assert.Single(logger.Entries);
        Assert.Empty(replacement.Entries);
    }

    [Fact]
    public async Task ConcurrentWarningsReserveOneEmissionWithoutSerializingUnrelatedMessages()
    {
        var logger = new CaptureLogger();
        SdkLogging.ConfigureLogging(logger);
        var message = "concurrent warning " + Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => SdkLogging.WarnOnce(message))));
        Assert.Single(logger.Entries);
        SdkLogging.WarnOnce(message + " other");
        Assert.Equal(2, logger.Entries.Count);
    }

    [Fact]
    public void StructuredValuesAndExceptionIdentityReachTheHostWithoutEagerRendering()
    {
        var logger = new CaptureLogger();
        SdkLogging.ConfigureLogging(logger);
        var names = new[] { "one", "two" };
        var failure = new IOException("original");
        SdkLogging.Warning(failure, "names=<{Names}> | failed", (object)names);
        names[0] = "changed";
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("names=<one, two> | failed", entry.Message);
        Assert.Same(failure, entry.Error);
        Assert.Equal(new[] { "one", "two" }, Assert.IsType<string[]>(entry.Values["Names"]));
        Assert.Equal("names=<{Names}> | failed", entry.Values["{OriginalFormat}"]);
    }

    [Fact]
    public void DisabledDiagnosticsDoNotFormatPayloadsAndObserverFailuresStayObservational()
    {
        SdkLogging.ConfigureLogging(new CaptureLogger { Enabled = false });
        var payload = new ThrowingPayload();
        SdkLogging.Warning("payload=<{Payload}>", payload);
        Assert.Equal(0, payload.Calls);
        SdkLogging.ConfigureLogging(new CaptureLogger { LogFailure = new IOException("listener") });
        SdkLogging.Warning("message");
    }

    [Fact]
    public async Task ConfiguredFactoriesAndLoggersRemainBorrowedAcrossAgentLifetimeAndReplacement()
    {
        var factory = new CaptureFactory();
        SdkLogging.ConfigureLogging(factory);
        Assert.Equal(SdkLogging.CategoryName, Assert.Single(factory.Categories));
        await using (var agent = new Agent(new() { Model = new MalformedFactsModel() })) await agent.InitializeAsync();
        SdkLogging.ConfigureLogging(new CaptureLogger());
        Assert.Equal(0, factory.Disposals);
        Assert.Equal(0, factory.Logger.Disposals);
    }

    [Fact]
    public void DefaultLoggerRetainsTraceInformationAndWritesOnlyWarningsAndErrorsToConsole()
    {
        var dispatcher = new LogDispatcher();
        using var trace = new CaptureTraceListener();
        var previous = Console.Error;
        using var console = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        System.Diagnostics.Trace.Listeners.Add(trace);
        Console.SetError(console);
        try
        {
            dispatcher.Write(LogLevel.Debug, null, "debug", []);
            dispatcher.Write(LogLevel.Information, null, "info", []);
            dispatcher.Write(LogLevel.Warning, null, "warning", []);
            dispatcher.Write(LogLevel.Error, null, "error", []);
        }
        finally { Console.SetError(previous); System.Diagnostics.Trace.Listeners.Remove(trace); }
        Assert.Contains("info", trace.Messages);
        Assert.Contains("warning", trace.Messages);
        Assert.Contains("error", trace.Messages);
        Assert.DoesNotContain("debug", trace.Messages);
        Assert.Equal("warning" + Environment.NewLine + "error" + Environment.NewLine, console.ToString());
    }

    [Fact]
    public void InvalidConfigurationDoesNotReplaceTheCurrentLogger()
    {
        var current = SdkLogging.Logger;
        Assert.Throws<ArgumentNullException>(() => SdkLogging.ConfigureLogging((ILogger)null!));
        Assert.Throws<ArgumentNullException>(() => SdkLogging.ConfigureLogging((ILoggerFactory)null!));
        Assert.Same(current, SdkLogging.Logger);
    }

    [Fact]
    public async Task ARealSdkDiagnosticReachesTheConfiguredStandardLogger()
    {
        var logger = new CaptureLogger();
        SdkLogging.ConfigureLogging(logger);

        var facts = await new ModelExtractor(new() { Model = new MalformedFactsModel() }).ExtractAsync([Message.User("remember")]);

        Assert.Empty(facts);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Memory extractor returned no JSON array; skipping output.", entry.Message);
    }

    [Theory]
    [InlineData("no array")]
    [InlineData("[not valid JSON]")]
    public async Task LoggerFailureCannotTurnTolerantFactParsingIntoAProviderFailure(string output)
    {
        SdkLogging.ConfigureLogging(new CaptureLogger { LogFailure = new IOException("host failed") });
        var facts = await new ModelExtractor(new() { Model = new MalformedFactsModel(output) }).ExtractAsync([Message.User("remember")]);
        Assert.Empty(facts);
    }

    [Fact]
    public void FailedTraceListenerStillAttemptsConsoleAndDoesNotPermanentlyConsumeTheWarning()
    {
        var dispatcher = new LogDispatcher();
        using var trace = new CaptureTraceListener { ThrowOnWrite = true };
        var previous = Console.Error;
        using var console = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        System.Diagnostics.Trace.Listeners.Add(trace);
        Console.SetError(console);
        try { dispatcher.WarnOnce("retry after listener failure"); dispatcher.WarnOnce("retry after listener failure"); }
        finally { Console.SetError(previous); System.Diagnostics.Trace.Listeners.Remove(trace); }
        Assert.Equal(string.Concat(Enumerable.Repeat("retry after listener failure" + Environment.NewLine, 2)), console.ToString());
    }

    private sealed record Entry(LogLevel Level, string Message, Exception? Error, IReadOnlyDictionary<string, object?> Values);
    private sealed class CaptureLogger : ILogger, IDisposable
    {
        internal ConcurrentQueue<Entry> Entries { get; } = new();
        internal bool Enabled { get; set; } = true;
        internal Exception? EnabledFailure { get; set; }
        internal Exception? LogFailure { get; set; }
        internal Action? OnLog { get; init; }
        internal int Disposals;
        public bool IsEnabled(LogLevel logLevel) => EnabledFailure is { } error ? throw error : Enabled;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (LogFailure is { } error) throw error;
            OnLog?.Invoke();
            Entries.Enqueue(new(logLevel, formatter(state, exception), exception,
                state is IEnumerable<KeyValuePair<string, object?>> values ? values.ToDictionary(value => value.Key, value => value.Value) : new Dictionary<string, object?>()));
        }
        public void Dispose() => Disposals++;
    }
    private sealed class CaptureFactory : ILoggerFactory
    {
        internal CaptureLogger Logger { get; } = new();
        internal List<string> Categories { get; } = [];
        internal int Disposals;
        public ILogger CreateLogger(string categoryName) { Categories.Add(categoryName); return Logger; }
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public void Dispose() => Disposals++;
    }
    private sealed class ThrowingPayload
    {
        internal int Calls;
        public override string ToString() { Calls++; throw new InvalidOperationException("formatted disabled payload"); }
    }
    private sealed class CaptureTraceListener : System.Diagnostics.TraceListener
    {
        internal List<string> Messages { get; } = [];
        internal bool ThrowOnWrite { get; init; }
        public override void Write(string? message) { if (ThrowOnWrite) throw new IOException("trace listener"); if (message is not null) Messages.Add(message); }
        public override void WriteLine(string? message) { if (ThrowOnWrite) throw new IOException("trace listener"); if (message is not null) Messages.Add(message); }
    }
    private sealed class MalformedFactsModel(string output = "no array") : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: output);
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
