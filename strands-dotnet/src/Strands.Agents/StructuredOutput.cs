using System.Text.Json;
using Strands.Agents.SchemaImplementation;

namespace Strands.Agents;

/// <summary>The source profile requested for structured response forcing and accepted history.</summary>
public enum StructuredOutputBehavior
{
    /// <summary>Preserve the native structured response behavior.</summary>
    Native,
    /// <summary>Use the TypeScript structured response profile.</summary>
    TypeScript,
    /// <summary>Use the Python structured response profile.</summary>
    Python
}

/// <summary>Per-invocation structured response validation and deserialization settings.</summary>
/// <typeparam name="T">The validated output type.</typeparam>
public sealed record StructuredOutputOptions<T>
{
    /// <summary>The structured response profile; omission preserves native behavior.</summary>
    public StructuredOutputBehavior Behavior { get; init; } = StructuredOutputBehavior.Native;
    /// <summary>The JSON-object schema describing the response tool's arguments.</summary>
    public required JsonElement Schema { get; init; }
    /// <summary>An optional model-facing description of the desired response.</summary>
    public string? Description { get; init; }
    /// <summary>Typed deserialization settings; null uses System.Text.Json web defaults.</summary>
    public JsonSerializerOptions? SerializerOptions { get; init; }
    /// <summary>Optional custom deserialization/validation; failures are returned to the model as validation feedback.</summary>
    public Func<JsonElement, T>? Deserializer { get; init; }
    /// <summary>The prompt used after a non-tool response. Python treats null/empty as the default; TypeScript does not use this prompt.</summary>
    public string ForcePrompt { get; init; } = "You must format the previous response as structured output.";
    /// <summary>An optional positive validation-attempt bound; defaults to Int32.MaxValue, with ordinary invocation limits governing retries.</summary>
    public int MaxValidationAttempts { get; init; } = int.MaxValue;
}

/// <summary>The typed response and ordinary invocation accounting that produced it.</summary>
/// <typeparam name="T">The validated output type.</typeparam>
/// <param name="Output">The accepted typed response. TypeScript final hook JSON is decoded after model-argument validation.</param>
/// <param name="Result">The ordinary agent result, retaining usage and caller-owned state.</param>
public sealed record StructuredOutputResult<T>(T Output, AgentResult Result);

/// <summary>A missing or invalid structured response, with original deserialization errors retained.</summary>
public sealed class StructuredOutputException : Exception
{
    /// <summary>The completed native invocation result when a typed response was preempted, or null when no result exists. This is live invocation data, not a safe serialization envelope.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AgentResult? Result { get; init; }
    /// <summary>Constructs a validation or structured-response protocol failure.</summary>
    public StructuredOutputException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>A temporary response tool whose validated value terminates a structured invocation.</summary>
public interface IStructuredOutputTool : ITool
{
    /// <summary>Whether a schema-valid value was successfully deserialized.</summary>
    bool HasResult { get; }
    /// <summary>The latest successful typed value, or null before validation.</summary>
    object? Result { get; }
    /// <summary>Retrieves a successful output by tool-use ID for deterministic batch extraction.</summary>
    bool TryGetResult(string toolUseId, out object? result);
}

internal interface IStructuredOutputAttempt
{
    void DiscardResult(string toolUseId);
    void AcceptFinalJson(string toolUseId, JsonElement json, CancellationToken cancellationToken);
}

/// <summary>Validates a model-issued response tool call and retains the typed value for the invocation.</summary>
/// <typeparam name="T">The expected response type.</typeparam>
public sealed class StructuredOutputTool<T> : IStructuredOutputTool, IRichTool, IStructuredOutputAttempt
{
    /// <summary>The reserved model-facing structured response tool name.</summary>
    public const string ToolName = "strands_structured_output";
    private const string DefaultForcePrompt = "You must format the previous response as structured output.";
    private const string ValidatedMessage = "Successfully validated structured output.";
    private readonly JsonSchema _schema;
    private readonly StructuredOutputOptions<T> _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly object _gate = new();
    private readonly Dictionary<string, T> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _resultJson = new(StringComparer.Ordinal);
    private readonly List<string> _resultOrder = [];
    private T? _output;
    private bool _hasResult;
    private int _attempts;

    /// <summary>Creates a fresh tool for one invocation and compiles its supplied JSON schema.</summary>
    public StructuredOutputTool(StructuredOutputOptions<T> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.MaxValidationAttempts < 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (!Enum.IsDefined(typeof(StructuredOutputBehavior), options.Behavior)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Behavior == StructuredOutputBehavior.Native) Guard.NotNullOrWhiteSpace(options.ForcePrompt, nameof(options.ForcePrompt));
        ForcePrompt = string.IsNullOrEmpty(options.ForcePrompt) ? DefaultForcePrompt : options.ForcePrompt;
        var instruction = "This tool MUST only be invoked as the last and final tool before returning the completed result to the caller.";
        Spec = new(ToolName, options.Description is null ? instruction : instruction + "\n<description>" + options.Description + "</description>", options.Schema);
        _schema = ToolSchema.Compile(Spec.InputSchema);
        _serializerOptions = options.SerializerOptions is null ? new(JsonSerializerDefaults.Web) : new(options.SerializerOptions);
    }
    /// <inheritdoc/>
    public ToolSpec Spec { get; }
    /// <inheritdoc/>
    public bool HasResult { get { lock (_gate) return _hasResult; } }
    /// <inheritdoc/>
    public object? Result { get { lock (_gate) return _hasResult ? _output : null; } }
    /// <summary>The latest accepted typed value; inspect HasResult to distinguish absence from a default value.</summary>
    public T? Output { get { lock (_gate) return _output; } }
    /// <summary>The number of validation calls attempted, including invalid responses.</summary>
    public int ValidationAttempts { get { lock (_gate) return _attempts; } }
    /// <summary>The configured maximum validation attempts.</summary>
    public int MaxValidationAttempts => _options.MaxValidationAttempts;

