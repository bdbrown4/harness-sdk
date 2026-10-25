using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Bedrock.Tests;

public sealed class BedrockRequestOptionsTests
{
    [Fact]
    public void NativeRequestSettingsReachTheOfficialAwsRequest()
    {
        using var mapper = new BedrockRequestMapper();
        var native = mapper.Map(new([Message.User("question")]), new()
        {
            AdditionalModelResponseFieldPaths = ["/stop_sequence"],
            RequestMetadata = new Dictionary<string, string> { ["trace"] = "value" },
            PromptVariables = new Dictionary<string, string> { ["topic"] = "agents" },
            PerformanceLatency = "optimized",
            ServiceTier = "flex",
            Guardrail = new() { Identifier = "guard", Version = "DRAFT", Trace = "enabled_full", StreamProcessingMode = "async" }
        });

        Assert.Equal(new[] { "/stop_sequence" }, native.AdditionalModelResponseFieldPaths);
        Assert.Equal("value", native.RequestMetadata["trace"]);
        Assert.Equal("agents", native.PromptVariables["topic"].Text);
        Assert.Equal("optimized", native.PerformanceConfig.Latency.Value);
        Assert.Equal("flex", native.ServiceTier.Type.Value);
        Assert.Equal("guard", native.GuardrailConfig.GuardrailIdentifier);
        Assert.Equal("DRAFT", native.GuardrailConfig.GuardrailVersion);
        Assert.Equal("enabled_full", native.GuardrailConfig.Trace.Value);
        Assert.Equal("async", native.GuardrailConfig.StreamProcessingMode.Value);
    }

    [Theory]
    [InlineData("other-model", true, "error")]
    [InlineData("anthropic.claude-test", false, null)]
    [InlineData("other-model", null, null)]
    [InlineData("anthropic.claude-test", null, "error")]
    public void ExplicitToolStatusOverridesAutomaticDetection(string modelId, bool? include, string? expected)
    {
        using var mapper = new BedrockRequestMapper();
        var native = mapper.Map(new([new(Role.User, [new ToolResultBlock("call", "failure", ToolResultStatus.Error)])]), new() { ModelId = modelId, IncludeToolResultStatus = include });
        Assert.Equal(expected, Assert.Single(Assert.Single(native.Messages).Content).ToolResult.Status?.Value);
    }

    [Fact]
    public void LatestGuardSelectionSurvivesToolTurnsWithoutChangingTheOriginalMessage()
    {
        var selected = new Message(Role.User, [new TextBlock("latest"), new ImageBlock("image/png", "AQID"), new ImageBlock("image/gif", "AQID"), new ImageBlock("image/jpeg", new S3Location("s3://media-bucket/key")), new GuardContentBlock("explicit", qualifiers: [GuardQualifier.Query])]);
        var messages = new[] { Message.User("earlier"), selected, new(Role.Assistant, [new ToolUseBlock("call", "lookup", JsonSerializer.SerializeToElement(new { }))]), new(Role.User, [new ToolResultBlock("call", "answer")]) };
        using var mapper = new BedrockRequestMapper();
        var native = mapper.Map(new(messages), new() { Guardrail = new() { Identifier = "guard", Version = "1", GuardLatestUserMessage = true } });

        Assert.Equal("earlier", Assert.Single(native.Messages[0].Content).Text);
        var content = native.Messages[1].Content;
        Assert.Equal("latest", content[0].GuardContent.Text.Text);
        Assert.Null(content[0].Text);
        Assert.Equal("png", content[1].GuardContent.Image.Format.Value);
        Assert.Null(content[1].Image);
        Assert.Equal("gif", content[2].Image.Format.Value);
        Assert.Equal("s3://media-bucket/key", content[3].Image.Source.S3Location.Uri);
        Assert.Equal(new[] { "query" }, content[4].GuardContent.Text.Qualifiers);
        Assert.NotNull(Assert.Single(native.Messages[3].Content).ToolResult);
        Assert.IsType<TextBlock>(selected.Content[0]);
    }

