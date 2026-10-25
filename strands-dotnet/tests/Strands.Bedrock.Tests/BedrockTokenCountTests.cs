using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Diagnostics = System.Diagnostics;

namespace Strands.Bedrock.Tests;

[CollectionDefinition("Bedrock counting diagnostics", DisableParallelization = true)]
public sealed class BedrockCountDiagnosticCollection { }

[Collection("Bedrock counting diagnostics")]
public sealed class BedrockTokenCountTests
{
    [Fact]
    public async Task NativeCountingUsesTheSameConverseInputIncludingSystemAndTools()
    {
        using var client = new CountClient();
        using var model = new BedrockModel(client, new() { UseNativeTokenCount = true });
        var request = new ModelRequest([Strands.Agents.Message.User("12345678")], "policy", [new("lookup", "Lookup", JsonSerializer.SerializeToElement(new { type = "object" }))]);

        Assert.Equal(42, await model.CountTokensAsync(request));
        var native = Assert.Single(client.Requests);
        Assert.Equal(model.ModelId, native.ModelId);
        Assert.Equal("12345678", Assert.Single(Assert.Single(native.Input.Converse.Messages).Content).Text);
        Assert.Equal("policy", Assert.Single(native.Input.Converse.System).Text);
        Assert.Equal("lookup", Assert.Single(native.Input.Converse.ToolConfig.Tools).ToolSpec.Name);
    }

    [Fact]
    public async Task PermissionFailureFallsBackAndIsCachedPerInstanceAndModel()
    {
        using var client = new CountClient { Failure = new AccessDeniedException("denied") };
        using var model = new BedrockModel(client, new() { UseNativeTokenCount = true });
        var request = new ModelRequest([Strands.Agents.Message.User("12345678")]);

        Assert.Equal(2, await model.CountTokensAsync(request));
        Assert.Equal(2, await model.CountTokensAsync(request));
        Assert.Single(client.Requests);
        model.UpdateConfig(options => options with { ModelId = "different-model" });
        Assert.Equal(2, await model.CountTokensAsync(request));
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task StrictFailureRetainsTheNativeCause()
    {
        var failure = new AccessDeniedException("denied");
        using var client = new CountClient { Failure = failure };
        using var model = new BedrockModel(client, new() { UseNativeTokenCount = true, TokenCountFailurePolicy = NativeTokenCountFailurePolicy.Throw });

        var error = await Assert.ThrowsAsync<ModelException>(async () => await model.CountTokensAsync(new ModelRequest([Strands.Agents.Message.User("text")])));

        Assert.Same(failure, error.InnerException);
    }

    [Fact]
    public async Task DisposedModelsCannotReturnAnApparentlySuccessfulLocalCount()
    {
        using var client = new CountClient();
        var model = new BedrockModel(client, new());
        model.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await model.CountTokensAsync(new ModelRequest([Strands.Agents.Message.User("text")])));
    }