    internal string ForcePrompt { get; }

    /// <summary>Retrieves a successful result by provider-issued tool-use ID for deterministic batch extraction.</summary>
    public bool TryGetResult(string toolUseId, out T? output)
    {
        Guard.NotNull(toolUseId, nameof(toolUseId));
        lock (_gate)
        {
            if (_results.TryGetValue(toolUseId, out var result)) { output = result; return true; }
            output = default;
            return false;
        }
    }
    bool IStructuredOutputTool.TryGetResult(string toolUseId, out object? result)
    {
        var found = TryGetResult(toolUseId, out var output);
        result = found ? output : null;
        return found;
    }
    void IStructuredOutputAttempt.DiscardResult(string toolUseId)
    {
        lock (_gate)
        {
            _results.Remove(toolUseId);
            _resultJson.Remove(toolUseId);
            _resultOrder.Remove(toolUseId);
            _hasResult = _resultOrder.Count > 0;
            _output = _hasResult ? _results[_resultOrder[_resultOrder.Count - 1]] : default;
        }
    }

    void IStructuredOutputAttempt.AcceptFinalJson(string toolUseId, JsonElement json, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JsonElement? emitted = null;
        lock (_gate) if (_resultJson.TryGetValue(toolUseId, out var original)) emitted = original;
        // Unmodified rich output already has its converted typed value; do not run converters twice.
        if (emitted is { } previous && string.Equals(previous.GetRawText(), json.GetRawText(), StringComparison.Ordinal)) return;
        T output;
        try
        {
            // Hooks run after model validation. Their final JSON is authoritative in the TypeScript profile.
            // Decode changed JSON to the native requested type without rerunning schema or custom validation.
            output = typeof(T) == typeof(JsonElement) ? (T)(object)json.Clone() : json.Deserialize<T>(_serializerOptions)!;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new StructuredOutputException("The accepted hook JSON cannot be converted to the requested structured output type.", error); }
        CommitOutput(output, toolUseId, cancellationToken, json);
    }

    /// <summary>Validates the schema and deserialization before storing a response; invalid calls produce retry feedback.</summary>
    /// <exception cref="StructuredOutputException">The schema, typed conversion, or attempt budget rejects the response.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled before committing the result.</exception>
    public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var output = ValidateOutput(input, context, cancellationToken);
        CommitOutput(output, context.ToolUse.ToolUseId, cancellationToken);
        return new(ValidatedMessage);
    }

    /// <summary>Returns detached validated JSON for TypeScript, or source-profile validation text for the other behaviors.</summary>
    public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var output = ValidateOutput(input, context, cancellationToken);
            ToolResultBlock result;
            JsonElement? emittedJson = null;
            if (_options.Behavior == StructuredOutputBehavior.TypeScript)
            {
                JsonElement json;
                try { json = JsonSerializer.SerializeToElement(output, _serializerOptions); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) { throw new StructuredOutputException("Structured output serialization failed. Correct the response and invoke the tool again. " + error.Message, error); }
                result = new(context.ToolUse.ToolUseId, [new JsonBlock(json)]);
                emittedJson = json;
            }
            else if (_options.Behavior == StructuredOutputBehavior.Python)
                result = new(context.ToolUse.ToolUseId, [new TextBlock($"Successfully validated {Spec.Name} structured output")]);
            else result = new(context.ToolUse.ToolUseId, ValidatedMessage);
            CommitOutput(output, context.ToolUse.ToolUseId, cancellationToken, emittedJson);
            return new(result);
        }
        catch (StructuredOutputException error)
        {
            return new(new ToolResultBlock(context.ToolUse.ToolUseId, error.Message, ToolResultStatus.Error) { Error = error });
        }
    }

    private T ValidateOutput(JsonElement input, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard.NotNull(context, nameof(context));
        lock (_gate)
        {
            if (_attempts >= _options.MaxValidationAttempts) throw new StructuredOutputException("The structured output validation attempt limit was reached.");
            _attempts++;
        }
        return ParseOutput(input, cancellationToken);
    }

    private void CommitOutput(T output, string toolUseId, CancellationToken cancellationToken, JsonElement? emittedJson = null)
    {
        var detached = emittedJson?.Clone();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _results[toolUseId] = output;
            if (detached is { } json) _resultJson[toolUseId] = json;
            else _resultJson.Remove(toolUseId);
            _resultOrder.Remove(toolUseId);
            _resultOrder.Add(toolUseId);
            _output = output;
            _hasResult = true;
        }
    }

    internal T ParseOutput(JsonElement input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var evaluation = _schema.Evaluate(input, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!evaluation.IsValid)
        {
            var errors = evaluation.Details?.Where(detail => !detail.IsValid && detail.Errors is not null)
                .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}")) ?? [];
            throw new StructuredOutputException("Structured output validation failed. Correct the response and invoke the tool again. " + string.Join(" ", errors));
        }
        T output;
        try
        {
            output = _options.Deserializer is { } deserialize ? deserialize(input.Clone()) : input.Deserialize<T>(_serializerOptions)!;
            if (output is null && _options.Behavior != StructuredOutputBehavior.TypeScript) throw new JsonException("The structured response cannot deserialize to null.");
            if (_options.Behavior == StructuredOutputBehavior.TypeScript && output is JsonElement json) output = (T)(object)json.Clone();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new StructuredOutputException("Structured output deserialization failed. Correct the response and invoke the tool again. " + error.Message, error); }
        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }
}
