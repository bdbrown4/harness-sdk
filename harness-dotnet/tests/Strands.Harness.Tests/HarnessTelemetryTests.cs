using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Strands.Agents;
using Strands.OpenTelemetry.Tests;

namespace Strands.Harness.Tests;

[Collection("Harness telemetry environment")]
public sealed class HarnessTelemetryTests : IDisposable
{
    private readonly Dictionary<string, string?> _environment = EnvironmentNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
    private readonly TextWriter _output = Console.Out;
    private readonly StringWriter _capture = new(CultureInfo.InvariantCulture);

    public HarnessTelemetryTests()
    {
        HarnessTelemetry.ResetForTests();
        foreach (var name in EnvironmentNames) Environment.SetEnvironmentVariable(name, null);
        Console.SetOut(_capture);
    }

    private static readonly string[] EnvironmentNames =
    [
        "OTEL_TRACES_EXPORTER", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_HEADERS",
        "OTEL_EXPORTER_OTLP_PROTOCOL", "OTEL_EXPORTER_OTLP_TIMEOUT", "OTEL_EXPORTER_OTLP_COMPRESSION",
        "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_HEADERS",
        "OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", "OTEL_EXPORTER_OTLP_TRACES_TIMEOUT", "OTEL_EXPORTER_OTLP_TRACES_COMPRESSION",
        "OTEL_BSP_SCHEDULE_DELAY", "OTEL_BSP_MAX_EXPORT_BATCH_SIZE"
    ];

    [Fact]
    public async Task ConsoleSelectionExportsTheActualHarnessAgentActivity()
    {
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "console");
        await using var agent = await Harness.CreateAsync(Options());

        await agent.InvokeAsync("trace this operation");

