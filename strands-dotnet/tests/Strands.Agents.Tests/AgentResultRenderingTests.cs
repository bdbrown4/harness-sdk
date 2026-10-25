using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class AgentResultRenderingTests
{
    [Fact]
    public void TypeScriptUsesOrderedTextReasoningAndCitationFragments()
    {
        var result = Blocks();
        Assert.Equal("first\n💭 Reasoning:\n   line1\n   line2\ncitation1\ncitation2\nlast", AgentResultRendering.Render(result));
        Assert.Equal("firstcitation1citation2last", result.ToString());
    }

    [Fact]
    public void PythonAddsTrailingNewlinesAndOmitsReasoning()
    {
        Assert.Equal("first\ncitation1\ncitation2\nlast\n", AgentResultRendering.Render(Blocks(), AgentResultOutputProfile.Python));
    }

    [Fact]
    public void TypeScriptInterruptsOverrideStructuredAndKeepPendingProjection()
    {
        var result = AgentResultJsonTests.Result() with
        {
            StructuredOutput = AgentResultJsonTests.Parse("{\"hidden\":true}"),
            Interrupts = [new("id", "approval", "native-call", AgentResultJsonTests.Parse("{\"ok\":true,\"text\":\"α\"}"), InterruptSource.RemoteAgent, true)]
        };
        using var json = JsonDocument.Parse(AgentResultRendering.Render(result));
        var request = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(new[] { "id", "name", "reason", "source" }, request.EnumerateObject().Select(p => p.Name));
        Assert.Equal("remote-agent", request.GetProperty("source").GetString());
        Assert.Equal("α", request.GetProperty("reason").GetProperty("text").GetString());
        Assert.False(request.TryGetProperty("toolUseId", out _));
        Assert.False(request.TryGetProperty("response", out _));
        Assert.Equal("native-call", Assert.Single(result.Interrupts).ToolUseId);
        Assert.True(result.Interrupts[0].IsBatch);
    }

    [Fact]
    public void PythonInterruptReprUsesJsonDomainAndPendingNoneResponse()
    {
        var result = AgentResultJsonTests.Result() with
        {
            StructuredOutput = AgentResultJsonTests.Parse("{\"hidden\":true}"),
            Interrupts = [new("id", "approval", "call", AgentResultJsonTests.Parse("{\"ok\":true,\"value\":null,\"text\":\"α\",\"list\":[false,2]}"))]
        };
        Assert.Equal("[{'id': 'id', 'name': 'approval', 'reason': {'ok': True, 'value': None, 'text': 'α', 'list': [False, 2]}, 'response': None}]", AgentResultRendering.Render(result, AgentResultOutputProfile.Python));
    }

    [Fact]
    public void StructuredPresenceBeatsTextAndEmptyInterruptsFallThrough()
    {
        foreach (var value in new[] { "null", "false", "0", "\"\"", "{\"value\":7}" })
        {
            var result = AgentResultJsonTests.Result() with { StructuredOutput = AgentResultJsonTests.Parse(value), Interrupts = [] };
            using var rendered = JsonDocument.Parse(AgentResultRendering.Render(result));
            Assert.True(JsonElement.DeepEquals(result.StructuredOutput!.Value, rendered.RootElement));
        }
        var structured = AgentResultJsonTests.Result() with { StructuredOutput = AgentResultJsonTests.Parse("{\"value\":7}") };
        Assert.Equal("{\"value\":7}", AgentResultRendering.Render(structured, AgentResultOutputProfile.Python));
        Assert.Equal("", AgentResultRendering.Render(new("custom", null, new(), 0, new Dictionary<string, object?>())));
        Assert.Equal("", AgentResultRendering.Render(new("custom", new(Role.Assistant, []), new(), 0, new Dictionary<string, object?>()), AgentResultOutputProfile.Python));
    }

    [Fact]
    public void RenderingConformanceR01EveryPendingSourceHasExplicitPresentationMapping()
    {
        var sources = new[] { InterruptSource.Tool, InterruptSource.Hook, InterruptSource.Middleware, InterruptSource.OrchestratorHook, InterruptSource.RemoteAgent };
        var names = new[] { "tool", "hook", "middleware", "multiagent-hook", "remote-agent" };
        for (var i = 0; i < sources.Length; i++)
        {
            var pending = new Interrupt("id", "name", "native-call", source: sources[i], isBatch: true);
            var result = AgentResultJsonTests.Result() with { Interrupts = [pending] };
            using var document = JsonDocument.Parse(AgentResultRendering.Render(result));
            Assert.Equal(names[i], document.RootElement[0].GetProperty("source").GetString());
            Assert.Same(pending, Assert.Single(result.Interrupts));
            Assert.Equal("native-call", pending.ToolUseId);
            Assert.True(pending.IsBatch);
        }
    }

    [Fact]
    public void RenderingConformanceR02AbsentAndExplicitNullReasonStayDistinctInTypeScript()
    {
        var absent = AgentResultJsonTests.Result() with { Interrupts = [new("id", "name", "call")] };
        var explicitNull = AgentResultJsonTests.Result() with { Interrupts = [new("id", "name", "call", AgentResultJsonTests.Parse("null"))] };
        using var first = JsonDocument.Parse(AgentResultRendering.Render(absent));
        using var second = JsonDocument.Parse(AgentResultRendering.Render(explicitNull));
        Assert.False(first.RootElement[0].TryGetProperty("reason", out _));
        Assert.Equal(JsonValueKind.Null, second.RootElement[0].GetProperty("reason").ValueKind);
        Assert.Equal(AgentResultRendering.Render(absent, AgentResultOutputProfile.Python), AgentResultRendering.Render(explicitNull, AgentResultOutputProfile.Python));
        Assert.Contains("'reason': None, 'response': None", AgentResultRendering.Render(absent, AgentResultOutputProfile.Python));
    }

    [Fact]
    public void RenderingConformanceR03PythonJsonDomainEscapesWithoutCallingForeignObjects()
    {
        var reason = JsonSerializer.SerializeToElement(new object?[] { "can't", "say \"hi\"", "\\\n\t\b\f", "α", 1.25m, true, null });
        var result = AgentResultJsonTests.Result() with { Interrupts = [new("id", "name", "call", reason)] };
        var rendered = AgentResultRendering.Render(result, AgentResultOutputProfile.Python);
        Assert.Contains("\"can't\"", rendered);
        Assert.Contains("'say \"hi\"'", rendered);
        Assert.Contains("\\\\\\n\\t\\x08\\x0c", rendered);
        Assert.Contains("'α', 1.25, True, None", rendered);
        Assert.EndsWith("'response': None}]", rendered);
    }

    [Fact]
    public void RenderingConformanceR04MultipleInterruptsWinWithoutRetainingExtraCorrelation()
    {
        var pending = new[] { new Interrupt("first", "one", "tool-one"), new Interrupt("second", "two", "tool-two", AgentResultJsonTests.Parse("false")) };
        var result = AgentResultJsonTests.Result() with { StructuredOutput = AgentResultJsonTests.Parse("{\"hidden\":true}"), Interrupts = pending };
        using var json = JsonDocument.Parse(AgentResultRendering.Render(result));
        Assert.Equal(new[] { "first", "second" }, json.RootElement.EnumerateArray().Select(value => value.GetProperty("id").GetString()));
        Assert.DoesNotContain("hidden", AgentResultRendering.Render(result, AgentResultOutputProfile.Python));
        Assert.DoesNotContain("tool-one", AgentResultRendering.Render(result));
        Assert.Same(pending, result.Interrupts);
    }

    [Fact]
    public void RenderingConformanceR05EveryPresentStructuredValueUsesNativeUnionPresence()
    {
        foreach (var text in new[] { "null", "false", "0", "\"\"", "[]", "{}" })
            foreach (var profile in new[] { AgentResultOutputProfile.TypeScript, AgentResultOutputProfile.Python })
            {
                var result = AgentResultJsonTests.Result() with { StructuredOutput = AgentResultJsonTests.Parse(text) };
                using var rendered = JsonDocument.Parse(AgentResultRendering.Render(result, profile));
                Assert.True(JsonElement.DeepEquals(result.StructuredOutput!.Value, rendered.RootElement));
            }
    }

    [Fact]
    public void RenderingConformanceR06JsonNumbersAndUnicodeRetainValuesUnderNativeCulture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            var result = AgentResultJsonTests.Result() with { StructuredOutput = AgentResultJsonTests.Parse(" { \"n\":1.00e2, \"u\":\"α\\n\\u0061\" } ") };
            foreach (var profile in new[] { AgentResultOutputProfile.TypeScript, AgentResultOutputProfile.Python })
            {
                var text = AgentResultRendering.Render(result, profile);
                using var json = JsonDocument.Parse(text);
                Assert.Equal(100, json.RootElement.GetProperty("n").GetDouble());
                Assert.Equal("α\na", json.RootElement.GetProperty("u").GetString());
                Assert.DoesNotContain(" : ", text);
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void RenderingConformanceR07EmptyTextAndExistingNewlineBytesKeepSourceSeparators()
    {
        var result = AgentResultJsonTests.Result(new(Role.Assistant, [new TextBlock(""), new TextBlock("body\r\n"), new CitationsBlock([], [new(""), new("tail")])]));
        Assert.Equal("\nbody\r\n\n\ntail", AgentResultRendering.Render(result));
        Assert.Equal("\nbody\r\n\n\ntail\n", AgentResultRendering.Render(result, AgentResultOutputProfile.Python));
    }

    [Fact]
    public void RenderingConformanceR08ReasoningEmptyWhitespaceAndTrailingLineAreExplicit()
    {
        var result = AgentResultJsonTests.Result(new(Role.Assistant, [new ReasoningBlock(""), new ReasoningBlock(" "), new ReasoningBlock("line\n"), new TextBlock("answer")]));
        Assert.Equal("💭 Reasoning:\n    \n💭 Reasoning:\n   line\n   \nanswer", AgentResultRendering.Render(result));
        Assert.Equal("answer\n", AgentResultRendering.Render(result, AgentResultOutputProfile.Python));
    }

    [Fact]
    public void RenderingConformanceR09CitationGeneratedTextDoesNotExposeEvidence()
    {
        var citation = new Citation(new WebCitationLocation("https://example.invalid"), "source-secret", [new("evidence-secret")], "title-secret");
        var result = AgentResultJsonTests.Result(new(Role.Assistant, [new TextBlock("before"), new CitationsBlock([citation], [new("generated-one"), new("generated-two")]), new TextBlock("after")]));
        Assert.Equal("before\ngenerated-one\ngenerated-two\nafter", AgentResultRendering.Render(result));
        Assert.Equal("before\ngenerated-one\ngenerated-two\nafter\n", AgentResultRendering.Render(result, AgentResultOutputProfile.Python));
    }

    [Fact]
    public void RenderingConformanceR10OpaqueArmsRemainSkippedWithoutChangingLegacyText()
    {
        var result = AgentResultJsonTests.Result(new(Role.Assistant, [new TextBlock("visible"), AgentResultJsonTests.NativeArm("openaiResponsesContent"), AgentResultJsonTests.NativeArm("googleContent"), AgentResultJsonTests.NativeArm("anthropicContent"), AgentResultJsonTests.NativeArm("openaiResponsesReasoning"), new ImageBlock("image/png", "AAEC")]));
        Assert.Equal("visible", AgentResultRendering.Render(result));
        Assert.Equal("visible\n", AgentResultRendering.Render(result, AgentResultOutputProfile.Python));
        Assert.Equal("visibleopaque-responseopaque-googleopaque-anthropic", result.ToString());
    }

    [Fact]
    public void RenderingConformanceR11NoMessageAndEmptyContentDoNotInventText()
    {
        foreach (var message in new Message?[] { null, new(Role.Assistant, []) })
            foreach (var profile in new[] { AgentResultOutputProfile.TypeScript, AgentResultOutputProfile.Python })
            {
                var result = new AgentResult("custom", message, new(), 0, new Dictionary<string, object?>());
                Assert.Equal("", AgentResultRendering.Render(result, profile));
                Assert.Equal("plain", AgentResultRendering.Render(result with { Message = Message.Assistant("plain") }, AgentResultOutputProfile.TypeScript));
            }
    }

    [Fact]
    public void RenderingConformanceR12OptInRenderingDoesNotAlterDefaultApiOrCollections()
    {
        var state = new Dictionary<string, object?> { ["caller"] = "untouched" };
        var message = new Message(Role.Assistant, [new TextBlock("one"), new TextBlock("two")]);
        var result = new AgentResult("custom", message, new(), 1, state);
        var oldJson = JsonSerializer.Serialize(result);
        var oldText = result.ToString();
        foreach (var profile in new[] { AgentResultOutputProfile.TypeScript, AgentResultOutputProfile.Python }) AgentResultRendering.Render(result, profile);
        Assert.Equal(oldJson, JsonSerializer.Serialize(result));
        Assert.Equal(oldText, result.ToString());
        Assert.Same(state, result.InvocationState);
        Assert.Same(message, result.Message);
        Assert.Equal(result, result with { });
    }

    private static AgentResult Blocks() => AgentResultJsonTests.Result(new(Role.Assistant, [
        new TextBlock("first"), new ReasoningBlock("line1\nline2", "signature"),
        new CitationsBlock([], [new("citation1"), new("citation2")]),
        new ImageBlock("image/png", "AAEC"), new ReasoningBlock(""), new TextBlock("last")
    ]));
}
