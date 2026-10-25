using System.Diagnostics;
using System.Threading.Tasks.Sources;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry;

[Collection("Telemetry listeners")]
public sealed class ValueTaskTraceContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextPreservesTheOriginalValueTaskCancellation(bool sampled)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Strands.Agents",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampled
                ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
        };
        ActivitySource.AddActivityListener(listener);
        using var ambient = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var tracer = new Tracer();
        using var span = tracer.StartAgentSpan("cancellation");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = new OperationCanceledException("original cancellation", cancellation.Token);
        var source = new CancelledSource(failure);
        Exception? observed = null;
        try
        {
            await span.WithValueTaskContextAsync(() =>
            {
                Assert.Equal(sampled ? span.Context : ambient.Context, Activity.Current?.Context);
                return source.Run();
            });
        }
        catch (Exception error) { observed = error; }

        Assert.Same(failure, observed);
        Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(observed).CancellationToken);
        Assert.Same(ambient, Activity.Current);
        Assert.Equal(1, source.ResultReads);
    }

    private sealed class CancelledSource(OperationCanceledException failure) : IValueTaskSource<bool>
    {
        private ManualResetValueTaskSourceCore<bool> _completion = new() { RunContinuationsAsynchronously = true };
        public int ResultReads { get; private set; }

        public ValueTask<bool> Run()
        {
            ThreadPool.QueueUserWorkItem(_ => _completion.SetException(failure));
            return new(this, _completion.Version);
        }

        public bool GetResult(short token)
        {
            ResultReads++;
            return _completion.GetResult(token);
        }

        public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _completion.OnCompleted(continuation, state, token, flags);
    }
}
