using Amazon.BedrockAgent;
using Amazon.BedrockAgent.Model;
using Amazon.BedrockAgentRuntime;
using Amazon.BedrockAgentRuntime.Model;
using Amazon.Runtime;
using Strands.Agents.BedrockKnowledgeBase;

namespace Strands.BedrockKnowledgeBase.Tests;

internal sealed class RuntimeClient : AmazonBedrockAgentRuntimeClient
{
    internal Func<RetrieveRequest, CancellationToken, Task<RetrieveResponse>> OnRetrieve { get; set; } = (_, _) => Task.FromResult(new RetrieveResponse());
    internal Action? OnDispose { get; set; }
    internal int Calls;
    internal int Disposals;
    internal RuntimeClient() : base(new BasicAWSCredentials("fixture", "secret"), new AmazonBedrockAgentRuntimeConfig { ServiceURL = "http://127.0.0.1:1", AuthenticationRegion = "us-east-1", MaxErrorRetry = 0 }) { }
    public override Task<RetrieveResponse> RetrieveAsync(RetrieveRequest request, CancellationToken token = default) { Interlocked.Increment(ref Calls); return OnRetrieve(request, token); }
    protected override void Dispose(bool disposing) { if (disposing) { Interlocked.Increment(ref Disposals); OnDispose?.Invoke(); } base.Dispose(disposing); }
}
internal sealed class AgentClient : AmazonBedrockAgentClient
{
    internal Func<IngestKnowledgeBaseDocumentsRequest, CancellationToken, Task<IngestKnowledgeBaseDocumentsResponse>> OnIngest { get; set; } = (_, _) => Task.FromResult(new IngestKnowledgeBaseDocumentsResponse());
    internal int IngestCalls;
    public override Task<IngestKnowledgeBaseDocumentsResponse> IngestKnowledgeBaseDocumentsAsync(IngestKnowledgeBaseDocumentsRequest request, CancellationToken token = default) { Interlocked.Increment(ref IngestCalls); return OnIngest(request, token); }
    internal Func<GetKnowledgeBaseRequest, CancellationToken, Task<GetKnowledgeBaseResponse>> OnGet { get; set; } = (_, _) => Task.FromResult(Type("MANAGED"));
    internal Action? OnDispose { get; set; }
    internal int Calls;
    internal int Disposals;
    internal AgentClient() : base(new BasicAWSCredentials("fixture", "secret"), new AmazonBedrockAgentConfig { ServiceURL = "http://127.0.0.1:1", AuthenticationRegion = "us-east-1", MaxErrorRetry = 0 }) { }
    public override Task<GetKnowledgeBaseResponse> GetKnowledgeBaseAsync(GetKnowledgeBaseRequest request, CancellationToken token = default) { Interlocked.Increment(ref Calls); return OnGet(request, token); }
    internal static GetKnowledgeBaseResponse Type(string name) => new() { KnowledgeBase = new() { KnowledgeBaseConfiguration = new() { Type = new(name) } } };
    protected override void Dispose(bool disposing) { if (disposing) { Interlocked.Increment(ref Disposals); OnDispose?.Invoke(); } base.Dispose(disposing); }
}
internal static class TestSupport
{
    internal static BedrockKnowledgeBaseStoreOptions Options(RuntimeClient runtime) => new() { Name = "knowledge", KnowledgeBaseId = "KB12345678", RuntimeClient = runtime, KnownKnowledgeBaseType = KnowledgeBaseType.MANAGED };
    internal static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task Ready(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))));
        await task;
    }
}
