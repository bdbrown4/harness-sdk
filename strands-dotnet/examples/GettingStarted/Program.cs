using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Models;
using Strands.Harness;

using var httpClient = new HttpClient();
IModel model = args.FirstOrDefault() switch
{
    null or "--demo" => new DemoModel(),
    "--openai" => new OpenAIModel(httpClient, new(
        RequiredEnvironment("STRANDS_MODEL_ID"), RequiredEnvironment("OPENAI_API_KEY"))),
    "--anthropic" => new AnthropicModel(httpClient, new(
        RequiredEnvironment("STRANDS_MODEL_ID"), RequiredEnvironment("ANTHROPIC_API_KEY"))),
    _ => throw new ArgumentException("Use --demo, --openai, or --anthropic.")
};

var schema = JsonSerializer.SerializeToElement(new
{
    type = "object",
    properties = new { x = new { type = "number" }, y = new { type = "number" } },
    required = new[] { "x", "y" },
    additionalProperties = false
});
var add = new FunctionTool(new ToolSpec("add", "Add two numbers.", schema),
    (input, _, _) => new ValueTask<string>((input.GetProperty("x").GetDouble() + input.GetProperty("y").GetDouble()).ToString(CultureInfo.InvariantCulture)));
var agent = await Harness.CreateAsync(new HarnessOptions
{
    Model = model,
    Instructions = "Use the add tool for addition, then explain the answer briefly.",
    BuiltinTools = [],
    Tools = [add],
    Session = new SessionOptions { Enabled = false }
}).ConfigureAwait(false);

var result = await agent.InvokeAsync("What is 2 + 3? Use add.").ConfigureAwait(false);
Console.WriteLine(result);
Console.WriteLine($"Stop: {result.StopReason}; model turns: {result.Turns}");

static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Set {name} before using a live provider.");

internal sealed class DemoModel : IModel
{
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ModelMessageStartEvent();
        if (request.Messages[request.Messages.Count - 1].Content.OfType<ToolResultBlock>().FirstOrDefault() is { } toolResult)
        {
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: $"2 + 3 = {toolResult.Content}.");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
        else
        {
            yield return new ModelContentBlockStartEvent(0, new("demo-add", "add", JsonSerializer.SerializeToElement(new { x = 2, y = 3 })));
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.ToolUse);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