    [Fact]
    public void PublishedCollectionsAreCopiedAndUpdatesPublishAtomically()
    {
        var paths = new[] { "/original" };
        var metadata = new Dictionary<string, string> { ["key"] = "original" };
        var variables = new Dictionary<string, string> { ["topic"] = "original" };
        using var model = new BedrockModel(new() { AdditionalModelResponseFieldPaths = paths, RequestMetadata = metadata, PromptVariables = variables });
        paths[0] = "/changed";
        metadata["key"] = "changed";
        variables["topic"] = "changed";
        var config = model.GetConfig();
        Assert.Equal("/original", Assert.Single(config.AdditionalModelResponseFieldPaths!));
        Assert.Equal("original", config.RequestMetadata!["key"]);
        Assert.Equal("original", config.PromptVariables!["topic"]);
        Assert.Throws<ArgumentException>(() => model.UpdateConfig(old => old with { Guardrail = new() { Identifier = "guard", Version = "1", Trace = "unknown" } }));
        Assert.Null(model.GetConfig().Guardrail);
    }

    [Theory]
    [InlineData("any")]
    [InlineData("tool")]
    public void ForcedToolsDropThinkingFromACopyOfModelFields(string choice)
    {
        var fields = JsonSerializer.SerializeToElement(new { thinking = new { type = "enabled", budget_tokens = 1024 }, top_k = 7 });
        using var mapper = new BedrockRequestMapper();
        var native = mapper.Map(new([Message.User("question")], Tools: [new("lookup", "Lookup", JsonSerializer.SerializeToElement(new { type = "object" }))]) { ToolChoice = choice == "any" ? new(ToolChoiceMode.Any) : new(ToolChoiceMode.Tool, "lookup") }, new() { AdditionalModelRequestFields = fields });
        Assert.False(native.AdditionalModelRequestFields.AsDictionary().ContainsKey("thinking"));
        Assert.Equal(7, native.AdditionalModelRequestFields.AsDictionary()["top_k"].AsInt());
        Assert.True(fields.TryGetProperty("thinking", out _));
    }

    [Fact]
    public void ThinkingOnlyFieldsAreOmittedForForcedToolsAndPreservedForAutomaticSelection()
    {
        var fields = JsonSerializer.SerializeToElement(new { thinking = new { type = "enabled", budget_tokens = 1024 } });
        using var mapper = new BedrockRequestMapper();
        var request = new ModelRequest([Message.User("question")]) { ToolChoice = new(ToolChoiceMode.Any) };
        Assert.True(mapper.Map(request, new() { AdditionalModelRequestFields = fields }).AdditionalModelRequestFields.IsNull());
        Assert.True(mapper.Map(request with { ToolChoice = new(ToolChoiceMode.Auto) }, new() { AdditionalModelRequestFields = fields }).AdditionalModelRequestFields.AsDictionary().ContainsKey("thinking"));
    }

    [Fact]
    public void GuardrailDefaultsAndAbsentOptionalFieldsStayDistinct()
    {
        using var mapper = new BedrockRequestMapper();
        var request = new ModelRequest([Message.User("question")]);
        var defaults = mapper.Map(request, new());
        Assert.Null(defaults.GuardrailConfig);
        Assert.Null(defaults.PerformanceConfig);
        Assert.Null(defaults.ServiceTier);
        Assert.Null(defaults.AdditionalModelResponseFieldPaths);
        Assert.Null(defaults.RequestMetadata);
        Assert.Null(defaults.PromptVariables);
        var guarded = mapper.Map(request, new() { Guardrail = new() { Identifier = "guard", Version = "1" } });
        Assert.Equal("enabled", guarded.GuardrailConfig.Trace.Value);
        Assert.Null(guarded.GuardrailConfig.StreamProcessingMode);
        Assert.Equal("question", Assert.Single(Assert.Single(guarded.Messages).Content).Text);
    }

