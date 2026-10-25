using Microsoft.Extensions.Logging;
using Strands.Agents;
using Strands.Harness.Mcp;
using Strands.Harness.Testing;

namespace Strands.Harness.Tests.Logging;

[Collection("Harness logging")]
public sealed class McpHarnessLoggingClosureTests : IDisposable
{
    private readonly ILogger _sdk = SdkLogging.Logger;
    private readonly ILogger _harness = HarnessLogging.Logger;
    public void Dispose() { SdkLogging.ConfigureLogging(_sdk); HarnessLogging.ConfigureLogging(_harness); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkippedConfigurationUsesTheHarnessLoggerWithTheOriginalValidationCause(bool throwingLogger)
    {
        var sdk = new Host(); var logger = new Host(throwingLogger); SdkLogging.ConfigureLogging(sdk); HarnessLogging.ConfigureLogging(logger);
        var configuration = McpServerConfiguration.Parse("""{"broken":{"transport":"not-a-transport"}}""", continueOnError: true);
        Assert.Empty(await configuration.ResolveAsync(new LocalFileSandbox(), default));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("server=<broken>, error_type=<ArgumentException> | MCP server config failed, skipping", entry.Message);
        Assert.IsType<ArgumentException>(entry.Error);
        Assert.Empty(sdk.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkippedConnectionUsesTheHarnessLoggerWithoutChangingContinueOnError(bool throwingLogger)
    {
        var sdk = new Host(); var logger = new Host(throwingLogger); SdkLogging.ConfigureLogging(sdk); HarnessLogging.ConfigureLogging(logger);
        await using var server = new McpTestServer { FailInitialization = true };
        var configuration = McpServerConfiguration.Parse("{\"broken\":{\"url\":\"" + server.Endpoint + "\"}}", continueOnError: true);
        var acquired = new List<IAsyncDisposable>();
        Assert.Empty(await McpConnections.ConnectAsync(configuration, new LocalFileSandbox(), acquired, default));
        Assert.Empty(acquired);
        var entry = Assert.Single(logger.Entries);
        Assert.StartsWith("server=<broken>, error_type=<", entry.Message);
        Assert.EndsWith(" | MCP server failed to connect, skipping", entry.Message);
        Assert.NotNull(entry.Error);
        Assert.Contains("fixture initialization failure", entry.Error!.Message);
        Assert.Empty(sdk.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IgnoredAuthFieldsUseTheHarnessLoggerWithoutSerializingCredentials(bool throwingLogger)
    {
        var sdk = new Host(); var logger = new Host(throwingLogger); SdkLogging.ConfigureLogging(sdk); HarnessLogging.ConfigureLogging(logger);
        var configuration = McpServerConfiguration.Parse("""{"remote":{"url":"https://example.test/mcp","auth":{"clientId":"client","clientSecret":"do-not-log-secret","futureField":"do-not-log-value"}}}""", continueOnError: false);
        var remote = Assert.Single(await configuration.ResolveAsync(new LocalFileSandbox(), default));
        Assert.Equal("do-not-log-secret", remote.Options.Auth!.ClientSecret);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("MCP client-credentials auth ignores unknown field: futureField", entry.Message);
        Assert.Null(entry.Error);
        Assert.DoesNotContain("do-not-log", entry.Message);
        Assert.Empty(sdk.Entries);
    }

    [Fact]
    public async Task ConfigurationErrorsStillFailClosedWithoutLoggingWhenContinueOnErrorIsFalse()
    {
        var logger = new Host(); HarnessLogging.ConfigureLogging(logger);
        var config = McpServerConfiguration.Parse("""{"broken":{"transport":"not-a-transport"}}""", continueOnError: false);
        await Assert.ThrowsAsync<ArgumentException>(() => config.ResolveAsync(new LocalFileSandbox(), default));
        Assert.Empty(logger.Entries);
    }

    private sealed record Entry(string Message, Exception? Error);
    private sealed class Host(bool throwing = false) : ILogger
    {
        internal List<Entry> Entries { get; } = [];
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) { Entries.Add(new(formatter(state, error), error)); if (throwing) throw new IOException("logger failure"); }
    }
}
