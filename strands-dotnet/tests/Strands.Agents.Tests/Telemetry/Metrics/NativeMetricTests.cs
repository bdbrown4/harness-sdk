using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry.Metrics;

[Collection("Telemetry listeners")]
public sealed class NativeMetricTests
{
    [Fact]
    public void MeasurementCallbacksCanReadCommittedStateFromAnotherThread()
    {
        var name = "application." + Guid.NewGuid().ToString("N");
        using var source = new Meter(name);
        var meter = new AgentMeter(meter: source);
        using var listener = new MeterListener();
        var observations = new ConcurrentQueue<bool>();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == name) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) =>
        {
            if (instrument.Name != "gen_ai.agent.cycle.count") return;
            var read = Task.Run(() => meter.Metrics.CycleCount);
            observations.Enqueue(read.Wait(TimeSpan.FromSeconds(2)) && read.Result == 1);
        });
        listener.Start();
        meter.StartNewInvocation();
        meter.StartCycle().End(TimeSpan.FromMilliseconds(10));
        Assert.True(Assert.Single(observations));
    }

    [Fact]
    public void TokenInstrumentsPreserveInt64PrecisionForBothConventions()
    {
        var name = "application." + Guid.NewGuid().ToString("N");
        using var source = new Meter(name);
        using var listener = new MeterListener();
        var values = new Dictionary<string, long>();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == name) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => values.Add(instrument.Name, value));
        listener.Start();
        const long tokens = 9_007_199_254_740_993;
        new AgentMeter(meter: source).UpdateCycle(new Usage(tokens));
        Assert.Equal(tokens, values["gen_ai.agent.tokens.input"]);
        Assert.Equal(tokens, values["strands.event_loop.input.tokens"]);
    }

    [Fact]
    public void ZeroFirstTokenTimingIsRetainedOnlyByThePythonConvention()
    {
        using var listener = new Measurements();
        new AgentMeter().UpdateCycle(metrics: new(TimeSpan.Zero, TimeSpan.Zero));
        listener.AssertOne("strands.model.time_to_first_token", 0, "ms");
        Assert.DoesNotContain(listener.Values, value => value.Name == "gen_ai.server.time_to_first_token");
    }

    [Fact]
    public void RepeatedEmissionsReuseInstrumentInstances()
    {
        var name = "application." + Guid.NewGuid().ToString("N");
        using var source = new Meter(name);
        using var listener = new MeterListener();
        var publications = new ConcurrentDictionary<string, int>();
        var measured = 0;
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name != name) return;
            publications.AddOrUpdate(instrument.Name, 1, (_, count) => count + 1);
            owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref measured));
        listener.Start();
        var meter = new AgentMeter(meter: source);
        for (var index = 0; index < 100; index++) meter.StartNewInvocation();
        Assert.Equal(1, publications["gen_ai.agent.invocation.count"]);
        Assert.Equal(100, measured);
        Assert.Equal(50, meter.Metrics.AgentInvocations.Count);
    }

    [Fact]
    public async Task ConcurrentCompletionEmitsExactlyOnePhysicalCycleEnd()
    {
        using var listener = new Measurements();
        var meter = new AgentMeter();
        var cycle = meter.StartCycle();
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => cycle.End(TimeSpan.FromMilliseconds(7)))));
        listener.AssertOne("gen_ai.agent.cycle.duration", 7, "ms");
        listener.AssertOne("strands.event_loop.end_cycle", 1, "Count");
        Assert.Equal(TimeSpan.FromMilliseconds(7), meter.Metrics.TotalDuration);
    }

    [Fact]
    public void UsesAnApplicationOwnedMeterForDependencyInjectionWithoutChangingTheDefaultMeter()
    {
        var name = "application." + Guid.NewGuid().ToString("N");
        using var source = new Meter(name);
        using var listener = new Measurements(name);
        var meter = new AgentMeter(meter: source);
        meter.StartNewInvocation();
        listener.AssertOne("gen_ai.agent.invocation.count", 1, null);
        source.CreateCounter<long>("application.alive").Add(7);
        listener.AssertOne("application.alive", 7, null);
        Assert.Single(meter.Metrics.AgentInvocations);
    }

    [Fact]
    public void FailingInstrumentPublicationDoesNotPoisonTheCollectorOrSubsequentInstruments()
    {
        var name = "application." + Guid.NewGuid().ToString("N");
        using var source = new Meter(name);
        using var listener = new MeterListener();
        var published = 0;
        var measured = 0;
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name != name) return;
            if (Interlocked.Increment(ref published) == 1) throw new InvalidOperationException("publication failed");
            owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref measured));
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref measured));
        listener.Start();
        var meter = new AgentMeter(meter: source);
        meter.StartNewInvocation();
        var cycle = meter.StartCycle();
        cycle.Update(new Usage(7, 3));
        cycle.End(TimeSpan.FromMilliseconds(10));
        Assert.Equal(new Usage(7, 3), meter.Metrics.AccumulatedUsage);
        Assert.True(published > 1);
        Assert.True(measured > 0);
    }

    [Fact]
    public void EmitsInvocationAndCycleCountersAndExplicitDurationUnitsForBothLanguages()
    {
        using var listener = new Measurements();
        var meter = new AgentMeter();
        meter.StartNewInvocation();
        var cycle = meter.StartCycle();
        cycle.End(TimeSpan.FromMilliseconds(2500));
        cycle.Dispose();

        listener.AssertOne("gen_ai.agent.invocation.count", 1, null);
        listener.AssertOne("gen_ai.agent.cycle.count", 1, null);
        listener.AssertOne("gen_ai.agent.cycle.duration", 2500, "ms");
        listener.AssertOne("strands.event_loop.cycle_count", 1, "Count");
        listener.AssertOne("strands.event_loop.start_cycle", 1, "Count");
        listener.AssertOne("strands.event_loop.end_cycle", 1, "Count");
        listener.AssertOne("strands.event_loop.cycle_duration", 2.5, "s");
    }

    [Fact]
    public void EmitsUsageAndModelTimingWithoutSummingTimeToFirstToken()
    {
        using var listener = new Measurements();
        var meter = new AgentMeter();
        meter.UpdateCycle(new Usage(10, 2, 5, 3, 20), new(TimeSpan.FromMilliseconds(700), TimeSpan.FromMilliseconds(250)));
        listener.AssertOne("gen_ai.agent.tokens.input", 10, null);
        listener.AssertOne("gen_ai.agent.tokens.output", 2, null);
        listener.AssertOne("strands.event_loop.input.tokens", 10, "token");
        listener.AssertOne("strands.event_loop.output.tokens", 2, "token");
        listener.AssertOne("strands.event_loop.cache_read.input.tokens", 5, "token");
        listener.AssertOne("strands.event_loop.cache_write.input.tokens", 3, "token");
        listener.AssertOne("gen_ai.agent.model.latency", 700, "ms");
        listener.AssertOne("strands.event_loop.latency", 700, "ms");
        listener.AssertOne("gen_ai.server.time_to_first_token", 0.25, "s");
        listener.AssertOne("strands.model.time_to_first_token", 250, "ms");
        Assert.Null(meter.Metrics.AccumulatedMetrics.TimeToFirstToken);
    }

    [Fact]
    public void ToolMeasurementsUseLanguageSpecificNamesAndCorrelationTags()
    {
        using var listener = new Measurements();
        using var input = JsonDocument.Parse("{}");
        var meter = new AgentMeter();
        meter.RecordToolCall(new("call-7", "calculate", input.RootElement), TimeSpan.FromMilliseconds(1500), false);
        var ts = listener.AssertOne("gen_ai.agent.tool.call.count", 1, null);
        Assert.Equal("calculate", ts.Tags["gen_ai.tool.name"]);
        listener.AssertOne("gen_ai.agent.tool.error.count", 1, null);
        listener.AssertOne("gen_ai.agent.tool.duration", 1500, "ms");
        var py = listener.AssertOne("strands.tool.call_count", 1, "Count");
        Assert.Equal("calculate", py.Tags["tool_name"]);
        Assert.Equal("call-7", py.Tags["tool_use_id"]);
        listener.AssertOne("strands.tool.error_count", 1, "Count");
        listener.AssertOne("strands.tool.duration", 1.5, "s");
        Assert.DoesNotContain(listener.Values, value => value.Name == "strands.tool.success_count");
    }

    [Fact]
    public void FailingMeasurementCallbackCannotReplaceLocalCollection()
    {
        var called = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "Strands.Agents") owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => { Interlocked.Increment(ref called); throw new InvalidOperationException("exporter failed"); });
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => { Interlocked.Increment(ref called); throw new InvalidOperationException("exporter failed"); });
        listener.Start();
        var meter = new AgentMeter();
        meter.StartNewInvocation();
        var cycle = meter.StartCycle();
        cycle.Update(new Usage(7, 3));
        cycle.End(TimeSpan.FromMilliseconds(10));
        Assert.Equal(new Usage(7, 3), meter.Metrics.AccumulatedUsage);
        Assert.Equal(TimeSpan.FromMilliseconds(10), meter.Metrics.TotalDuration);
        Assert.True(called > 0, "The failing listener must actually observe instrumentation.");
    }

    private sealed class Measurements : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal ConcurrentQueue<Measurement> Values { get; } = new();
        internal Measurements(string meterName = AgentMeter.MeterName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meterName) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.Start();
        }
        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags) copy.Add(tag.Key, tag.Value);
            Values.Enqueue(new(instrument.Name, instrument.Unit, value, copy));
        }
        internal Measurement AssertOne(string name, double expected, string? unit)
        {
            var value = Assert.Single(Values, item => item.Name == name);
            Assert.Equal(expected, value.Value);
            Assert.Equal(unit, value.Unit);
            return value;
        }
        public void Dispose() => _listener.Dispose();
    }
    private sealed record Measurement(string Name, string? Unit, double Value, IReadOnlyDictionary<string, object?> Tags);
}
