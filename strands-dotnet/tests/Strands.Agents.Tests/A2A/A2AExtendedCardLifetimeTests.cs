using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AExtendedCardLifetimeTests
{
    [Fact]
    public async Task AServerWithoutAnExtendedCardReportsTheSpecificProtocolError()
    {
        await using var executor = Executor(); await using var server = new A2AServer(executor, A2AServerTests.Options());
        using var http = new HttpClient(new A2AServerTests.Handler(server));
        var error = await Assert.ThrowsAsync<A2ARpcException>(() => new A2AClient(http, new(new Uri("http://local/"))).GetAuthenticatedExtendedCardAsync());
        Assert.Equal(-32007, error.Code); Assert.False(server.AgentCard.GetProperty("supportsAuthenticatedExtendedCard").GetBoolean());
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrivateMetadataAndExplicitAuthorizationMustBeConfiguredTogether(bool cardOnly)
    {
        await using var executor = Executor(); var options = Options((_, _) => new(true));
        options = cardOnly ? options with { AuthorizeExtendedCard = null } : options with { AuthenticatedExtendedCard = null };
        Assert.Throws<ArgumentException>(() => new A2AServer(executor, options));
    }
    [Fact]
    public async Task ShutdownCancelsAndDrainsAnAcceptedAuthorizationRead()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var closed = false;
        await using var executor = Executor();
        await using var server = new A2AServer(executor, Options(async (_, token) =>
        { entered.TrySetResult(true); try { await Task.Delay(Timeout.Infinite, token); return true; } finally { closed = true; } }));
        using var request = Request(); var read = server.HandleAsync(request);
        await Within(entered.Task);
        await Within(server.DisposeAsync().AsTask());
        using var response = await read;
        Assert.True(closed); Assert.Contains("-32603", await response.Content.ReadAsStringAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationWinsDuringAuthorizationAndAfterItsAwait(bool late)
    {
        using var cancellation = new CancellationTokenSource(); var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var closed = false;
        await using var executor = Executor();
        await using var server = new A2AServer(executor, Options(async (_, token) =>
        {
            entered.TrySetResult(true);
            try { if (late) cancellation.Cancel(); else await Task.Delay(Timeout.Infinite, token); return true; }
            finally { closed = true; }
        }));
        using var request = Request(); var read = server.HandleAsync(request, cancellation.Token);
        await Within(entered.Task); if (!late) cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.True(closed);
    }
    [Fact]
    public async Task AuthorizationCannotAwaitItsOwnServerDisposalOrRecursivelyReadItsCard()
    {
        A2AServer? current = null; var calls = 0;
        await using var executor = Executor();
        await using var server = new A2AServer(executor, Options(async (_, _) =>
        {
            calls++;
            Assert.Throws<InvalidOperationException>(() => current!.DisposeAsync());
            using var recursive = Request(); using var denied = await current!.HandleAsync(recursive);
            Assert.Contains("-32603", await denied.Content.ReadAsStringAsync());
            return true;
        }));
        current = server;
        using var request = Request(); using var response = await server.HandleAsync(request);
        Assert.Equal(1, calls); Assert.Contains("Private", await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task AuthorizationBackendFailuresAreSanitizedWithoutPublishingPrivateCardData()
    {
        await using var executor = Executor();
        await using var server = new A2AServer(executor, Options((_, _) => throw new IOException("secret backend detail")));
        using var request = Request(); using var response = await server.HandleAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("-32603", raw); Assert.Contains("\"id\":\"card\"", raw); Assert.DoesNotContain("secret", raw); Assert.DoesNotContain("Private", raw);
    }
    internal static A2AServerOptions Options(Func<HttpRequestMessage, CancellationToken, ValueTask<bool>> authorize) => A2AServerTests.Options() with
    {
        AuthorizeExtendedCard = authorize,
        AuthenticatedExtendedCard = JsonSerializer.SerializeToElement(new
        {
            protocolVersion = "0.3.0",
            name = "Private",
            description = "Private metadata",
            version = "0.0.1",
            url = "http://local/rpc",
            capabilities = new { streaming = true },
            defaultInputModes = new[] { "text/plain" },
            defaultOutputModes = new[] { "text/plain" },
            skills = Array.Empty<object>()
        })
    };
    internal static A2AExecutor Executor() => new(new() { AgentFactory = (_, _) => throw new InvalidOperationException("Metadata only.") });
    internal static HttpRequestMessage Request() => new(HttpMethod.Post, "http://local/rpc")
    { Content = new StringContent("""{"jsonrpc":"2.0","id":"card","method":"agent/getAuthenticatedExtendedCard"}""", Encoding.UTF8, "application/json") };
    private static async Task Within(Task task)
    { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task; }
}
