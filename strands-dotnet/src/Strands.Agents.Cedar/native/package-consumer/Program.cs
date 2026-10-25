using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Interventions;
using Strands.Agents.Interventions.Cedar;

if (args.Length != 1 || !RuntimeInformation.ProcessArchitecture.ToString().Equals(args[0], StringComparison.Ordinal))
    throw new InvalidOperationException("The consumer did not run in its requested process architecture.");
await using var agent = new Agent(new AgentOptions { Model = new NoModel() });
var tool = new ToolSpec("search", "Search", JsonSerializer.SerializeToElement(new { type = "object", properties = new { query = new { type = "string" } } }));
var handler = new CedarAuthorization(new()
{
    Policies = "permit(principal, action == Action::\"search\", resource) when { context.input.query == \"雪\" };",
    Tools = [tool], PoliciesAreInline = true
});
BeforeToolCallEvent Event(string query) => new(agent, new("call", "search", JsonSerializer.SerializeToElement(new { query })), new Dictionary<string, object?>());
if (await handler.BeforeToolCallAsync(Event("雪")) is not Proceed || await handler.BeforeToolCallAsync(Event("other")) is not Deny)
    throw new InvalidOperationException("Real native Cedar authorization failed.");
var invalidRejected = false;
try { _ = new CedarAuthorization(new() { Policies = "permit(principal, action == Action::\"typo\", resource);", Tools = [tool] }); }
catch (ArgumentException) { invalidRejected = true; }
if (!invalidRejected) throw new InvalidOperationException("Real native schema generation or validation did not reject an unknown action.");
var schemaAware = new CedarAuthorization(new()
{
    Policies = "permit(principal, action == Action::\"search\", resource);", Tools = [tool],
    UseSchemaForAuthorization = true, EngineExceptionPolicy = CedarEngineExceptionPolicy.Deny
});
if (await schemaAware.BeforeToolCallAsync(Event("hello")) is not Proceed ||
    await schemaAware.BeforeToolCallAsync(new(agent, new("typed", "search", JsonSerializer.SerializeToElement(new { query = 3 })), new Dictionary<string, object?>())) is not Deny)
    throw new InvalidOperationException("Real schema-aware native authorization did not enforce the declared input type.");
Console.WriteLine("Schema-aware native authorization: validated.");
Console.WriteLine($"Cedar:allow,deny,schema-rejected;Architecture:{RuntimeInformation.ProcessArchitecture};PointerSize:{IntPtr.Size}");

internal sealed class NoModel : IModel
{
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("Authorization cannot call a model.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