    [Theory]
    [InlineData("identifier")]
    [InlineData("version")]
    [InlineData("trace")]
    [InlineData("processing")]
    [InlineData("paths")]
    [InlineData("metadata")]
    [InlineData("variables")]
    public void InvalidOptionsCannotReplaceAPreviouslyAcceptedConfiguration(string setting)
    {
        using var model = new BedrockModel(new() { ServiceTier = "priority" });
        var guardrail = new BedrockGuardrailOptions { Identifier = "guard", Version = "1" };
        Assert.Throws<ArgumentException>(() => model.UpdateConfig(old => setting switch
        {
            "identifier" => old with { Guardrail = guardrail with { Identifier = "" } },
            "version" => old with { Guardrail = guardrail with { Version = "" } },
            "trace" => old with { Guardrail = guardrail with { Trace = "invalid" } },
            "processing" => old with { Guardrail = guardrail with { StreamProcessingMode = "invalid" } },
            "paths" => old with { AdditionalModelResponseFieldPaths = [null!] },
            "metadata" => old with { RequestMetadata = new Dictionary<string, string> { ["key"] = null! } },
            _ => old with { PromptVariables = new Dictionary<string, string> { ["key"] = null! } }
        }));
        Assert.Equal("priority", model.GetConfig().ServiceTier);
        Assert.Null(model.GetConfig().Guardrail);
    }

    [Fact]
    public void ReturnedConfigurationDoesNotExposeMutablePublishedCollections()
    {
        using var model = new BedrockModel(new()
        {
            AdditionalModelResponseFieldPaths = ["/value"],
            RequestMetadata = new Dictionary<string, string> { ["key"] = "original" },
            PromptVariables = new Dictionary<string, string> { ["key"] = "original" }
        });
        var config = model.GetConfig();
        Assert.Throws<NotSupportedException>(() => ((IList<string>)config.AdditionalModelResponseFieldPaths!)[0] = "/changed");
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)config.RequestMetadata!)["key"] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)config.PromptVariables!)["key"] = "changed");
        Assert.Equal("original", model.GetConfig().RequestMetadata!["key"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\n")]
    public void AutomaticGuardSelectionPreservesBlankTextInsteadOfCreatingAnInvalidGuardBlock(string blank)
    {
        using var mapper = new BedrockRequestMapper();
        var native = mapper.Map(new([new(Role.User, [new TextBlock(blank), new TextBlock("valid")])]), new() { Guardrail = new() { Identifier = "guard", Version = "1", GuardLatestUserMessage = true } });
        var content = Assert.Single(native.Messages).Content;
        Assert.Equal(blank, content[0].Text);
        Assert.Null(content[0].GuardContent);
        Assert.Equal("valid", content[1].GuardContent.Text.Text);
    }

    [Fact]
    public void ManagedPromptVariablesDoNotIntroduceForbiddenEmptyInferenceConfiguration()
    {
        using var mapper = new BedrockRequestMapper();
        var native = mapper.Map(new([Strands.Agents.Message.User("question")]), new() { ModelId = "arn:aws:bedrock:us-east-1:123456789012:prompt/ABCDEFGHIJ:1", PromptVariables = new Dictionary<string, string> { ["topic"] = "agents" } });
        Assert.Null(native.InferenceConfig);
        var request = new Amazon.BedrockRuntime.Model.Internal.MarshallTransformations.ConverseStreamRequestMarshaller().Marshall(native);
        using var body = request.ContentStream;
        using var json = body is null ? JsonDocument.Parse(request.Content) : JsonDocument.Parse(body);
        Assert.False(json.RootElement.TryGetProperty("inferenceConfig", out _));
        Assert.Equal("agents", json.RootElement.GetProperty("promptVariables").GetProperty("topic").GetProperty("text").GetString());
    }
}
