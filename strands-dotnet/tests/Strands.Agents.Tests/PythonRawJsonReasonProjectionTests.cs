using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class PythonRawJsonReasonProjectionTests
{
    // Fixed CPython3.10.2/UCD13 json.loads+repr reference; no .NET decoded-string precondition.
    private static readonly IReadOnlyDictionary<string, Case> Cases = new Dictionary<string, Case>(StringComparer.Ordinal)
    {
        ["J-HIGH-VALUE"] = new("\"\\ud800\"", "'\\ud800'"),
        ["J-LOW-VALUE"] = new("\"\\udc00\"", "'\\udc00'"),
        ["J-HIGH-KEY"] = new("{\"\\ud800\":\"high\"}", "{'\\ud800': 'high'}"),
        ["J-LOW-KEY"] = new("{\"\\udc00\":\"low\"}", "{'\\udc00': 'low'}"),
        ["J-PAIR-VALUE"] = new("\"\\ud83d\\ude00\"", "'\ud83d\ude00'"),
        ["J-PAIR-KEY"] = new("{\"\\ud83d\\ude00\":\"pair\"}", "{'\ud83d\ude00': 'pair'}"),
        ["J-DUPLICATE-LAST-FIRST-POSITION"] = new("{\"a\":1,\"b\":2,\"a\":3}", "{'a': 3, 'b': 2}"),
        ["J-ESCAPED-DUPLICATE-ALIAS"] = new("{\"a\":1,\"\\u0062\":2,\"\\u0061\":4}", "{'a': 4, 'b': 2}"),
    };

    [Theory]
    [InlineData("J-HIGH-VALUE")]
    [InlineData("J-LOW-VALUE")]
    [InlineData("J-HIGH-KEY")]
    [InlineData("J-LOW-KEY")]
    [InlineData("J-PAIR-VALUE")]
    [InlineData("J-PAIR-KEY")]
    [InlineData("J-DUPLICATE-LAST-FIRST-POSITION")]
    [InlineData("J-ESCAPED-DUPLICATE-ALIAS")]
    public void PythonInterruptProjectsRawJsonAfterOwnerDisposalWithoutChangingTheReason(string id)
    {
        var selected = Cases[id];
        Interrupt pending;
        using (var document = JsonDocument.Parse(selected.RawJson))
            pending = new("id", "name", "native-call", document.RootElement);
        var result = new AgentResult(StopReasons.EndTurn, Message.Assistant("hidden"), new(), 1,
            new Dictionary<string, object?>())
        { Interrupts = [pending] };
        Assert.Equal(selected.RawJson, pending.Reason!.Value.GetRawText());
        try
        {
            Assert.Equal("[{'id': 'id', 'name': 'name', 'reason': " + selected.ValueRepr + ", 'response': None}]",
                AgentResultRendering.Render(result, AgentResultOutputProfile.Python));
        }
        finally
        {
            Assert.Equal(selected.RawJson, pending.Reason!.Value.GetRawText());
            Assert.Equal("native-call", pending.ToolUseId);
            Assert.Same(pending, Assert.Single(result.Interrupts));
        }
    }

    private sealed record Case(string RawJson, string ValueRepr);
}
