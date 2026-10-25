using System.Runtime.ExceptionServices;
using System.Text.Json;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.EventStreams;

namespace Strands.Agents.Bedrock;

public sealed partial class BedrockModel
{
    private async IAsyncEnumerable<ModelStreamEvent> StreamConverseAsync(ConverseStreamRequest request, BedrockModelOptions options, CancellationToken caller,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken operation, CancellationToken deadline)
    {
        ConverseResponse response;
        try
        {
            operation.ThrowIfCancellationRequested();
            Amazon.BedrockRuntime.IAmazonBedrockRuntime client;
            lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(BedrockModel)); client = _client.Value; }
            response = await SendWithRecoveryAsync(request, options, input => client.ConverseAsync(new ConverseRequest
            {
                ModelId = input.ModelId,
                Messages = input.Messages,
                System = input.System,
                InferenceConfig = input.InferenceConfig,
                ToolConfig = input.ToolConfig,
                AdditionalModelRequestFields = input.AdditionalModelRequestFields,
                AdditionalModelResponseFieldPaths = input.AdditionalModelResponseFieldPaths,
                RequestMetadata = input.RequestMetadata,
                PromptVariables = input.PromptVariables,
                PerformanceConfig = input.PerformanceConfig,
                ServiceTier = input.ServiceTier,
                OutputConfig = input.OutputConfig,
                GuardrailConfig = input.GuardrailConfig is { } guardrail ? new GuardrailConfiguration { GuardrailIdentifier = guardrail.GuardrailIdentifier, GuardrailVersion = guardrail.GuardrailVersion, Trace = guardrail.Trace } : null
            }, operation), operation).ConfigureAwait(false) ?? throw new ModelException("Bedrock Converse returned no response.");
        }
        catch (Exception error) { throw Translate(error, caller, operation, deadline); }
        using var scope = new ConverseResponseScope(response, error => Translate(error, caller, operation, deadline));
        using var parser = new BedrockStreamParser(options);
        using var iterator = scope.Events().GetEnumerator();
        while (true)
        {
            IReadOnlyList<ModelStreamEvent> events;
            bool moved;
            try
            {
                operation.ThrowIfCancellationRequested();
                moved = iterator.MoveNext();
                events = moved ? parser.Map(iterator.Current) : parser.Complete();
            }
            catch (Exception error) { scope.PrimaryError = Translate(error, caller, operation, deadline); throw scope.PrimaryError; }
            foreach (var item in events) yield return item;
            if (!moved) break;
        }
    }

    private sealed class ConverseResponseScope(ConverseResponse response, Func<Exception, Exception> translate) : IDisposable
    {
        internal Exception? PrimaryError { get; set; }
        internal IEnumerable<IEventStreamEvent> Events()
        {
            var message = response.Output?.Message ?? throw new ModelException("Bedrock Converse returned no output message.");
            yield return new MessageStartEvent { Role = message.Role };
            var content = message.Content ?? throw new ModelException("Bedrock Converse omitted its message content.");
            for (var index = 0; index < content.Count; index++)
            {
                var block = content[index] ?? throw new ModelException("Bedrock Converse returned null content.");
                var members = (block.Text is not null ? 1 : 0) + (block.ToolUse is not null ? 1 : 0) + (block.ReasoningContent is not null ? 1 : 0)
                    + (block.CitationsContent is not null ? 1 : 0) + (block.Image is not null ? 1 : 0) + (block.Audio is not null ? 1 : 0)
                    + (block.Video is not null ? 1 : 0) + (block.Document is not null ? 1 : 0) + (block.ToolResult is not null ? 1 : 0)
                    + (block.GuardContent is not null ? 1 : 0) + (block.CachePoint is not null ? 1 : 0);
                if (members != 1) throw new ModelException("Bedrock Converse content must contain exactly one content variant.");
                if (block.Text is { } text) yield return Delta(index, new() { Text = text });
                else if (block.ToolUse is { } tool)
                {
                    yield return new ContentBlockStartEvent { ContentBlockIndex = index, Start = new() { ToolUse = new() { ToolUseId = tool.ToolUseId, Name = tool.Name } } };
                    yield return Delta(index, new() { ToolUse = new() { Input = JsonSerializer.Serialize(tool.Input) } });
                }
                else if (block.ReasoningContent is { } reasoning)
                {
                    if ((reasoning.ReasoningText is not null ? 1 : 0) + (reasoning.RedactedContent is not null ? 1 : 0) != 1) throw new ModelException("Bedrock Converse returned ambiguous reasoning content.");
                    yield return Delta(index, new() { ReasoningContent = new() { Text = reasoning.ReasoningText?.Text, Signature = reasoning.ReasoningText?.Signature, RedactedContent = reasoning.RedactedContent } });
                }
                else if (block.Image is { } image)
                {
                    if (image.Source?.Bytes is null || image.Source.S3Location is not null) throw new ModelException("Bedrock Converse output images require an inline byte source.");
                    yield return new ContentBlockStartEvent { ContentBlockIndex = index, Start = new() { Image = new() { Format = image.Format } } };
                    yield return Delta(index, new() { Image = new() { Source = new() { Bytes = image.Source.Bytes } } });
                }
                else if (block.CitationsContent is { } citations)
                {
                    yield return Delta(index, new() { Text = string.Concat(citations.Content?.Select(item => item.Text) ?? []) });
                    foreach (var citation in citations.Citations ?? [])
                        yield return Delta(index, new() { Citation = new() { Location = citation.Location, Source = citation.Source, SourceContent = citation.SourceContent?.Select(item => new CitationSourceContentDelta { Text = item.Text }).ToList(), Title = citation.Title } });
                }
                else throw new ModelException("Bedrock Converse returned an unsupported output content variant.");
                yield return new ContentBlockStopEvent { ContentBlockIndex = index };
            }
            yield return new MessageStopEvent { StopReason = response.StopReason, AdditionalModelResponseFields = response.AdditionalModelResponseFields };
            if (response.Usage is not null || response.Trace is not null) yield return new ConverseStreamMetadataEvent { Usage = response.Usage ?? new TokenUsage(), Trace = response.Trace is null ? null : new ConverseStreamTrace { Guardrail = response.Trace.Guardrail, PromptRouter = response.Trace.PromptRouter } };
        }

        private static ContentBlockDeltaEvent Delta(int index, ContentBlockDelta delta) => new() { ContentBlockIndex = index, Delta = delta };

        public void Dispose()
        {
            Exception? failure = null;
            var closed = new List<MemoryStream>();
            foreach (var block in response.Output?.Message?.Content ?? [])
            {
                if (block is null) continue;
                Close(block.ReasoningContent?.RedactedContent);
                Close(block.Image?.Source?.Bytes);
                Close(block.Audio?.Source?.Bytes);
                Close(block.Video?.Source?.Bytes);
                Close(block.Document?.Source?.Bytes);
                Close(block.GuardContent?.Image?.Source?.Bytes);
                foreach (var result in block.ToolResult?.Content ?? []) { Close(result?.Image?.Source?.Bytes); Close(result?.Document?.Source?.Bytes); Close(result?.Video?.Source?.Bytes); }
            }
            if (failure is not null && PrimaryError is null) ExceptionDispatchInfo.Capture(translate(failure)).Throw();
            void Close(MemoryStream? stream)
            {
                if (stream is null || closed.Any(previous => ReferenceEquals(previous, stream))) return;
                closed.Add(stream);
                try { stream.Dispose(); }
                catch (Exception error)
                {
                    var primary = PrimaryError ?? failure;
                    if (primary is null) failure = error;
                    else
                    {
                        if (primary.Data["Strands.CleanupErrors"] is not List<Exception> errors) primary.Data["Strands.CleanupErrors"] = errors = [];
                        errors.Add(error);
                    }
                }
            }
        }
    }
}
