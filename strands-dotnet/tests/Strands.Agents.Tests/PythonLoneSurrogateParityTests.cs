using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class PythonLoneSurrogateParityTests
{
    [Fact]
    public void PythonInterruptRenderingPreservesEscapedLoneSurrogateAfterDocumentDisposal()
    {
        const string raw = "\"\\ud800\"";
        Interrupt pending;
        using (var document = JsonDocument.Parse(raw))
            pending = new("id", "name", "native-call", document.RootElement);
        var result = new AgentResult(StopReasons.EndTurn, Message.Assistant("hidden"), new(), 1,
            new Dictionary<string, object?>())
        { Interrupts = [pending] };
        Assert.Equal(raw, pending.Reason!.Value.GetRawText());
        try
        {
            Assert.Equal("[{'id': 'id', 'name': 'name', 'reason': '\\ud800', 'response': None}]",
                AgentResultRendering.Render(result, AgentResultOutputProfile.Python));
        }
        finally
        {
            Assert.Equal(raw, pending.Reason!.Value.GetRawText());
            Assert.Equal("native-call", pending.ToolUseId);
            Assert.Same(pending, Assert.Single(result.Interrupts));
        }
    }
}
