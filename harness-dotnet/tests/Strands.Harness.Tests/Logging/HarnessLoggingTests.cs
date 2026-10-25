using Microsoft.Extensions.Logging;
using Strands.Agents;

namespace Strands.Harness.Tests.Logging;

[CollectionDefinition("Harness logging", DisableParallelization = true)]
public sealed class HarnessLoggingCollection { }

[Collection("Harness logging")]
public sealed class HarnessLoggingTests : IDisposable
{
    private readonly ILogger _sdk = SdkLogging.Logger;
    private readonly ILogger _harness = HarnessLogging.Logger;
    public void Dispose()
    {
        SdkLogging.ConfigureLogging(_sdk);
        HarnessLogging.ConfigureLogging(_harness);
    }

    [Fact]
    public void ARealHarnessDiagnosticUsesItsSeparateConfiguredLogger()
    {
        var sdk = new CaptureLogger();
        var harness = new CaptureLogger();
        SdkLogging.ConfigureLogging(sdk);
        HarnessLogging.ConfigureLogging(harness);

        HarnessAgentConfig.Parse("{\"futureSetting\":true}");

        Assert.Empty(sdk.Messages);
        Assert.Contains("futureSetting", Assert.Single(harness.Messages));
    }

    [Fact]
    public void HarnessDeduplicationSurvivesReplacementWithoutUsingTheSdkLogger()
    {
        var sdk = new CaptureLogger();
        var harness = new CaptureLogger();
        var message = "shared message " + Guid.NewGuid();
        SdkLogging.ConfigureLogging(sdk);
        HarnessLogging.ConfigureLogging(harness);
        HarnessLogging.WarnOnce(message);
        HarnessLogging.WarnOnce(message);
        var replacement = new CaptureLogger();
        HarnessLogging.ConfigureLogging(replacement);
        HarnessLogging.WarnOnce(message);
        Assert.Empty(sdk.Messages);
        Assert.Equal(message, Assert.Single(harness.Messages));
        Assert.Empty(replacement.Messages);
    }

    [Fact]
    public void HarnessFactoriesUseTheirOwnCategoryAndRemainBorrowed()
    {
        using var factory = new CaptureFactory();
        HarnessLogging.ConfigureLogging(factory);
        Assert.Equal(HarnessLogging.CategoryName, factory.Category);
        HarnessLogging.ConfigureLogging(new CaptureLogger());
        Assert.Equal(0, factory.Disposals);
    }

    [Fact]
    public void HostFailureCannotChangeConfigurationValidationAndUnknownKeyFiltering()
    {
        HarnessLogging.ConfigureLogging(new CaptureLogger { Failure = new IOException("logger") });
        var config = HarnessAgentConfig.Parse("{\"future\":42,\"name\":\"kept\"}");
        Assert.Equal("kept", config.Name);
        Assert.False(config.ToJsonElement().TryGetProperty("future", out _));
        Assert.Throws<ArgumentException>(() => HarnessAgentConfig.Parse("{\"model\":42}"));
    }

    [Fact]
    public void DisabledHarnessWarningRemainsDeduplicatedAfterEnabling()
    {
        var logger = new CaptureLogger { Enabled = false };
        HarnessLogging.ConfigureLogging(logger);
        var message = "disabled harness " + Guid.NewGuid();
        HarnessLogging.WarnOnce(message);
        logger.Enabled = true;
        HarnessLogging.WarnOnce(message);
        Assert.Empty(logger.Messages);
    }

    private sealed class CaptureLogger : ILogger
    {
        internal List<string> Messages { get; } = [];
        internal bool Enabled { get; set; } = true;
        internal Exception? Failure { get; init; }
        public bool IsEnabled(LogLevel logLevel) => Enabled;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (Failure is { } error) throw error; Messages.Add(formatter(state, exception)); }
    }
    private sealed class CaptureFactory : ILoggerFactory
    {
        internal string? Category;
        internal int Disposals;
        public ILogger CreateLogger(string categoryName) { Category = categoryName; return new CaptureLogger(); }
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public void Dispose() => Disposals++;
    }
}
