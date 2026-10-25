using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Strands.Agents.Models;
using static Strands.Agents.Tests.Models.LlamaApiModelTests;
using static Strands.Agents.Tests.Models.LlamaApiTestTransport;

namespace Strands.Agents.Tests.Models;

[Collection("Llama API diagnostics")]
public sealed class LlamaApiProtocolTests
{
    [Theory]
    [InlineData("This model's maximum context length is 128000")]
    [InlineData("will exceed context limit")]
    [InlineData("model's maximum context limit")]
    [InlineData("is longer than the model's context length")]
    [InlineData("prompt is too long")]
    [InlineData("too many tokens")]
    public async Task EverySourceBadRequestOverflowPhraseRetainsTheTypedFailure(string message)
    {
        using var transport = new LlamaApiTestTransport(JsonSerializer.Serialize(new { error = new { message } }), HttpStatusCode.BadRequest); using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<ContextWindowOverflowException>(() => CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")])));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task ASourceSupportedBareJsonErrorAfterCompletionCannotBecomeSuccess()
    {
        using var transport = new LlamaApiTestTransport(TextStream() + "{\"error\":{\"message\":\"native error\"}}\n\n"); using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")])));
        Assert.Contains("native error", error.Message);
    }

    [Theory]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("9223372036854775807.00000000001")]
    public async Task FractionalCountsCannotBeRoundedIntoValidIntegralUsage(string number)
    {
        using var transport = new LlamaApiTestTransport(TextStream() + UsageFrame(number)); using var client = new HttpClient(transport);
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")])));
    }

    [Theory]
    [InlineData("100.000", 100)]
    [InlineData("1e2", 100)]
    [InlineData("1000e-1", 100)]
    [InlineData("0e999999999999999999999", 0)]
    [InlineData("-0.0", 0)]
    public async Task IntegralFloatSchemaValuesAreParsedExactly(string number, long expected)
    {
        using var transport = new LlamaApiTestTransport(TextStream() + UsageFrame(number)); using var client = new HttpClient(transport);
        var usage = Assert.Single((await CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")]))).OfType<ModelMetadataEvent>());
        Assert.Equal(expected, usage.Usage.InputTokens); Assert.True(usage.UsageReported);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0.5")]
    [InlineData("\"10\"")]
    [InlineData("9223372036854775808")]
    [InlineData("1e100")]
    [InlineData("1e-100")]
    public async Task InvalidTokenMetricsNeverBecomeReportedUsage(string number)
    {
        using var transport = new LlamaApiTestTransport(TextStream() + UsageFrame(number)); using var client = new HttpClient(transport);
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")])));
    }

    [Theory]
    [InlineData(ToolChoiceMode.Auto, false)]
    [InlineData(ToolChoiceMode.Any, false)]
    [InlineData(ToolChoiceMode.None, false)]
    [InlineData(ToolChoiceMode.Tool, false)]
    [InlineData(ToolChoiceMode.Auto, true)]
    [InlineData(ToolChoiceMode.Any, true)]
    [InlineData(ToolChoiceMode.None, true)]
    [InlineData(ToolChoiceMode.Tool, true)]
    public async Task ToolChoiceIsOmittedWithWarningUnlessNativeWireSupportIsExplicitlyEnabled(ToolChoiceMode mode, bool honor)
    {
        using var transport = new LlamaApiTestTransport(TextStream()); using var client = new HttpClient(transport); using var listener = new WarningListener(); Trace.Listeners.Add(listener);
        try
        {
            var schema = System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" });
            var request = new ModelRequest([Message.User("input")], Tools: [new("lookup", "Lookup", schema)]) { ToolChoice = new(mode, mode == ToolChoiceMode.Tool ? "lookup" : null) };
            await CollectAsync(new LlamaApiModel(client, new("meta", "key") { HonorToolChoice = honor }), request);
            var body = Assert.Single(transport.Bodies);
            if (!honor) { Assert.False(body.TryGetProperty("tool_choice", out _)); Assert.Contains("ToolChoice was provided", listener.Text); }
            else
            {
                Assert.DoesNotContain("ToolChoice was provided", listener.Text); var choice = body.GetProperty("tool_choice");
                if (mode == ToolChoiceMode.Tool) Assert.Equal("lookup", choice.GetProperty("function").GetProperty("name").GetString());
                else Assert.Equal(mode == ToolChoiceMode.Any ? "required" : mode == ToolChoiceMode.None ? "none" : "auto", choice.GetString());
            }
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task RawMetaErrorsRemainOptInAndUnrelatedUnknownFieldsRemainIgnored()
    {
        var openAi = Frame("""{"choices":[{"index":0,"delta":{"role":"assistant","content":"unchanged"},"finish_reason":"stop"}]}""") + "{\"error\":{\"message\":\"unknown SSE field\"}}\n\n" + Frame("[DONE]");
        using var ordinary = new LlamaApiTestTransport(openAi); using var ordinaryClient = new HttpClient(ordinary);
        Assert.Equal("unchanged", Complete(await CollectAsync(new OpenAIModel(ordinaryClient, new("model", "key")), new([Message.User("input")]))).Message.Text);
        using var meta = new LlamaApiTestTransport(TextStream() + "unknown: value\n{\"vendor\":1}\n\n"); using var metaClient = new HttpClient(meta);
        Assert.Equal("answer", Complete(await CollectAsync(new LlamaApiModel(metaClient, new("model", "key")), new([Message.User("input")]))).Message.Text);
    }

    [Theory]
    [InlineData("{\"event\":{\"event_type\":\"progress\",\"delta\":{\"type\":\"text\",\"text\":\"late\"}}}")]
    [InlineData("{\"event\":{\"event_type\":\"metrics\",\"delta\":{\"type\":\"text\",\"text\":\"late\"}}}")]
    [InlineData("{\"event\":{\"event_type\":\"complete\",\"delta\":{\"type\":\"text\",\"text\":\"\"},\"stop_reason\":\"stop\"}}")]
    public async Task ExtraContentOrRepeatedCompletionAfterFinishIsRejected(string suffix)
    {
        using var transport = new LlamaApiTestTransport(TextStream() + Frame(suffix)); using var client = new HttpClient(transport);
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")])));
    }

    [Fact]
    public async Task MissingCompletionCannotTurnAClosedTransportIntoAnAnswer()
    {
        using var transport = new LlamaApiTestTransport(Frame("""{"event":{"event_type":"start","delta":{"type":"text","text":"partial"}}}""")); using var client = new HttpClient(transport);
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")])));
    }

    private sealed class WarningListener : TraceListener
    {
        internal string Text = "";
        public override void Write(string? message) => Text += message;
        public override void WriteLine(string? message) => Text += message;
    }

    internal static string UsageFrame(string input) => Frame("{\"event\":{\"event_type\":\"metrics\",\"delta\":{\"type\":\"text\",\"text\":\"\"},\"metrics\":[{\"metric\":\"num_prompt_tokens\",\"value\":" + input + "},{\"metric\":\"num_completion_tokens\",\"value\":0},{\"metric\":\"num_total_tokens\",\"value\":0}]}}");
}

[CollectionDefinition("Llama API diagnostics", DisableParallelization = true)]
public sealed class LlamaApiDiagnosticCollection { }
