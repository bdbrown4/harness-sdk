using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class SwarmPythonKnowledgeReprTests
{
    // Expected constants come from the frozen CPython3.10.2 json.loads + dict repr reference.
    // Only ASCII IDs enter theory display names; transport/expected text stays in this map.
    private static readonly IReadOnlyDictionary<string, Case> Cases = new Dictionary<string, Case>(StringComparer.Ordinal)
    {
        ["N-INT-NEG-ZERO"] = new("-0", "\u2022 memory: {'value': 0}\n"),
        ["N-BIGINT"] = new("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "\u2022 memory: {'value': 12345678901234567890123456789012345678901234567890123456789012345678901234567890}\n"),
        ["N-FLOAT-ONE"] = new("1.0", "\u2022 memory: {'value': 1.0}\n"),
        ["N-EXP-THOUSAND"] = new("1e3", "\u2022 memory: {'value': 1000.0}\n"),
        ["N-FLOAT-NEG-ZERO"] = new("-0.0", "\u2022 memory: {'value': -0.0}\n"),
        ["N-SMALL-EXP"] = new("1e-5", "\u2022 memory: {'value': 1e-05}\n"),
        ["N-LARGE-EXP"] = new("1e16", "\u2022 memory: {'value': 1e+16}\n"),
        ["N-TRAILING-ZEROS"] = new("1.2500", "\u2022 memory: {'value': 1.25}\n"),
        ["U-NBSP"] = new("\"\\u00a0\"", "\u2022 memory: {'value': '\\xa0'}\n"),
        ["U-ZERO-WIDTH"] = new("\"\\u200b\"", "\u2022 memory: {'value': '\\u200b'}\n"),
        ["U-ASTRAL"] = new("\"\\ud83d\\ude42\"", "\u2022 memory: {'value': '\ud83d\ude42'}\n"),
        ["U-NEWLINE"] = new("\"\\n\"", "\u2022 memory: {'value': '\\n'}\n"),
    };

    [Theory]
    [InlineData("N-INT-NEG-ZERO")]
    [InlineData("N-BIGINT")]
    [InlineData("N-FLOAT-ONE")]
    [InlineData("N-EXP-THOUSAND")]
    [InlineData("N-FLOAT-NEG-ZERO")]
    [InlineData("N-SMALL-EXP")]
    [InlineData("N-LARGE-EXP")]
    [InlineData("N-TRAILING-ZEROS")]
    public async Task PythonProfilePreservesJsonIntegerVersusFloatRepr(string id)
    {
        var selected = Cases[id];
        var prompt = await PromptAsync(selected.RawJson, SwarmContextTextProfile.PythonRepr);
        Assert.Contains(selected.ExpectedLine, prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("U-NBSP")]
    [InlineData("U-ZERO-WIDTH")]
    [InlineData("U-ASTRAL")]
    [InlineData("U-NEWLINE")]
    public async Task PythonProfileEscapesNonprintableCharactersButKeepsPrintableAstralAndNewlineRules(string id)
    {
        var selected = Cases[id];
        var prompt = await PromptAsync(selected.RawJson, SwarmContextTextProfile.PythonRepr);
        Assert.Contains(selected.ExpectedLine, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeJsonProfileRetainsItsRawNumberTransportRepresentation()
    {
        const string raw = """{"n":1e3,"z":-0.0,"i":1234567890123456789012345678901234567890}""";

        var prompt = await PromptAsync(raw, SwarmContextTextProfile.NativeJson);

        Assert.Contains("• memory: {\"value\":" + raw + "}\n", prompt, StringComparison.Ordinal);
    }

    private static async Task<string> PromptAsync(string raw, SwarmContextTextProfile profile)
    {
        var model = new CaptureModel();
        await using var agent = new Agent(new() { Id = "reader", Model = model });
        var swarm = new Swarm(SwarmOptions.Python with { Nodes = [new(agent)], ContextTextProfile = profile });
        using (var document = JsonDocument.Parse(raw))
            swarm.SharedContext.AddContext("memory", "value", document.RootElement);

        await swarm.InvokeAsync("task");

        var stored = swarm.SharedContext.GetAll()["memory"]["value"];
        using var expected = JsonDocument.Parse(raw);
        if (expected.RootElement.ValueKind == JsonValueKind.String)
            Assert.Equal(expected.RootElement.GetString(), stored.GetString());
        else
            Assert.Equal(raw, stored.GetRawText());
        var input = Assert.Single(Assert.Single(model.Requests).Messages, message => message.Role == Role.User);
        return input.Text;
    }

    private sealed record Case(string RawJson, string ExpectedLine);

    private sealed class CaptureModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Requests.Count != 1) throw new InvalidOperationException("A completed single-node visit must not be replayed.");
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