        Assert.Contains("invoke_agent", _capture.ToString());
    }

    [Fact]
    public async Task ReentrantDiagnosticCallbacksCannotInstallDuplicateExporters()
    {
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "unknown,console");
        var calls = 0;
        using var listener = new CallbackListener(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) HarnessTelemetry.EnsureConfigured();
        });
        Trace.Listeners.Add(listener);
        try
        {
            await using var agent = await Harness.CreateAsync(Options());
            await agent.InvokeAsync("one operation");

            Assert.Equal(1, AgentExports());
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  , ")]
    [InlineData("none")]
    [InlineData("console,none")]
    [InlineData("otlp, NONE")]
    [InlineData("unknown")]
    public async Task DisabledSelectorsDoNotInstallAnExporter(string? selector)
    {
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", selector);
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", "invalid-protocol");
        await using var agent = await Harness.CreateAsync(Options());

        await agent.InvokeAsync("disabled");

        Assert.Equal(0, AgentExports());
    }

    [Fact]
    public async Task EndpointAloneDoesNotEnableOtlp()
    {
        using var collector = new OtlpLoopback();
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", collector.Endpoint.AbsoluteUri);
        await using var agent = await Harness.CreateAsync(Options());
        await agent.InvokeAsync("disabled");

        HarnessTelemetry.ResetForTests();

        Assert.False(collector.HasReceivedRequest);
    }

    [Fact]
    public async Task RepeatedMixedCaseSelectorsInstallOneConsoleExporter()
    {
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", " , CoNsOle,console, CONSOLE, ");
        await using var agent = await Harness.CreateAsync(Options());

        await agent.InvokeAsync("one operation");

        Assert.Equal(1, AgentExports());
    }

    [Fact]
    public async Task DisabledSetupAllowsLaterOptIn()
    {
        await using (var agent = await Harness.CreateAsync(Options())) await agent.InvokeAsync("off");
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "console");

        await using (var agent = await Harness.CreateAsync(Options())) await agent.InvokeAsync("on");

        Assert.Equal(1, AgentExports());
    }

    [Fact]
    public async Task CallerCanManageTelemetryWithoutSealingLaterHarnessSetup()
    {
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "console");
        await using (var agent = await Harness.CreateAsync(Options() with { UseEnvironmentTelemetry = false }))
            await agent.InvokeAsync("caller managed");
        Assert.Equal(0, AgentExports());

        await using (var agent = await Harness.CreateAsync(Options())) await agent.InvokeAsync("environment managed");

        Assert.Equal(1, AgentExports());
    }

    [Fact]
    public async Task ProcessExporterSurvivesAgentDisposalAndSubsequentEnvironmentChanges()
    {
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "console");
        await using (var agent = await Harness.CreateAsync(Options())) await agent.InvokeAsync("first");
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "none");

        await using (var agent = await Harness.CreateAsync(Options())) await agent.InvokeAsync("second");

        Assert.Equal(2, AgentExports());
    }

    [Fact]
    public async Task ConcurrentConstructionDoesNotStackExporters()
    {
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "console");
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            await using var agent = await Harness.CreateAsync(Options());
            await agent.InvokeAsync("concurrent operation");
        })).ToArray();
        start.SetResult(true);

        await Task.WhenAll(invocations);

        Assert.Equal(16, AgentExports());
    }

    [Fact]
    public async Task CancelledConstructionDoesNotInitializeTheProcessExporter()
    {
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "console");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Harness.CreateAsync(Options(), cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "none");
        await using var agent = await Harness.CreateAsync(Options());
        await agent.InvokeAsync("disabled");
        Assert.Equal(0, AgentExports());
    }

    [Fact]
    public async Task FailedSetupCanBeRetried()
    {
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "otlp");
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", "invalid-protocol");

        await Assert.ThrowsAsync<ArgumentException>(() => Harness.CreateAsync(Options()));
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", null);
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "console");
        await using var agent = await Harness.CreateAsync(Options());
        await agent.InvokeAsync("retry");
        Assert.Equal(1, AgentExports());
    }

    [Fact]
    public async Task UnknownSelectorWarningsAreDeduplicatedAndNoneTakesPrecedence()
    {
        var warnings = 0;
        using var listener = new CallbackListener(() => Interlocked.Increment(ref warnings));
        Trace.Listeners.Add(listener);
        try
        {
            Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "unknown,none");
            await using (var agent = await Harness.CreateAsync(Options())) { }
            Assert.Equal(0, warnings);
            Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "unknown,UNKNOWN");
            await using (var agent = await Harness.CreateAsync(Options())) { }
            var first = warnings;
            Assert.True(first > 0);

            await using (var agent = await Harness.CreateAsync(Options())) { }

            Assert.Equal(first, warnings);
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task DiagnosticListenerFailuresCannotFailAgentConstruction()
    {
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "unknown,console");
        using var listener = new CallbackListener(() => throw new InvalidOperationException("diagnostic failure"));
        Trace.Listeners.Add(listener);
        try
        {
            await using var agent = await Harness.CreateAsync(Options());
            await agent.InvokeAsync("application operation");

            Assert.Equal(1, AgentExports());
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task OtlpSelectionUsesPerSignalEnvironmentAndFlushesTheActualAgentTrace()
    {
        using var collector = new OtlpLoopback();
        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "otlp");
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", new Uri(collector.Endpoint, "/harness-traces").AbsoluteUri);
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", "http/protobuf");
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_HEADERS", "x-harness=environment");
        Environment.SetEnvironmentVariable("OTEL_BSP_SCHEDULE_DELAY", "60000");
        await using (var agent = await Harness.CreateAsync(Options())) await agent.InvokeAsync("actual trace");
        Assert.False(collector.HasReceivedRequest);

        HarnessTelemetry.ResetForTests();
        var request = await collector.ReceiveAsync();

        Assert.Equal("POST", request.Method);
        Assert.Equal("/harness-traces", request.Path);
        Assert.Equal("environment", request.Headers["x-harness"]);
        Assert.Equal("application/x-protobuf", request.Headers["Content-Type"]);
        Assert.Contains("invoke_agent", System.Text.Encoding.UTF8.GetString(request.Body));
        Assert.Equal(0, AgentExports());
    }

    private int AgentExports() => Regex.Matches(_capture.ToString(), @"(?m)^Activity\.DisplayName:\s+invoke_agent(?:\s|$)").Count;

    private sealed class CallbackListener(Action action) : TraceListener
    {
        public override void Write(string? message) => action();
        public override void WriteLine(string? message) => action();
    }

    private static HarnessOptions Options() => new()
    {
        Model = new TextModel(),
        BuiltinTools = [],
        BuiltinPlugins = [],
        BackgroundTasks = null,
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new() { Enabled = false }
    };

    public void Dispose()
    {
        try { HarnessTelemetry.ResetForTests(); }
        finally
        {
            foreach (var setting in _environment) Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            Console.SetOut(_output);
            _capture.Dispose();
        }
    }

    private sealed class TextModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}

[CollectionDefinition("Harness telemetry environment", DisableParallelization = true)]
public sealed class HarnessTelemetryEnvironmentCollection;