    [Fact]
    public async Task TheDefaultEstimatesWithoutInitializingTheAwsClient()
    {
        using var client = new CountClient();
        using var model = new BedrockModel(client, new());
        Assert.Equal(2, await model.CountTokensAsync(new ModelRequest([Strands.Agents.Message.User("12345678")])));
        Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyPermanentUnsupportedFailuresAreCached(bool unsupported)
    {
        using var client = new CountClient { Failure = new ValidationException(unsupported ? "This model doesn't support counting tokens" : "temporary counting failure") };
        using var model = new BedrockModel(client, new() { UseNativeTokenCount = true });
        var request = new ModelRequest([Strands.Agents.Message.User("12345678")]);
        Assert.Equal(2, await model.CountTokensAsync(request));
        Assert.Equal(2, await model.CountTokensAsync(request));
        Assert.Equal(unsupported ? 1 : 2, client.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FallbackAlsoCoversSourcesTheNativeCountingApiCannotRepresent(bool reference)
    {
        using var client = new CountClient();
        using var model = new BedrockModel(client, new() { UseNativeTokenCount = true });
        Strands.Agents.ContentBlock unsupported = reference ? new ResourceLinkBlock("https://example.com/file", "file") : new Strands.Agents.ImageBlock("image/png", new Uri("https://example.com/image.png"));
        var request = new ModelRequest([new(Role.User, [new TextBlock("12345678"), unsupported])]);

        Assert.Equal(2, await model.CountTokensAsync(request));
        Assert.Empty(client.Requests);
        model.UpdateConfig(options => options with { TokenCountFailurePolicy = NativeTokenCountFailurePolicy.Throw });
        await Assert.ThrowsAsync<ModelException>(async () => await model.CountTokensAsync(request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public async Task InvalidNativeCountsCannotBecomeSuccessfulStrictCounts(int? count)
    {
        using var client = new CountClient { Handler = (_, _) => Task.FromResult(new CountTokensResponse { InputTokens = count }) };
        using var model = new BedrockModel(client, new() { UseNativeTokenCount = true, TokenCountFailurePolicy = NativeTokenCountFailurePolicy.Throw });
        await Assert.ThrowsAsync<ModelException>(async () => await model.CountTokensAsync(new ModelRequest([Strands.Agents.Message.User("text")])));
    }

    [Fact]
    public async Task CallerCancellationWinsEvenWhenTheNativeClientReturnsAValidCount()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new CountClient { Handler = (_, _) => { cancellation.Cancel(); return Task.FromResult(new CountTokensResponse { InputTokens = 42 }); } };
        using var model = new BedrockModel(client, new() { UseNativeTokenCount = true });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await model.CountTokensAsync(new ModelRequest([Strands.Agents.Message.User("text")]), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Single(client.Requests);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task CancellationDuringRequestFormattingPreventsNativeIo()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new CountClient();
        using var model = new BedrockModel(client, new() { UseNativeTokenCount = true });
        var messages = new CancellingMessages(cancellation);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await model.CountTokensAsync(new ModelRequest(messages), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task ModelDisposalCannotFallBackOrReturnAValidNativeCount()
    {
        BedrockModel? model = null;
        using var client = new CountClient { Handler = (_, _) => { model!.Dispose(); return Task.FromResult(new CountTokensResponse { InputTokens = 42 }); } };
        model = new(client, new() { UseNativeTokenCount = true });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await model.CountTokensAsync(new ModelRequest([Strands.Agents.Message.User("text")])));
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task TheRequestDeadlineCannotBeHiddenByFallback()
    {
        using var client = new CountClient { Handler = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new CountTokensResponse { InputTokens = 42 }; } };
        using var model = new BedrockModel(client, new() { UseNativeTokenCount = true, RequestTimeout = TimeSpan.FromMilliseconds(25) });
        var error = await Assert.ThrowsAsync<ModelException>(async () => await model.CountTokensAsync(new ModelRequest([Strands.Agents.Message.User("text")])));
        Assert.IsType<TimeoutException>(error.InnerException);
    }

    [Fact]
    public async Task StrictConfigurationBypassesPreviousFallbackCache()
    {
        using var client = new CountClient { Failure = new AccessDeniedException("denied") };
        using var model = new BedrockModel(client, new() { UseNativeTokenCount = true });
        var request = new ModelRequest([Strands.Agents.Message.User("12345678")]);
        Assert.Equal(2, await model.CountTokensAsync(request));
        model.UpdateConfig(options => options with { TokenCountFailurePolicy = NativeTokenCountFailurePolicy.Throw });
        await Assert.ThrowsAsync<ModelException>(async () => await model.CountTokensAsync(request));
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task CountRequestOwnsAndDisposesItsMediaStreams()
    {
        using var client = new CountClient();
        using var model = new BedrockModel(client, new() { UseNativeTokenCount = true });
        Assert.Equal(42, await model.CountTokensAsync(new ModelRequest([new(Role.User, [new Strands.Agents.ImageBlock("image/png", "AQID")])])));
        Assert.False(Assert.Single(Assert.Single(Assert.Single(client.Requests).Input.Converse.Messages).Content).Image.Source.Bytes.CanRead);
        Assert.False(client.Disposed);
    }

    [Fact]
    public void InvalidPolicyPublicationLeavesThePreviousConfigurationIntact()
    {
        using var client = new CountClient();
        using var model = new BedrockModel(client, new());
        Assert.Throws<ArgumentOutOfRangeException>(() => model.UpdateConfig(options => options with { TokenCountFailurePolicy = (NativeTokenCountFailurePolicy)999 }));
        Assert.Equal(NativeTokenCountFailurePolicy.Fallback, model.GetConfig().TokenCountFailurePolicy);
    }

    [Fact]
    public async Task FailedDiagnosticListenersCannotReplaceThePromisedFallback()
    {
        var failure = new AccessDeniedException("denied");
        using var client = new CountClient { Failure = failure };
        using var model = new BedrockModel(client, new() { UseNativeTokenCount = true });
        var previous = Diagnostics.Trace.Listeners.Cast<Diagnostics.TraceListener>().ToArray();
        using var listener = new ThrowingListener();
        try
        {
            Diagnostics.Trace.Listeners.Clear();
            Diagnostics.Trace.Listeners.Add(listener);
            Assert.Equal(2, await model.CountTokensAsync(new ModelRequest([Strands.Agents.Message.User("12345678")])));
            Assert.Same(listener.Error, Assert.Single(Assert.IsType<List<Exception>>(failure.Data["Strands.DiagnosticErrors"])));
        }
        finally
        {
            Diagnostics.Trace.Listeners.Clear();
            foreach (var original in previous) Diagnostics.Trace.Listeners.Add(original);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAcceptedByADiagnosticCallbackCannotBecomeFallbackSuccess(bool dispose)
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new CountClient { Failure = new AccessDeniedException("denied") };
        using var model = new BedrockModel(client, new() { UseNativeTokenCount = true });
        var previous = Diagnostics.Trace.Listeners.Cast<Diagnostics.TraceListener>().ToArray();
        using var listener = new ActionListener(() => { if (dispose) model.Dispose(); else cancellation.Cancel(); });
        try
        {
            Diagnostics.Trace.Listeners.Clear();
            Diagnostics.Trace.Listeners.Add(listener);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await model.CountTokensAsync(new ModelRequest([Strands.Agents.Message.User("12345678")]), cancellation.Token));
            if (!dispose) Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            Diagnostics.Trace.Listeners.Clear();
            foreach (var original in previous) Diagnostics.Trace.Listeners.Add(original);
        }
    }

    private sealed class ActionListener(Action action) : Diagnostics.TraceListener
    {
        public override void Write(string? message) => action();
        public override void WriteLine(string? message) => action();
    }

    private sealed class ThrowingListener : Diagnostics.TraceListener
    {
        internal IOException Error { get; } = new("diagnostic sink unavailable");
        public override void Write(string? message) => throw Error;
        public override void WriteLine(string? message) => throw Error;
    }

    private sealed class CancellingMessages(CancellationTokenSource cancellation) : IReadOnlyList<Strands.Agents.Message>
    {
        public int Count => 1;
        public Strands.Agents.Message this[int index] => index == 0 ? Strands.Agents.Message.User("text") : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<Strands.Agents.Message> GetEnumerator() { cancellation.Cancel(); yield return this[0]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountClient : AmazonBedrockRuntimeClient
    {
        internal CountClient() : base(new AnonymousAWSCredentials(), RegionEndpoint.USEast1) { }
        internal List<CountTokensRequest> Requests { get; } = [];
        internal Exception? Failure { get; init; }
        internal Func<CountTokensRequest, CancellationToken, Task<CountTokensResponse>>? Handler { get; init; }
        internal bool Disposed { get; private set; }
        public override Task<CountTokensResponse> CountTokensAsync(CountTokensRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Handler is { } handler) return handler(request, cancellationToken);
            return Failure is { } failure ? Task.FromException<CountTokensResponse>(failure) : Task.FromResult(new CountTokensResponse { InputTokens = 42 });
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
