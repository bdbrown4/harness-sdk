using System.Text;
using System.Text.Json;
using Amazon.SageMakerRuntime.Model;
using Strands.Agents.Models;

namespace Strands.Agents.SageMaker;

internal sealed class SageMakerInvocation
{
    private readonly Dictionary<string, string?> _headers;
    internal byte[] Body { get; }
    internal bool Stream { get; }
    internal SageMakerInvocation(SageMakerModelOptions options, Dictionary<string, object?> payload, object? responseFormat = null)
    {
        _headers = new(StringComparer.Ordinal) { ["EndpointName"] = options.EndpointName, ["ContentType"] = "application/json", ["Accept"] = "application/json", ["InferenceComponentName"] = options.InferenceComponentName, ["TargetModel"] = options.TargetModel, ["TargetVariant"] = options.TargetVariant };
        string? customBody = null;
        foreach (var item in ProviderConfiguration.Parameters(options.AdditionalArguments))
        {
            if (item.Value is not JsonElement { ValueKind: JsonValueKind.String } value) throw new ArgumentException("AWS endpoint arguments must be strings.", nameof(options));
            if (item.Key == "Body") customBody = value.GetString();
            else if (item.Key is "EndpointName" or "ContentType" or "Accept" or "InferenceComponentName" or "TargetModel" or "TargetVariant" or "TargetContainerHostname" or "InferenceId" or "CustomAttributes" or "SessionId" or "PrefixAwareId" or "EnableExplanations") _headers[item.Key] = value.GetString();
            else throw new ArgumentException("Unsupported SageMaker endpoint argument: " + item.Key, nameof(options));
        }
        if (responseFormat is not null)
        {
            if (customBody is not null)
            {
                using var parsed = JsonDocument.Parse(customBody);
                payload = ProviderConfiguration.Parameters(parsed.RootElement);
                customBody = null;
            }
            payload["stream"] = false;
            payload["response_format"] = responseFormat;
        }
        Guard.NotNullOrWhiteSpace(Get("EndpointName"), nameof(options.EndpointName));
        Stream = JsonSerializer.SerializeToElement(payload["stream"]).GetBoolean();
        if (Stream && (Get("TargetModel") is not null || _headers.ContainsKey("EnableExplanations"))) throw new NotSupportedException("InvokeEndpointWithResponseStream does not support TargetModel or EnableExplanations.");
        Body = customBody is null ? JsonSerializer.SerializeToUtf8Bytes(payload) : Encoding.UTF8.GetBytes(customBody);
    }
    internal InvokeEndpointRequest Request(MemoryStream body) => new()
    {
        EndpointName = Get("EndpointName"),
        ContentType = Get("ContentType"),
        Accept = Get("Accept"),
        Body = body,
        InferenceComponentName = Get("InferenceComponentName"),
        TargetModel = Get("TargetModel"),
        TargetVariant = Get("TargetVariant"),
        TargetContainerHostname = Get("TargetContainerHostname"),
        InferenceId = Get("InferenceId"),
        CustomAttributes = Get("CustomAttributes"),
        SessionId = Get("SessionId"),
        PrefixAwareId = Get("PrefixAwareId"),
        EnableExplanations = Get("EnableExplanations")
    };
    internal InvokeEndpointWithResponseStreamRequest StreamingRequest(MemoryStream body) => new()
    {
        EndpointName = Get("EndpointName"),
        ContentType = Get("ContentType"),
        Accept = Get("Accept"),
        Body = body,
        InferenceComponentName = Get("InferenceComponentName"),
        TargetVariant = Get("TargetVariant"),
        TargetContainerHostname = Get("TargetContainerHostname"),
        InferenceId = Get("InferenceId"),
        CustomAttributes = Get("CustomAttributes"),
        SessionId = Get("SessionId"),
        PrefixAwareId = Get("PrefixAwareId")
    };
    private string? Get(string key) => _headers.TryGetValue(key, out var value) ? value : null;
}
