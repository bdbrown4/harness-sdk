using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Strands.Agents.Telemetry;

namespace Strands.OpenTelemetry.Tests;

public sealed class TelemetrySetupTests
{
    [Fact]
    public async Task ConsoleAndOtlpMetricExportersHaveIndependentConfiguredReaders()
    {
        using var environment = UseDefaultOtlpEnvironment();
        using var collector = new OtlpLoopback();
        var readers = new List<MetricReaderOptions>();
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            using var provider = Sdk.CreateMeterProviderBuilder().AddStrandsInstrumentation(new()
            {
                ConsoleExporter = true,
                OtlpExporter = true,
                ConfigureOtlpExporter = exporter =>
                {
                    exporter.Endpoint = new Uri(collector.Endpoint, "/v1/metrics");
                    exporter.TimeoutMilliseconds = 3000;
                },
                ConfigureMetricReader = reader =>
                {
                    readers.Add(reader);
                    reader.TemporalityPreference = MetricReaderTemporalityPreference.Delta;
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 60000;
                }
            }).Build();
            Assert.Equal(2, readers.Count);
            Assert.NotSame(readers[0], readers[1]);
            new AgentMeter().StartNewInvocation();
            Assert.True(provider.ForceFlush(5000));
            Assert.Contains("gen_ai.agent.invocation.count", writer.ToString());
            var request = await collector.ReceiveAsync();
            Assert.Equal("/v1/metrics", request.Path);
            Assert.NotEmpty(LengthDelimitedFields(request.Body, 1));
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public async Task CallerDisposalFlushesQueuedOtlpSpans()
    {
        using var environment = UseDefaultOtlpEnvironment();
        using var collector = new OtlpLoopback();
        var provider = Sdk.CreateTracerProviderBuilder().AddStrandsInstrumentation(new()
        {
            OtlpExporter = true,
            ConfigureOtlpExporter = exporter =>
            {
                exporter.Endpoint = new Uri(collector.Endpoint, "/v1/traces");
                exporter.TimeoutMilliseconds = 3000;
                exporter.BatchExportProcessorOptions.ScheduledDelayMilliseconds = 60000;
            }
        }).Build();
        try
        {
            new Strands.Agents.Telemetry.Tracer().StartSpan("caller disposal flush").End();
            Assert.False(collector.HasReceivedRequest);
        }
        finally { provider.Dispose(); }
        var request = await collector.ReceiveAsync();
        var resource = Assert.Single(LengthDelimitedFields(request.Body, 1));
        var scope = Assert.Single(LengthDelimitedFields(resource, 2));
        var span = Assert.Single(LengthDelimitedFields(scope, 2));
        Assert.Equal("caller disposal flush", Encoding.UTF8.GetString(Assert.Single(LengthDelimitedFields(span, 5))));
    }

    [Theory]
    [InlineData(500, false)]
    [InlineData(200, true)]
    public async Task CollectorFailureCannotReplaceApplicationOperationsAndShutdownIsBounded(int statusCode, bool withholdResponse)
    {
        using var environment = UseDefaultOtlpEnvironment();
        using var collector = new OtlpLoopback(statusCode, withholdResponse);
        // Cancel a proven in-flight stalled request. A whole-request timer can
        // legitimately expire before the collector has received any bytes.
        using var heldClient = withholdResponse
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan }
            : null;
        var provider = Sdk.CreateTracerProviderBuilder().AddStrandsInstrumentation(new()
        {
            OtlpExporter = true,
            ConfigureOtlpExporter = exporter =>
            {
                exporter.Endpoint = new Uri(collector.Endpoint, "/v1/traces");
                exporter.TimeoutMilliseconds = 250;
                exporter.BatchExportProcessorOptions.ScheduledDelayMilliseconds = 60000;
                if (heldClient is { } client) exporter.HttpClientFactory = () => client;
            }
        }).Build();
        var elapsed = Stopwatch.StartNew();
        Task<bool>? flush = null;
        OtlpLoopback.Request? request = null;
        Exception? primary = null;
        var cleanupErrors = new List<Exception>();
        try
        {
            Assert.Null(Record.Exception(() => new Strands.Agents.Telemetry.Tracer().StartSpan("application survives").End()));
            if (heldClient is not null)
            {
                flush = Task.Run(() => provider.ForceFlush(3000));
                request = await collector.ReceiveAsync();
                Assert.False(flush.IsCompleted, "The admitted request must still be waiting for the withheld response.");
                heldClient.CancelPendingRequests();
                await flush;
            }
            else
            {
                provider.ForceFlush(3000);
                request = await collector.ReceiveAsync();
            }
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { heldClient?.CancelPendingRequests(); } catch (Exception error) { cleanupErrors.Add(error); }
            try { if (flush is not null) await flush; } catch (Exception error) { cleanupErrors.Add(error); }
            try { provider.Dispose(); } catch (Exception error) { cleanupErrors.Add(error); }
            if (cleanupErrors.Count > 0)
            {
                if (primary is not null) primary.Data["Strands.CleanupErrors"] = cleanupErrors.ToArray();
                else throw new AggregateException(cleanupErrors);
            }
        }
        elapsed.Stop();
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), "Exporter shutdown exceeded its bounded test deadline.");
        Assert.NotNull(request);
        Assert.Equal("/v1/traces", request.Path);
        Assert.NotEmpty(LengthDelimitedFields(request.Body, 1));
    }

    [Theory]
    [InlineData("PROTOCOL", "unsupported")]
    [InlineData("ENDPOINT", "relative-path")]
    [InlineData("TIMEOUT", "-1")]
    [InlineData("COMPRESSION", "unsupported")]
    public void InvalidSignalSpecificConfigurationFailsDuringSetup(string setting, string value)
    {
        using var defaults = UseDefaultOtlpEnvironment();
        using var environment = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_TRACES_" + setting] = value
        });
        var exception = Assert.Throws<ArgumentException>(() =>
            Sdk.CreateTracerProviderBuilder().AddStrandsInstrumentation(new() { OtlpExporter = true }).Build());
        Assert.Contains("OTEL_EXPORTER_OTLP_TRACES_" + setting, exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignalSpecificOtlpEnvironmentOverridesGlobalSettingsAndExports(bool metrics)
    {
        using var defaults = UseDefaultOtlpEnvironment();
        using var collector = new OtlpLoopback();
        var signal = metrics ? "METRICS" : "TRACES";
        var path = metrics ? "/v1/metrics" : "/v1/traces";
        using var environment = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1/global",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "wrong=global",
            ["OTEL_EXPORTER_OTLP_TIMEOUT"] = "1",
            ["OTEL_EXPORTER_OTLP_COMPRESSION"] = null,
            ["OTEL_EXPORTER_OTLP_" + signal + "_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_" + signal + "_ENDPOINT"] = new Uri(collector.Endpoint, path).AbsoluteUri,
            ["OTEL_EXPORTER_OTLP_" + signal + "_HEADERS"] = "x-strands-env=signal",
            ["OTEL_EXPORTER_OTLP_" + signal + "_TIMEOUT"] = "3000",
            ["OTEL_EXPORTER_OTLP_" + signal + "_COMPRESSION"] = "gzip"
        });
        if (metrics)
        {
            using var provider = Sdk.CreateMeterProviderBuilder().AddStrandsInstrumentation(new() { OtlpExporter = true }).Build();
            new AgentMeter().StartNewInvocation();
            Assert.True(provider.ForceFlush(5000));
        }
        else
        {
            using var provider = Sdk.CreateTracerProviderBuilder().AddStrandsInstrumentation(new() { OtlpExporter = true }).Build();
            new Strands.Agents.Telemetry.Tracer().StartSpan("environment.trace").End();
            Assert.True(provider.ForceFlush(5000));
        }
        var request = await collector.ReceiveAsync();
        Assert.Equal(path, request.Path);
        Assert.Equal("signal", request.Headers["x-strands-env"]);
        Assert.False(request.Headers.ContainsKey("wrong"));
        Assert.Contains("application/x-protobuf", request.Headers["Content-Type"]);
        Assert.Equal("gzip", request.Headers["Content-Encoding"]);
        using var compressed = new MemoryStream(request.Body);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var body = new MemoryStream();
        gzip.CopyTo(body);
        Assert.NotEmpty(LengthDelimitedFields(body.ToArray(), 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitOtlpCallbackOverridesSignalSpecificEnvironment(bool metrics)
    {
        using var defaults = UseDefaultOtlpEnvironment();
        using var collector = new OtlpLoopback();
        var signal = metrics ? "METRICS" : "TRACES";
        var path = metrics ? "/chosen/metrics" : "/chosen/traces";
        using var environment = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc",
            ["OTEL_EXPORTER_OTLP_" + signal + "_PROTOCOL"] = "grpc",
            ["OTEL_EXPORTER_OTLP_" + signal + "_ENDPOINT"] = "http://127.0.0.1:1/unused",
            ["OTEL_EXPORTER_OTLP_" + signal + "_HEADERS"] = "wrong=signal",
            ["OTEL_EXPORTER_OTLP_" + signal + "_TIMEOUT"] = "1"
        });
        var options = new StrandsTelemetryOptions
        {
            OtlpExporter = true,
            ConfigureOtlpExporter = exporter =>
            {
                exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                exporter.Endpoint = new Uri(collector.Endpoint, path);
                exporter.Headers = "x-strands-env=callback";
                exporter.TimeoutMilliseconds = 3000;
            }
        };
        if (metrics)
        {
            using var provider = Sdk.CreateMeterProviderBuilder().AddStrandsInstrumentation(options).Build();
            new AgentMeter().StartNewInvocation();
            Assert.True(provider.ForceFlush(5000));
        }
        else
        {
            using var provider = Sdk.CreateTracerProviderBuilder().AddStrandsInstrumentation(options).Build();
            new Strands.Agents.Telemetry.Tracer().StartSpan("callback.trace").End();
            Assert.True(provider.ForceFlush(5000));
        }
        var request = await collector.ReceiveAsync();
        Assert.Equal(path, request.Path);
        Assert.Equal("callback", request.Headers["x-strands-env"]);
        Assert.False(request.Headers.ContainsKey("wrong"));
    }

    [Fact]
    public void InstrumentationPreservesApplicationResourceIdentityUnlessExplicitlyOverridden()
    {
        using var provider = Sdk.CreateTracerProviderBuilder()
            .ConfigureResource(resource => resource.AddService("host-service", serviceVersion: "9.0")
                .AddAttributes(new Dictionary<string, object> { ["host.region"] = "west" }))
            .AddStrandsInstrumentation().Build();
        Assert.Contains(provider.GetResource().Attributes, pair => pair.Key == "service.name" && Equals(pair.Value, "host-service"));
        Assert.Contains(provider.GetResource().Attributes, pair => pair.Key == "service.version" && Equals(pair.Value, "9.0"));
        Assert.Contains(provider.GetResource().Attributes, pair => pair.Key == "host.region" && Equals(pair.Value, "west"));
        using var overridden = Sdk.CreateTracerProviderBuilder()
            .ConfigureResource(resource => resource.AddService("host-service"))
            .AddStrandsInstrumentation(new() { ServiceName = "chosen-service" }).Build();
        Assert.Contains(overridden.GetResource().Attributes, pair => pair.Key == "service.name" && Equals(pair.Value, "chosen-service"));
    }

    [Fact]
    public void ResourceEnvironmentAndExplicitOverridesUseStandardPrecedence()
    {
        using var environment = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["OTEL_RESOURCE_ATTRIBUTES"] = "service.name=resource-name,service.namespace=resource-namespace,deployment.environment=staging,tenant=environment",
            ["OTEL_SERVICE_NAME"] = "service-name",
            ["OTEL_SERVICE_NAMESPACE"] = "namespace-fallback",
            ["OTEL_DEPLOYMENT_ENVIRONMENT"] = "environment-fallback"
        });
        var detected = StrandsTelemetryExtensions.CreateResourceBuilder().Build();
        Assert.Contains(detected.Attributes, pair => pair.Key == "service.name" && Equals(pair.Value, "service-name"));
        Assert.Contains(detected.Attributes, pair => pair.Key == "service.namespace" && Equals(pair.Value, "resource-namespace"));
        Assert.Contains(detected.Attributes, pair => pair.Key == "deployment.environment" && Equals(pair.Value, "staging"));
        var explicitResource = StrandsTelemetryExtensions.CreateResourceBuilder(new()
        {
            ServiceName = "explicit-name",
            ResourceAttributes = new Dictionary<string, object> { ["tenant"] = "explicit" }
        }).Build();
        Assert.Contains(explicitResource.Attributes, pair => pair.Key == "service.name" && Equals(pair.Value, "explicit-name"));
        Assert.Contains(explicitResource.Attributes, pair => pair.Key == "tenant" && Equals(pair.Value, "explicit"));
    }

    [Fact]
    public void ResourceDefaultsIdentifyTheNativeDotNetSdkAndDetachArrayValues()
    {
        using var environment = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["OTEL_RESOURCE_ATTRIBUTES"] = null,
            ["OTEL_SERVICE_NAME"] = null,
            ["OTEL_SERVICE_NAMESPACE"] = null,
            ["OTEL_DEPLOYMENT_ENVIRONMENT"] = null
        });
        var array = new[] { "first", "second" };
        var resource = StrandsTelemetryExtensions.CreateResourceBuilder(new()
        { ResourceAttributes = new Dictionary<string, object> { ["regions"] = array } }).Build();
        array[0] = "changed";
        Assert.Contains(resource.Attributes, pair => pair.Key == "service.name" && Equals(pair.Value, "strands-agents"));
        Assert.Contains(resource.Attributes, pair => pair.Key == "service.namespace" && Equals(pair.Value, "strands"));
        Assert.Contains(resource.Attributes, pair => pair.Key == "telemetry.sdk.language" && Equals(pair.Value, "dotnet"));
        Assert.Equal("first", Assert.IsType<string[]>(Assert.Single(resource.Attributes, pair => pair.Key == "regions").Value)[0]);
    }

    [Fact]
    public void BaggageFailureCannotReplaceApplicationSpanAndCaptureAllIsOptIn()
    {
        var prior = Baggage.Current;
        try
        {
            Baggage.Current = default;
            Baggage.SetBaggage("public.key", "value");
            var spans = new List<Activity>();
            using (var provider = Sdk.CreateTracerProviderBuilder().AddStrandsInstrumentation(new()
            { BaggageKeyPredicate = _ => throw new InvalidOperationException("predicate failed") })
                .AddProcessor(new SimpleActivityExportProcessor(new CaptureExporter<Activity>(spans))).Build())
                new Strands.Agents.Telemetry.Tracer().StartSpan("survives predicate").End();
            Assert.Equal("survives predicate", Assert.Single(spans).DisplayName);
            Assert.Null(spans[0].GetTagItem("public.key"));
            spans.Clear();
            using (var provider = Sdk.CreateTracerProviderBuilder().AddStrandsInstrumentation(new() { CaptureBaggage = true })
                .AddProcessor(new SimpleActivityExportProcessor(new CaptureExporter<Activity>(spans))).Build())
                new Strands.Agents.Telemetry.Tracer().StartSpan("captures baggage").End();
            Assert.Equal("value", Assert.Single(spans).GetTagItem("public.key"));
        }
        finally { Baggage.Current = prior; }
    }

    [Fact]
    public void InvalidSetupFailsBeforeBuildingAProvider()
    {
        Assert.Throws<ArgumentNullException>(() => StrandsTelemetryExtensions.AddStrandsInstrumentation((TracerProviderBuilder)null!));
        Assert.Throws<ArgumentNullException>(() => StrandsTelemetryExtensions.AddStrandsInstrumentation((MeterProviderBuilder)null!));
        Assert.Throws<ArgumentException>(() => StrandsTelemetryExtensions.CreateResourceBuilder(new() { ServiceName = " " }));
        Assert.Throws<ArgumentException>(() => StrandsTelemetryExtensions.CreateResourceBuilder(new() { ResourceAttributes = null! }));
    }

    [Fact]
    public async Task OtlpTraceExporterSendsGenuineHttpProtobufToTheConfiguredCollector()
    {
        using var environment = UseDefaultOtlpEnvironment();
        using var collector = new OtlpLoopback();
        using var provider = Sdk.CreateTracerProviderBuilder().AddStrandsInstrumentation(new()
        {
            OtlpExporter = true,
            ConfigureOtlpExporter = exporter =>
            {
                Assert.Equal(OtlpExportProtocol.HttpProtobuf, exporter.Protocol);
                exporter.Endpoint = new Uri(collector.Endpoint, "/v1/traces");
                exporter.Headers = "x-strands-test=synthetic";
                exporter.TimeoutMilliseconds = 3000;
            }
        }).Build();
        new Strands.Agents.Telemetry.Tracer().StartSpan("socket.trace").End();
        Assert.True(provider.ForceFlush(5000));
        var request = await collector.ReceiveAsync();
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/traces", request.Path);
        Assert.Equal("synthetic", request.Headers["x-strands-test"]);
        Assert.Contains("application/x-protobuf", request.Headers["Content-Type"]);
        var resource = Assert.Single(LengthDelimitedFields(request.Body, 1));
        var scope = Assert.Single(LengthDelimitedFields(resource, 2));
        var span = Assert.Single(LengthDelimitedFields(scope, 2));
        Assert.Equal("socket.trace", Encoding.UTF8.GetString(Assert.Single(LengthDelimitedFields(span, 5))));
        Assert.Equal(16, Assert.Single(LengthDelimitedFields(span, 1)).Length);
        Assert.Equal(8, Assert.Single(LengthDelimitedFields(span, 2)).Length);
    }

    [Fact]
    public async Task OtlpMetricExporterSendsGenuineHttpProtobufToTheConfiguredCollector()
    {
        using var environment = UseDefaultOtlpEnvironment();
        using var collector = new OtlpLoopback();
        using var provider = Sdk.CreateMeterProviderBuilder().AddStrandsInstrumentation(new()
        {
            OtlpExporter = true,
            ConfigureOtlpExporter = exporter =>
            {
                Assert.Equal(OtlpExportProtocol.HttpProtobuf, exporter.Protocol);
                exporter.Endpoint = new Uri(collector.Endpoint, "/v1/metrics");
                exporter.Headers = "x-strands-test=synthetic";
                exporter.TimeoutMilliseconds = 3000;
            }
        }).Build();
        new AgentMeter().StartNewInvocation();
        Assert.True(provider.ForceFlush(5000));
        var request = await collector.ReceiveAsync();
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/metrics", request.Path);
        Assert.Equal("synthetic", request.Headers["x-strands-test"]);
        Assert.Contains("application/x-protobuf", request.Headers["Content-Type"]);
        var resource = Assert.Single(LengthDelimitedFields(request.Body, 1));
        var scope = Assert.Single(LengthDelimitedFields(resource, 2));
        var metric = Assert.Single(LengthDelimitedFields(scope, 2));
        Assert.Equal("gen_ai.agent.invocation.count", Encoding.UTF8.GetString(Assert.Single(LengthDelimitedFields(metric, 1))));
        Assert.NotEmpty(LengthDelimitedFields(metric, 7));
    }

    [Fact]
    public void ExplicitConsoleTraceExporterWritesRealStrandsSpans()
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            using var provider = Sdk.CreateTracerProviderBuilder().AddStrandsInstrumentation(new() { ConsoleExporter = true }).Build();
            new Strands.Agents.Telemetry.Tracer().StartSpan("console trace check").End();
            Assert.True(provider.ForceFlush());
            Assert.Contains("console trace check", writer.ToString());
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public void ExplicitConsoleMetricExporterWritesRealStrandsMeasurements()
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            using var provider = Sdk.CreateMeterProviderBuilder().AddStrandsInstrumentation(new() { ConsoleExporter = true }).Build();
            new AgentMeter().StartNewInvocation();
            Assert.True(provider.ForceFlush());
            Assert.Contains("gen_ai.agent.invocation.count", writer.ToString());
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public void BaggagePredicateStampsOnlySelectedAmbientKeysAtSpanStart()
    {
        var prior = Baggage.Current;
        try
        {
            Baggage.Current = default;
            Baggage.SetBaggage("tenant.id", "seven");
            Baggage.SetBaggage("private.token", "omitted");
            var exported = new List<Activity>();
            using var provider = Sdk.CreateTracerProviderBuilder()
                .AddStrandsInstrumentation(new() { BaggageKeyPredicate = key => key.StartsWith("tenant.", StringComparison.Ordinal) })
                .AddProcessor(new SimpleActivityExportProcessor(new CaptureExporter<Activity>(exported))).Build();
            new Strands.Agents.Telemetry.Tracer().StartSpan("baggage check").End();
            var span = Assert.Single(exported);
            Assert.Equal("seven", span.GetTagItem("tenant.id"));
            Assert.Null(span.GetTagItem("private.token"));
        }
        finally { Baggage.Current = prior; }
    }

    [Fact]
    public void RealTracerProviderCollectsStrandsSpansAndRemainsCallerOwned()
    {
        var exported = new List<Activity>();
        var builder = Sdk.CreateTracerProviderBuilder();
        Assert.Same(builder, builder.AddStrandsInstrumentation());
        using var provider = builder.AddProcessor(new SimpleActivityExportProcessor(new CaptureExporter<Activity>(exported))).Build();
        new Strands.Agents.Telemetry.Tracer().StartSpan("setup operation").End();
        Assert.True(provider.ForceFlush());
        Assert.Equal("setup operation", Assert.Single(exported).DisplayName);
    }

    [Fact]
    public void RealMeterProviderCollectsStrandsCounters()
    {
        var exported = new List<Metric>();
        var builder = Sdk.CreateMeterProviderBuilder();
        Assert.Same(builder, builder.AddStrandsInstrumentation());
        using var provider = builder.AddReader(new BaseExportingMetricReader(new CaptureExporter<Metric>(exported))).Build();
        new AgentMeter().StartNewInvocation();
        Assert.True(provider.ForceFlush());
        var metric = Assert.Single(exported, value => value.Name == "gen_ai.agent.invocation.count");
        var points = new List<long>();
        foreach (var point in metric.GetMetricPoints()) points.Add(point.GetSumLong());
        Assert.Equal(1, Assert.Single(points));
    }

    [Fact]
    public void ResourceCarriesExplicitApplicationIdentityAndDetachedAttributes()
    {
        var attributes = new Dictionary<string, object> { ["tenant"] = "first" };
        var resource = StrandsTelemetryExtensions.CreateResourceBuilder(new()
        {
            ServiceName = "orders",
            ServiceNamespace = "native",
            ServiceVersion = "2.0",
            DeploymentEnvironment = "production",
            ResourceAttributes = attributes
        }).Build();
        attributes["tenant"] = "later";
        Assert.Contains(resource.Attributes, pair => pair.Key == "service.name" && Equals(pair.Value, "orders"));
        Assert.Contains(resource.Attributes, pair => pair.Key == "service.namespace" && Equals(pair.Value, "native"));
        Assert.Contains(resource.Attributes, pair => pair.Key == "service.version" && Equals(pair.Value, "2.0"));
        Assert.Contains(resource.Attributes, pair => pair.Key == "deployment.environment" && Equals(pair.Value, "production"));
        Assert.Contains(resource.Attributes, pair => pair.Key == "tenant" && Equals(pair.Value, "first"));
    }

    private sealed class CaptureExporter<T>(List<T> values) : BaseExporter<T> where T : class
    {
        public override ExportResult Export(in Batch<T> batch)
        {
            foreach (var value in batch) values.Add(value);
            return ExportResult.Success;
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _prior = new(StringComparer.Ordinal);
        internal EnvironmentScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var value in values)
            {
                _prior.Add(value.Key, Environment.GetEnvironmentVariable(value.Key));
                Environment.SetEnvironmentVariable(value.Key, value.Value);
            }
        }
        public void Dispose()
        {
            foreach (var value in _prior) Environment.SetEnvironmentVariable(value.Key, value.Value);
        }
    }

    private static EnvironmentScope UseDefaultOtlpEnvironment()
    {
        var values = new Dictionary<string, string?>();
        foreach (var signal in new[] { "", "TRACES_", "METRICS_" })
            foreach (var setting in new[] { "PROTOCOL", "ENDPOINT", "HEADERS", "TIMEOUT", "COMPRESSION" })
                values.Add("OTEL_EXPORTER_OTLP_" + signal + setting, null);
        return new EnvironmentScope(values);
    }

    // Field numbers follow the official OTLP trace/metrics protobuf schemas. Decode
    // only the envelope fields needed to verify real, structured exporter payloads.
    private static IReadOnlyList<byte[]> LengthDelimitedFields(byte[] message, int requestedField)
    {
        var fields = new List<byte[]>();
        var offset = 0;
        while (offset < message.Length)
        {
            var tag = ReadVarint(message, ref offset);
            var number = checked((int)(tag >> 3));
            var wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: ReadVarint(message, ref offset); break;
                case 1: offset = checked(offset + 8); break;
                case 5: offset = checked(offset + 4); break;
                case 2:
                    var length = checked((int)ReadVarint(message, ref offset));
                    if (length > message.Length - offset) throw new InvalidDataException("Truncated OTLP field.");
                    if (number == requestedField)
                    {
                        var bytes = new byte[length];
                        Array.Copy(message, offset, bytes, 0, length);
                        fields.Add(bytes);
                    }
                    offset += length;
                    break;
                default: throw new InvalidDataException("Unsupported OTLP wire field.");
            }
            if (offset > message.Length) throw new InvalidDataException("Truncated OTLP field.");
        }
        return fields;
    }

    private static ulong ReadVarint(byte[] bytes, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= bytes.Length) throw new InvalidDataException("Truncated OTLP integer.");
            var current = bytes[offset++];
            value |= (ulong)(current & 127) << shift;
            if ((current & 128) == 0) return value;
        }
        throw new InvalidDataException("Invalid OTLP integer.");
    }
}
