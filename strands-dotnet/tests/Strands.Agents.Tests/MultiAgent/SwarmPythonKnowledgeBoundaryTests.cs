using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class SwarmPythonKnowledgeBoundaryTests
{
    // Constants are captured CPython3.10.2/UCD13 json.loads+repr, never the implementation under test.
    // Historical-UCD assertions deliberately stay strict: a host-version mismatch remains an observed gap.
    private static readonly IReadOnlyDictionary<string, Case> Cases = new Dictionary<string, Case>(StringComparer.Ordinal)
    {
        ["N-ZERO"] = new("0", "\u2022 memory: {'value': 0}\n", "0"),
        ["N-INT-NEG-ZERO"] = new("-0", "\u2022 memory: {'value': 0}\n", "0"),
        ["N-BIGINT"] = new("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "\u2022 memory: {'value': 12345678901234567890123456789012345678901234567890123456789012345678901234567890}\n", "12345678901234567890123456789012345678901234567890123456789012345678901234567890"),
        ["N-FLOAT-ONE"] = new("1.0", "\u2022 memory: {'value': 1.0}\n", "1.0"),
        ["N-EXP-ONE"] = new("1e0", "\u2022 memory: {'value': 1.0}\n", "1.0"),
        ["N-EXP-THOUSAND"] = new("1e3", "\u2022 memory: {'value': 1000.0}\n", "1000.0"),
        ["N-FLOAT-NEG-ZERO"] = new("-0.0", "\u2022 memory: {'value': -0.0}\n", "-0.0"),
        ["N-EXP-NEG-ZERO"] = new("-0e0", "\u2022 memory: {'value': -0.0}\n", "-0.0"),
        ["N-SMALL-EXP"] = new("1e-5", "\u2022 memory: {'value': 1e-05}\n", "1e-05"),
        ["N-SMALL-FIXED"] = new("1e-4", "\u2022 memory: {'value': 0.0001}\n", "0.0001"),
        ["N-LARGE-EXP"] = new("1e16", "\u2022 memory: {'value': 1e+16}\n", "1e+16"),
        ["N-LARGE-FIXED"] = new("1e15", "\u2022 memory: {'value': 1000000000000000.0}\n", "1000000000000000.0"),
        ["N-FRACTION"] = new("1.25", "\u2022 memory: {'value': 1.25}\n", "1.25"),
        ["N-TRAILING-ZEROS"] = new("1.2500", "\u2022 memory: {'value': 1.25}\n", "1.25"),
        ["N-DOUBLE-NEAREST"] = new("1000000000000000100.0", "\u2022 memory: {'value': 1.0000000000000001e+18}\n", "1.0000000000000001e+18"),
        ["N-MAX-FINITE"] = new("1.7976931348623157e308", "\u2022 memory: {'value': 1.7976931348623157e+308}\n", "1.7976931348623157e+308"),
        ["N-MIN-SUBNORMAL"] = new("5e-324", "\u2022 memory: {'value': 5e-324}\n", "5e-324"),
        ["N-POSITIVE-OVERFLOW"] = new("1e400", "\u2022 memory: {'value': inf}\n", "inf"),
        ["N-NEGATIVE-OVERFLOW"] = new("-1e400", "\u2022 memory: {'value': -inf}\n", "-inf"),
        ["N-POSITIVE-UNDERFLOW"] = new("1e-400", "\u2022 memory: {'value': 0.0}\n", "0.0"),
        ["N-NEGATIVE-UNDERFLOW"] = new("-1e-400", "\u2022 memory: {'value': -0.0}\n", "-0.0"),
        ["U-ASCII"] = new("\"ordinary\"", "\u2022 memory: {'value': 'ordinary'}\n", "'ordinary'"),
        ["U-NBSP"] = new("\"\\u00a0\"", "\u2022 memory: {'value': '\\xa0'}\n", "'\\xa0'"),
        ["U-ZERO-WIDTH"] = new("\"\\u200b\"", "\u2022 memory: {'value': '\\u200b'}\n", "'\\u200b'"),
        ["U-ASTRAL"] = new("\"\\ud83d\\ude42\"", "\u2022 memory: {'value': '\ud83d\ude42'}\n", "'\ud83d\ude42'"),
        ["U-NEWLINE"] = new("\"\\n\"", "\u2022 memory: {'value': '\\n'}\n", "'\\n'"),
        ["U-LINE-SEPARATOR"] = new("\"\\u2028\"", "\u2022 memory: {'value': '\\u2028'}\n", "'\\u2028'"),
        ["U-PRIVATE-USE"] = new("\"\\ue000\"", "\u2022 memory: {'value': '\\ue000'}\n", "'\\ue000'"),
        ["U-SOFT-HYPHEN"] = new("\"\\u00ad\"", "\u2022 memory: {'value': '\\xad'}\n", "'\\xad'"),
        ["U-COMBINING"] = new("\"e\\u0301\"", "\u2022 memory: {'value': 'e\u0301'}\n", "'e\u0301'"),
        ["U-BELL"] = new("\"\\u0007\"", "\u2022 memory: {'value': '\\x07'}\n", "'\\x07'"),
        ["U-LONE-SURROGATE"] = new("\"\\ud800\"", "\u2022 memory: {'value': '\\ud800'}\n", "'\\ud800'"),
        ["U-VERSION-BOUNDARY"] = new("\"\\ud83e\\udd79\"", "\u2022 memory: {'value': '\\U0001f979'}\n", "'\\U0001f979'"),
        ["N-MIN-NORMAL"] = new("2.2250738585072014e-308", "\u2022 memory: {'value': 2.2250738585072014e-308}\n", "2.2250738585072014e-308"),
        ["N-MAX-SUBNORMAL"] = new("2.225073858507201e-308", "\u2022 memory: {'value': 2.225073858507201e-308}\n", "2.225073858507201e-308"),
        ["N-HALF-SUB-TO-EVEN-ZERO"] = new("2.4703282292062327e-324", "\u2022 memory: {'value': 0.0}\n", "0.0"),
        ["N-ABOVE-HALF-SUB"] = new("2.4703282292062328e-324", "\u2022 memory: {'value': 5e-324}\n", "5e-324"),
        ["N-NEG-HALF-SUB"] = new("-2.4703282292062327e-324", "\u2022 memory: {'value': -0.0}\n", "-0.0"),
        ["N-HALF-UNIT-EVEN"] = new("1.00000000000000011102230246251565404236316680908203125", "\u2022 memory: {'value': 1.0}\n", "1.0"),
        ["N-ABOVE-HALF-UNIT"] = new("1.00000000000000011102230246251565404236316680908203126", "\u2022 memory: {'value': 1.0000000000000002}\n", "1.0000000000000002"),
        ["N-MAX-ROUND-DOWN"] = new("1.7976931348623158e308", "\u2022 memory: {'value': 1.7976931348623157e+308}\n", "1.7976931348623157e+308"),
        ["N-MAX-ROUND-UP"] = new("1.7976931348623159e308", "\u2022 memory: {'value': inf}\n", "inf"),
        ["N-HUGE-POS-EXP"] = new("1e999999999999999999999999", "\u2022 memory: {'value': inf}\n", "inf"),
        ["N-HUGE-NEG-EXP"] = new("1e-999999999999999999999999", "\u2022 memory: {'value': 0.0}\n", "0.0"),
        ["N-HUGE-NEG-UNDERFLOW"] = new("-1e-999999999999999999999999", "\u2022 memory: {'value': -0.0}\n", "-0.0"),
        ["N-ZERO-HUGE-POS-EXP"] = new("0.000e999999999999999999999999", "\u2022 memory: {'value': 0.0}\n", "0.0"),
        ["N-NEG-ZERO-HUGE-NEG-EXP"] = new("-0.000e-999999999999999999999999", "\u2022 memory: {'value': -0.0}\n", "-0.0"),
        ["N-ARBITRARY-INT256"] = new("1000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000007", "\u2022 memory: {'value': 1000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000007}\n", "1000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000007"),
        ["N-SCI-21"] = new("1000000000000000000000.0", "\u2022 memory: {'value': 1e+21}\n", "1e+21"),
        ["N-NEG-MIN-SUBNORMAL"] = new("-5e-324", "\u2022 memory: {'value': -5e-324}\n", "-5e-324"),
        ["U-SPACE"] = new("\" \"", "\u2022 memory: {'value': ' '}\n", "' '"),
        ["U-PARAGRAPH-SEPARATOR"] = new("\"\\u2029\"", "\u2022 memory: {'value': '\\u2029'}\n", "'\\u2029'"),
        ["U-UNASSIGNED-BMP"] = new("\"\\u0378\"", "\u2022 memory: {'value': '\\u0378'}\n", "'\\u0378'"),
        ["U-PRIVATE-SUPPLEMENTARY"] = new("\"\\udb80\\udc00\"", "\u2022 memory: {'value': '\\U000f0000'}\n", "'\\U000f0000'"),
        ["U-NONCHARACTER-SUPPLEMENTARY"] = new("\"\\udbff\\udfff\"", "\u2022 memory: {'value': '\\U0010ffff'}\n", "'\\U0010ffff'"),
        ["U-TAG-FORMAT-SUPPLEMENTARY"] = new("\"\\udb40\\udc01\"", "\u2022 memory: {'value': '\\U000e0001'}\n", "'\\U000e0001'"),
        ["U-NEXT-LINE"] = new("\"\\u0085\"", "\u2022 memory: {'value': '\\x85'}\n", "'\\x85'"),
        ["U-ZERO-WIDTH-JOINER"] = new("\"\\u200d\"", "\u2022 memory: {'value': '\\u200d'}\n", "'\\u200d'"),
        ["U-THREAD-UCD11"] = new("\"\\ud83e\\uddf5\"", "\u2022 memory: {'value': '\ud83e\uddf5'}\n", "'\ud83e\uddf5'"),
        ["COMPOUND"] = new("{\"a\":[-0,1e0,-0.0,1e400,5e-324],\"q\":\"can't\",\"d\":\"say \\\"hi\\\"\",\"s\":\"a\\\\\\n\\u00a0\",\"p\":\"\\ud83d\\ude42\"}", "\u2022 memory: {'value': {'a': [0, 1.0, -0.0, inf, 5e-324], 'q': \"can't\", 'd': 'say \"hi\"', 's': 'a\\\\\\n\\xa0', 'p': '\ud83d\ude42'}}\n", "{'a': [0, 1.0, -0.0, inf, 5e-324], 'q': \"can't\", 'd': 'say \"hi\"', 's': 'a\\\\\\n\\xa0', 'p': '\ud83d\ude42'}"),
    };

    [Theory]
    [InlineData("N-ZERO")]
    [InlineData("N-INT-NEG-ZERO")]
    [InlineData("N-BIGINT")]
    [InlineData("N-FLOAT-ONE")]
    [InlineData("N-EXP-ONE")]
    [InlineData("N-EXP-THOUSAND")]
    [InlineData("N-FLOAT-NEG-ZERO")]
    [InlineData("N-EXP-NEG-ZERO")]
    [InlineData("N-SMALL-EXP")]
    [InlineData("N-SMALL-FIXED")]
    [InlineData("N-LARGE-EXP")]
    [InlineData("N-LARGE-FIXED")]
    [InlineData("N-FRACTION")]
    [InlineData("N-TRAILING-ZEROS")]
    [InlineData("N-DOUBLE-NEAREST")]
    [InlineData("N-MAX-FINITE")]
    [InlineData("N-MIN-SUBNORMAL")]
    [InlineData("N-POSITIVE-OVERFLOW")]
    [InlineData("N-NEGATIVE-OVERFLOW")]
    [InlineData("N-POSITIVE-UNDERFLOW")]
    [InlineData("N-NEGATIVE-UNDERFLOW")]
    [InlineData("U-ASCII")]
    [InlineData("U-NBSP")]
    [InlineData("U-ZERO-WIDTH")]
    [InlineData("U-ASTRAL")]
    [InlineData("U-NEWLINE")]
    [InlineData("U-LINE-SEPARATOR")]
    [InlineData("U-PRIVATE-USE")]
    [InlineData("U-SOFT-HYPHEN")]
    [InlineData("U-COMBINING")]
    [InlineData("U-BELL")]
    [InlineData("U-VERSION-BOUNDARY")]
    [InlineData("N-MIN-NORMAL")]
    [InlineData("N-MAX-SUBNORMAL")]
    [InlineData("N-HALF-SUB-TO-EVEN-ZERO")]
    [InlineData("N-ABOVE-HALF-SUB")]
    [InlineData("N-NEG-HALF-SUB")]
    [InlineData("N-HALF-UNIT-EVEN")]
    [InlineData("N-ABOVE-HALF-UNIT")]
    [InlineData("N-MAX-ROUND-DOWN")]
    [InlineData("N-MAX-ROUND-UP")]
    [InlineData("N-HUGE-POS-EXP")]
    [InlineData("N-HUGE-NEG-EXP")]
    [InlineData("N-HUGE-NEG-UNDERFLOW")]
    [InlineData("N-ZERO-HUGE-POS-EXP")]
    [InlineData("N-NEG-ZERO-HUGE-NEG-EXP")]
    [InlineData("N-ARBITRARY-INT256")]
    [InlineData("N-SCI-21")]
    [InlineData("N-NEG-MIN-SUBNORMAL")]
    [InlineData("U-SPACE")]
    [InlineData("U-PARAGRAPH-SEPARATOR")]
    [InlineData("U-UNASSIGNED-BMP")]
    [InlineData("U-PRIVATE-SUPPLEMENTARY")]
    [InlineData("U-NONCHARACTER-SUPPLEMENTARY")]
    [InlineData("U-TAG-FORMAT-SUPPLEMENTARY")]
    [InlineData("U-NEXT-LINE")]
    [InlineData("U-ZERO-WIDTH-JOINER")]
    [InlineData("U-THREAD-UCD11")]
    public async Task PythonProfileMatchesCapturedNumberAndScalarReference(string id)
    {
        var selected = Cases[id];
        var prompt = await PromptAsync(selected.RawJson, SwarmContextTextProfile.PythonRepr);
        Assert.Contains(selected.ExpectedLine, prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("N-INT-NEG-ZERO")]
    [InlineData("N-EXP-ONE")]
    [InlineData("N-NEGATIVE-UNDERFLOW")]
    [InlineData("N-MIN-SUBNORMAL")]
    [InlineData("N-POSITIVE-OVERFLOW")]
    [InlineData("U-NBSP")]
    [InlineData("U-PRIVATE-SUPPLEMENTARY")]
    [InlineData("U-ASTRAL")]
    public void SharedPythonInterruptProjectionUsesTheSameDetachedNumberAndScalarRules(string id)
    {
        var selected = Cases[id];
        Interrupt pending;
        using (var document = JsonDocument.Parse(selected.RawJson))
            pending = new("id", "name", "native-call", document.RootElement);
        var result = Result() with { StructuredOutput = Parse("{\"hidden\":true}"), Interrupts = [pending] };

        var rendered = AgentResultRendering.Render(result, AgentResultOutputProfile.Python);

        Assert.Equal("[{'id': 'id', 'name': 'name', 'reason': " + selected.ValueRepr + ", 'response': None}]", rendered);
        Assert.Same(pending, Assert.Single(result.Interrupts));
        Assert.Equal("native-call", pending.ToolUseId);
        using var native = JsonDocument.Parse(AgentResultRendering.Render(result, AgentResultOutputProfile.TypeScript));
        var nativeRow = Assert.Single(native.RootElement.EnumerateArray());
        Assert.Equal("hook", nativeRow.GetProperty("source").GetString());
        Assert.False(nativeRow.TryGetProperty("response", out _));
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    public async Task PythonKnowledgePresentationIsCultureIndependent(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var selected = Cases["N-DOUBLE-NEAREST"];
            Assert.Contains(selected.ExpectedLine, await PromptAsync(selected.RawJson, SwarmContextTextProfile.PythonRepr), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    [Fact]
    public async Task NestedKnowledgeUsesSourceOrderingQuotesAndNumberKindsAfterOwnerDisposal()
    {
        var selected = Cases["COMPOUND"];
        Assert.Contains(selected.ExpectedLine, await PromptAsync(selected.RawJson, SwarmContextTextProfile.PythonRepr), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeJsonProfileRetainsExtremeRawNumericTokens()
    {
        const string raw = "{\"overflow\":1e400,\"underflow\":-1e-999999999999999999999999,\"negativeZero\":-0.0,\"integerNegativeZero\":-0}";
        Assert.Contains("• memory: {\"value\":" + raw + "}\n", await PromptAsync(raw, SwarmContextTextProfile.NativeJson), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonLoneSurrogateReasonUsesPythonEscapesWithoutChangingStoredJson()
    {
        // Required Python parity replaces the retained historical decode rejection.
        var selected = Cases["U-LONE-SURROGATE"];
        Interrupt pending;
        using (var document = JsonDocument.Parse(selected.RawJson))
            pending = new("id", "name", "native-call", document.RootElement);
        var result = Result() with { Interrupts = [pending] };
        Assert.Equal("[{'id': 'id', 'name': 'name', 'reason': " + selected.ValueRepr + ", 'response': None}]",
            AgentResultRendering.Render(result, AgentResultOutputProfile.Python));
        Assert.Equal(selected.RawJson, pending.Reason!.Value.GetRawText());
        Assert.Equal(JsonValueKind.String, pending.Reason!.Value.ValueKind);
    }

    [Fact]
    public void DirectStringIdentityEscapesUnpairedUtf16WithoutChangingTheSourceString()
    {
        const string id = "id\ud800";
        const string name = "name\udc00";
        var pending = new Interrupt(id, name, "native-call");
        var rendered = AgentResultRendering.Render(Result() with { Interrupts = [pending] }, AgentResultOutputProfile.Python);
        Assert.Equal("[{'id': 'id\\ud800', 'name': 'name\\udc00', 'reason': None, 'response': None}]", rendered);
        Assert.Equal(id, pending.Id);
        Assert.Equal(name, pending.Name);
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
        AssertJsonPreserved(expected.RootElement, stored);
        var input = Assert.Single(Assert.Single(model.Requests).Messages, message => message.Role == Role.User);
        return input.Text;
    }

    private static void AssertJsonPreserved(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);
        switch (expected.ValueKind)
        {
            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), actual.GetString());
                break;
            case JsonValueKind.Number:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
            case JsonValueKind.Array:
                var expectedItems = expected.EnumerateArray().ToArray();
                var actualItems = actual.EnumerateArray().ToArray();
                Assert.Equal(expectedItems.Length, actualItems.Length);
                for (var index = 0; index < expectedItems.Length; index++)
                    AssertJsonPreserved(expectedItems[index], actualItems[index]);
                break;
            case JsonValueKind.Object:
                var expectedProperties = expected.EnumerateObject().ToArray();
                var actualProperties = actual.EnumerateObject().ToArray();
                Assert.Equal(expectedProperties.Length, actualProperties.Length);
                for (var index = 0; index < expectedProperties.Length; index++)
                {
                    Assert.Equal(expectedProperties[index].Name, actualProperties[index].Name);
                    AssertJsonPreserved(expectedProperties[index].Value, actualProperties[index].Value);
                }
                break;
        }
    }

    private static AgentResult Result() => new(StopReasons.EndTurn, Message.Assistant("hidden"), new(), 1, new Dictionary<string, object?>());
    private static JsonElement Parse(string raw) { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
    private sealed record Case(string RawJson, string ExpectedLine, string ValueRepr);
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
