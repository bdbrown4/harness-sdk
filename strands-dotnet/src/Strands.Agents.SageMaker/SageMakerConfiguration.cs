using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.SageMaker;

public sealed partial class SageMakerAIModel
{
    /// <inheritdoc/>
    public ModelMetadata Metadata
    {
        get
        {
            var options = Volatile.Read(ref _options);
            long? maximum = options.Payload.AdditionalArguments is { } extra && extra.TryGetProperty("max_tokens", out var value) ? value.GetInt64() : options.Payload.MaxTokens;
            return new(options.EndpointName, options.ContextWindowLimit, maximum);
        }
    }
    /// <inheritdoc/>
    public SageMakerModelOptions GetConfig() => Snapshot(Volatile.Read(ref _options));
    /// <summary>Atomically replaces validated endpoint and payload options. The original AWS client's region and lifetime remain unchanged.</summary>
    public void UpdateConfig(Func<SageMakerModelOptions, SageMakerModelOptions> update)
    {
        if (update is null) throw new ArgumentNullException(nameof(update));
        lock (_gate) if (_disposed) throw new ObjectDisposedException(nameof(SageMakerAIModel));
        var next = Snapshot(update(GetConfig()));
        if (next.Region != _region) throw new ArgumentException("Region is a construction-time transport setting.", nameof(update));
        lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(SageMakerAIModel)); Volatile.Write(ref _options, next); }
    }
    private static SageMakerModelOptions Snapshot(SageMakerModelOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        Guard.NotNullOrWhiteSpace(options.EndpointName, nameof(options.EndpointName));
        var payload = options.Payload ?? throw new ArgumentNullException(nameof(options.Payload));
        if (options.MaximumResponseBytes <= 0 || options.ContextWindowLimit is <= 0 || payload.MaxTokens is <= 0 || payload.TopK is <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        ProviderConfiguration.Range(payload.Temperature, 0, double.MaxValue, nameof(payload.Temperature));
        ProviderConfiguration.Range(payload.TopP, 0, 1, nameof(payload.TopP));
        if (!Enum.IsDefined(typeof(SageMakerMalformedEventPolicy), options.MalformedEventPolicy)) throw new ArgumentOutOfRangeException(nameof(options));
        if (payload.Stop?.Any(value => value is null) == true) throw new ArgumentException("Stop sequences cannot contain null.", nameof(options));
        var region = options.Region ?? Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-west-2";
        Guard.NotNullOrWhiteSpace(region, nameof(options.Region));
        var copied = options with { Region = region, AdditionalArguments = ProviderConfiguration.Snapshot(options.AdditionalArguments), Payload = payload with { AdditionalArguments = ProviderConfiguration.Snapshot(payload.AdditionalArguments), Stop = payload.Stop is null ? null : Array.AsReadOnly(payload.Stop.ToArray()) } };
        _ = ProviderConfiguration.Parameters(copied.AdditionalArguments);
        var extra = ProviderConfiguration.Parameters(copied.Payload.AdditionalArguments);
        if (extra.TryGetValue("stream", out var stream) && stream is JsonElement { ValueKind: not (JsonValueKind.True or JsonValueKind.False) }) throw new ArgumentException("stream must be a boolean.", nameof(options));
        if (extra.TryGetValue("max_tokens", out var maximum) && maximum is JsonElement value && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number <= 0)) throw new ArgumentException("max_tokens must be a positive integer.", nameof(options));
        _ = new SageMakerInvocation(copied, new() { ["stream"] = extra.TryGetValue("stream", out var requested) ? requested : copied.Payload.Stream });
        return copied;
    }
}
