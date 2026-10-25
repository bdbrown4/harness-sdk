using System.Diagnostics;
using System.Text.Json;
using Strands.Agents.Memory;

namespace Strands.Agents.Tests.Memory;

[CollectionDefinition("Memory diagnostic sinks", DisableParallelization = true)]
public sealed class MemoryDiagnosticCollection { }

[Collection("Memory diagnostic sinks")]
public sealed class MemoryDiagnosticsTests
{
    [Fact]
    public async Task AFailedDiagnosticSinkCannotLeaveAcceptedBackgroundWritesInAFaultedDrain()
    {
        var manager = new MemoryManager(new() { Stores = [new FailingStore()], AddTool = new() { WaitForWrites = false }, Injection = null });
        var agent = new Agent(new() { Model = new UnusedModel(), MemoryManager = manager });
        await agent.InitializeAsync();
        var original = Trace.Listeners.Cast<TraceListener>().ToArray();
        using var listener = new ThrowingListener();
        var verified = false;
        try
        {
            Trace.Listeners.Clear();
            Trace.Listeners.Add(listener);
            var input = JsonSerializer.SerializeToElement(new { entries = new[] { "fact" } });
            var tool = Assert.Single(agent.Tools, tool => tool.Spec.Name == "add_memory");
            await tool.InvokeAsync(input, new(agent, new("write", "add_memory", input), new Dictionary<string, object?>()));

            await manager.FlushAsync();

            Assert.True(listener.Attempts > 0);
            verified = true;
        }
        finally
        {
            Trace.Listeners.Clear();
            foreach (var previous in original) Trace.Listeners.Add(previous);
            try { await agent.DisposeAsync(); }
            catch (IOException) when (!verified) { /* Preserve the first failing assertion while releasing the deliberately broken pre-fix drain. */ }
        }
    }

    private sealed class ThrowingListener : TraceListener
    {
        internal int Attempts;
        public override void Write(string? message) { Attempts++; throw new IOException("diagnostic sink unavailable"); }
        public override void WriteLine(string? message) { Attempts++; throw new IOException("diagnostic sink unavailable"); }
    }

    private sealed class FailingStore : MemoryStore, IWritableMemoryStore
    {
        public override string Name => "failed";
        public override bool Writable => true;
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => new((IReadOnlyList<MemoryEntry>)[]);
        public ValueTask AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default) => throw new IOException("backend unavailable");
    }

    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This fixture must not call a model.");
    }
}
