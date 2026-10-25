using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace Strands.Agents.Bedrock;

public sealed partial class BedrockModel
{
    private readonly ConcurrentDictionary<string, byte> _unsupportedTokenCounts = new(StringComparer.Ordinal);

    /// <summary>Counts native Converse input when enabled, otherwise estimates locally. Fallback does not suppress caller cancellation, disposal or the configured deadline.</summary>
    public async ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        BedrockModelOptions options;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BedrockModel));
            options = Volatile.Read(ref _options);
        }
        if (!options.UseNativeTokenCount || options.TokenCountFailurePolicy == NativeTokenCountFailurePolicy.Fallback && _unsupportedTokenCounts.ContainsKey(options.ModelId))
            return ModelExtensions.EstimateInputTokens(request, cancellationToken);

        using var mapper = new BedrockRequestMapper();
        using var deadline = new CancellationTokenSource();
        if (options.RequestTimeout is { } timeout) deadline.CancelAfter(timeout);
        using var operation = CreateRequestCancellation(cancellationToken, deadline.Token);
        try
        {
            var formatted = ApplyRememberedThinking(ApplyRememberedToolResultTurns(mapper.Map(request, options), options), options);
            var native = new CountTokensRequest
            {
                ModelId = options.ModelId,
                Input = new CountTokensInput
                {
                    Converse = new ConverseTokensRequest
                    {
                        Messages = formatted.Messages,
                        System = formatted.System,
                        ToolConfig = formatted.ToolConfig,
                        AdditionalModelRequestFields = formatted.AdditionalModelRequestFields
                    }
                }
            };
            operation.Token.ThrowIfCancellationRequested();
            IAmazonBedrockRuntime client;
            lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(BedrockModel)); client = _client.Value; }
            var response = await client.CountTokensAsync(native, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (response?.InputTokens is not { } count || count < 0) throw new ModelException("Bedrock CountTokens returned no nonnegative input-token count.");
            return count;
        }
        catch (Exception error)
        {
            var translated = Translate(error, cancellationToken, operation.Token, deadline.Token);
            if (operation.IsCancellationRequested || options.TokenCountFailurePolicy == NativeTokenCountFailurePolicy.Throw)
            { ExceptionDispatchInfo.Capture(translated).Throw(); throw; }
            if (error is AccessDeniedException || error is ValidationException && error.Message.IndexOf("doesn't support counting tokens", StringComparison.OrdinalIgnoreCase) >= 0)
                _unsupportedTokenCounts.TryAdd(options.ModelId, 0);
            try
            {
                var observerFailure = SdkLogging.WarningFailure(error, "Bedrock native token counting failed for model {Model}; using the local estimate: {Error}", options.ModelId, error.Message);
                if (observerFailure is not null) ExceptionDispatchInfo.Capture(observerFailure).Throw();
            }
            catch (Exception diagnostic)
            {
                const string key = "Strands.DiagnosticErrors";
                if (error.Data[key] is not List<Exception> errors) error.Data[key] = errors = [];
                errors.Add(diagnostic);
            }
            if (operation.IsCancellationRequested) throw Translate(new OperationCanceledException(operation.Token), cancellationToken, operation.Token, deadline.Token);
            return ModelExtensions.EstimateInputTokens(request, cancellationToken);
        }
    }
}
