using System.Text.Json;
using Strands.Agents.VendedTools;

namespace Strands.Agents.Tests.VendedTools;

public sealed class SleepMinimumTests
{
    [Fact]
    public void FactoryIsInertAndPreservesExplicitMetadata()
    {
        var tool = Sleep.Create(new() { Name = "short_pause", Description = "Selected pause." });
        Assert.Equal("short_pause", tool.Spec.Name);
        Assert.Equal("Selected pause.", tool.Spec.Description);
        Assert.False(tool is IDisposable);
        Assert.False(tool is IAsyncDisposable);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(2.5)]
    public void SchemaAdvertisesRequiredFiniteNumericSecondsAndConfiguredCap(double cap)
    {
        var tool = cap == 60 ? Sleep.Create() : Sleep.Create(new() { MaximumDuration = TimeSpan.FromSeconds(cap) });
        var schema = tool.Spec.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var duration = schema.GetProperty("properties").GetProperty("duration");
        Assert.Equal("number", duration.GetProperty("type").GetString());
        Assert.Equal(0, duration.GetProperty("minimum").GetDouble());
        Assert.Equal(cap, duration.GetProperty("maximum").GetDouble());
        Assert.Equal("duration", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task ZeroDurationCompletesWithInvariantText()
    {
        Assert.Equal("Slept for 0 seconds", await Sleep.Create().InvokeAsync(Input(0), Context()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveMaximumIsRejectedAtFactory(double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sleep.Create(new() { MaximumDuration = TimeSpan.FromSeconds(seconds) }));
    }

    [Fact]
    public async Task DirectInputAboveCustomCapIsRejectedBeforeWaiting()
    {
        var tool = Sleep.Create(new() { MaximumDuration = TimeSpan.FromSeconds(2.5) });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await tool.InvokeAsync(Input(2.5001), Context()));
    }

    [Fact]
    public async Task LongCustomCapAdmitsNonzeroWaitAndUsesExactOperationCancellationToken()
    {
        using var operation = new CancellationTokenSource();
        var tool = Sleep.Create(new() { MaximumDuration = TimeSpan.FromDays(60) });
        var pending = tool.InvokeAsync(Input(TimeSpan.FromDays(30).TotalSeconds), Context(), operation.Token).AsTask();
        var wasPendingAtReturn = !pending.IsCompleted;
        operation.Cancel();
        var observed = await Record.ExceptionAsync(async () => await Join(pending));
        Assert.True(wasPendingAtReturn, "A valid nonzero delay must return pending before the caller releases cancellation; observed " + observed);
        Assert.Equal(operation.Token, Assert.IsAssignableFrom<OperationCanceledException>(observed).CancellationToken);
    }

    [Fact]
    public async Task CapturedContextSignalCancelsTheAdmittedWaitWithItsOwnToken()
    {
        using var signal = new CancellationTokenSource();
        var context = Context() with { ExecutionCancelSignal = signal.Token };
        var pending = Sleep.Create().InvokeAsync(Input(30), context).AsTask();
        var wasPendingAtReturn = !pending.IsCompleted;
        signal.Cancel();
        var observed = await Record.ExceptionAsync(async () => await Join(pending));
        Assert.True(wasPendingAtReturn, "A valid delay must be admitted before captured-signal cancellation; observed " + observed);
        Assert.Equal(signal.Token, Assert.IsAssignableFrom<OperationCanceledException>(observed).CancellationToken);
    }

    private static JsonElement Input(double seconds) => JsonSerializer.SerializeToElement(new { duration = seconds });
    private static ToolContext Context() => new(null!, new("sleep-call", "sleep", Input(0)), new Dictionary<string, object?>());

    private static async Task<string> Join(Task<string> operation)
    {
        using var watchdog = new CancellationTokenSource();
        try
        {
            var timeout = Task.Delay(TimeSpan.FromSeconds(10), watchdog.Token);
            Assert.Same(operation, await Task.WhenAny(operation, timeout));
            return await operation;
        }
        finally { watchdog.Cancel(); }
    }
}
