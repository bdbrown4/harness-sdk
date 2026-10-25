using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.Documents;
using NativeMessage = Amazon.BedrockRuntime.Model.Message;

namespace Strands.Agents.Bedrock;

public sealed partial class BedrockModel
{
    private const string ThinkingBindingBeta = "thinking-binding-controls-2026-08-01";
    private string? _dropStaleThinkingModelId;
    private string? _separateToolResultModelId;

    private const string RejectedToolResultTurn = "Conversation blocks and tool result blocks cannot be provided in the same turn.";

    private ConverseStreamRequest ApplyRememberedToolResultTurns(ConverseStreamRequest request, BedrockModelOptions options)
    {
        if (!options.RecoverToolResultTurns || !string.Equals(Volatile.Read(ref _separateToolResultModelId), request.ModelId, StringComparison.Ordinal))
            return request;
        return TrySeparateToolResultTurns(request, out var recovered) ? recovered : request;
    }

    private async Task<T> SendWithRecoveryAsync<T>(ConverseStreamRequest request, BedrockModelOptions options,
        Func<ConverseStreamRequest, Task<T>> send, CancellationToken operation)
    {
        try { return await SendWithThinkingRecoveryAsync(request, options, send, operation).ConfigureAwait(false); }
        catch (ValidationException error)
        {
            if (!options.RecoverToolResultTurns || !string.Equals(error.ErrorCode, "ValidationException", StringComparison.Ordinal) ||
                error.Message.IndexOf(RejectedToolResultTurn, StringComparison.Ordinal) < 0 ||
                !TrySeparateToolResultTurns(request, out var recovered)) throw;

            SdkLogging.Warning("Bedrock model '{Model}' rejected an adjacent tool-result turn; retrying once with an acknowledgement.", request.ModelId);
            // Transform this original admitted graph; a newly learned inner thinking copy is not this outer retry's input.
            var response = await SendWithThinkingRecoveryAsync(recovered, options, send, operation).ConfigureAwait(false);
            Volatile.Write(ref _separateToolResultModelId, request.ModelId);
            return response;
        }
    }

    private static bool TrySeparateToolResultTurns(ConverseStreamRequest request, out ConverseStreamRequest recovered)
    {
        recovered = request;
        List<NativeMessage>? messages = null;
        for (var index = 0; index < request.Messages.Count; index++)
        {
            var current = request.Messages[index];
            if (index > 0 && request.Messages[index - 1] is { } previous &&
                string.Equals(previous.Role?.Value, "user", StringComparison.Ordinal) && string.Equals(current.Role?.Value, "user", StringComparison.Ordinal) &&
                previous.Content is { Count: > 0 } prior && current.Content is { Count: > 0 } content &&
                prior.All(block => block.ToolResult is not null) && content.All(block => block.ToolResult is null))
            {
                messages ??= request.Messages.Take(index).ToList();
                messages.Add(new NativeMessage { Role = ConversationRole.Assistant, Content = [new() { Text = "Tool result received." }] });
            }
            if (messages is not null) messages.Add(current);
        }
        if (messages is null) return false;
        recovered = WithThinkingFields(request, request.AdditionalModelRequestFields);
        recovered.Messages = messages;
        return true;
    }

    private ConverseStreamRequest ApplyRememberedThinking(ConverseStreamRequest request, BedrockModelOptions options)
    {
        if (!options.RecoverStaleThinkingBlocks || !string.Equals(Volatile.Read(ref _dropStaleThinkingModelId), request.ModelId, StringComparison.Ordinal))
            return request;
        return TryDropBlockFields(request.AdditionalModelRequestFields, out var fields) ? WithThinkingFields(request, fields) : request;
    }

    private async Task<T> SendWithThinkingRecoveryAsync<T>(ConverseStreamRequest request, BedrockModelOptions options,
        Func<ConverseStreamRequest, Task<T>> send, CancellationToken operation)
    {
        try { return await send(request).ConfigureAwait(false); }
        catch (ValidationException error)
        {
            if (!options.RecoverStaleThinkingBlocks || !string.Equals(error.ErrorCode, "ValidationException", StringComparison.Ordinal) ||
                error.Message.IndexOf("bound to a different conversation", StringComparison.Ordinal) < 0 ||
                !TryDropBlockFields(request.AdditionalModelRequestFields, out var fields)) throw;

            SdkLogging.Warning("Bedrock model '{Model}' rejected stale thinking; retrying once with drop_block.", request.ModelId);
            // Like the pinned providers, learning precedes retry and survives a failed retry for this exact model ID.
            Volatile.Write(ref _dropStaleThinkingModelId, request.ModelId);
            operation.ThrowIfCancellationRequested();
            return await send(WithThinkingFields(request, fields)).ConfigureAwait(false);
        }
    }

    private static bool TryDropBlockFields(Document fields, out Document recovered)
    {
        recovered = default;
        var json = JsonSerializer.SerializeToElement(fields);
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("thinking", out var thinking) ||
            thinking.ValueKind != JsonValueKind.Object || thinking.TryGetProperty("block_binding", out _)) return false;

        var copy = json.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        var thinkingCopy = thinking.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        thinkingCopy["block_binding"] = JsonSerializer.Deserialize<JsonElement>("""{"prefix_mismatch_behavior":"drop_block"}""");
        copy["thinking"] = JsonSerializer.SerializeToElement(thinkingCopy);
        var betas = json.TryGetProperty("anthropic_beta", out var configured) && configured.ValueKind == JsonValueKind.Array
            ? configured.EnumerateArray().Select(value => value.Clone()).ToList() : [];
        if (!betas.Any(value => value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), ThinkingBindingBeta, StringComparison.Ordinal)))
            betas.Add(JsonSerializer.SerializeToElement(ThinkingBindingBeta));
        copy["anthropic_beta"] = JsonSerializer.SerializeToElement(betas);
        recovered = JsonSerializer.Deserialize<Document>(JsonSerializer.SerializeToElement(copy).GetRawText());
        return true;
    }

    // Only request-local additional fields change; the already mapped graph and its owned media streams are reused.
    private static ConverseStreamRequest WithThinkingFields(ConverseStreamRequest request, Document fields) => new()
    {
        ModelId = request.ModelId,
        Messages = request.Messages,
        System = request.System,
        InferenceConfig = request.InferenceConfig,
        ToolConfig = request.ToolConfig,
        AdditionalModelRequestFields = fields,
        AdditionalModelResponseFieldPaths = request.AdditionalModelResponseFieldPaths,
        RequestMetadata = request.RequestMetadata,
        PromptVariables = request.PromptVariables,
        PerformanceConfig = request.PerformanceConfig,
        ServiceTier = request.ServiceTier,
        OutputConfig = request.OutputConfig,
        GuardrailConfig = request.GuardrailConfig,
    };
}
