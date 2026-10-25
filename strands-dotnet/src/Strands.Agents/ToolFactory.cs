using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Strands.Agents.SchemaImplementation;

namespace Strands.Agents;

/// <summary>Marks a public C# method for explicit tool discovery.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class ToolAttribute(string description) : Attribute
{
    /// <summary>The model-facing purpose of the method.</summary>
    public string Description { get; } = description;
    /// <summary>An optional registered name; otherwise the method name is used.</summary>
    public string? Name { get; set; }
}

/// <summary>Supplies model-facing metadata for one method parameter.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ToolParameterAttribute(string description) : Attribute
{
    /// <summary>The parameter's purpose.</summary>
    public string Description { get; } = description;
    /// <summary>An optional JSON parameter name; otherwise the declared name is used.</summary>
    public string? Name { get; set; }
}

/// <summary>Overrides inferred tool metadata and JSON serialization.</summary>
public sealed record ToolFactoryOptions
{
    /// <summary>An optional registered name overriding method metadata.</summary>
    public string? Name { get; init; }
    /// <summary>An optional description overriding method metadata.</summary>
    public string? Description { get; init; }
    /// <summary>Serialization and schema settings, copied when constructing the tool.</summary>
    public JsonSerializerOptions? SerializerOptions { get; init; }
}

/// <summary>Builds native tools from explicitly supplied methods. Reflection does not load or execute foreign modules.</summary>
public static class ToolFactory
{
    /// <summary>Builds all explicitly attributed public instance methods without invoking them. The supplied object remains caller-owned.</summary>
    public static IReadOnlyList<ITool> FromObject(object instance)
    {
        Guard.NotNull(instance, nameof(instance));
        return Discover(instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance), instance);
    }

    /// <summary>Builds explicitly attributed public static methods. No object is constructed and no method is invoked during discovery.</summary>
    public static IReadOnlyList<ITool> FromType(Type type)
    {
        Guard.NotNull(type, nameof(type));
        return Discover(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy), null);
    }

    /// <summary>Builds one explicitly selected method. Instance methods require their bound target; static methods require null.</summary>
    public static ITool Create(MethodInfo method, object? target, ToolFactoryOptions? options = null)
    {
        Guard.NotNull(method, nameof(method));
        if (method.IsStatic ? target is not null : target is null || !method.DeclaringType!.IsInstanceOfType(target))
            throw new ArgumentException("The target must match the selected instance method, or be null for a static method.", nameof(target));
        return Wrap(new(method, arguments => method.Invoke(target, arguments), options));
    }

    /// <summary>Derives a schema from a single callback and injects ToolContext and CancellationToken parameters.</summary>
    public static ITool Create(Delegate callback, ToolFactoryOptions? options = null)
    {
        Guard.NotNull(callback, nameof(callback));
        if (callback.GetInvocationList().Length != 1) throw new ArgumentException("A tool callback must have exactly one invocation target.", nameof(callback));
        var method = callback.Method;
        if (method.GetParameters().Length != callback.GetType().GetMethod("Invoke")!.GetParameters().Length)
            throw new ArgumentException("Open instance and closed static delegates are not supported. Supply a bound method or lambda.", nameof(callback));
        return Wrap(new(method, arguments => callback.DynamicInvoke(arguments), options));
    }

    private static ITool Wrap(MethodTool method) => method.IsStreaming ? new StreamingFunctionTool(method.Spec, method.StreamAsync) : method;

    private static IReadOnlyList<ITool> Discover(IEnumerable<MethodInfo> methods, object? target)
    {
        var tools = methods.Where(method => method.IsDefined(typeof(ToolAttribute), true)).OrderBy(method => method.Name, StringComparer.Ordinal)
            .ThenBy(method => method.MetadataToken).Select(method => Create(method, target)).ToArray();
        if (tools.GroupBy(tool => tool.Spec.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Attributed methods must declare unique registered tool names.", nameof(methods));
        return Array.AsReadOnly(tools);
    }

    private sealed class MethodTool : IRichTool
    {
        private readonly Func<object?[], object?> _invoke;
        private readonly ParameterInfo[] _parameters;
        private readonly string?[] _names;
        private readonly JsonSerializerOptions _serializer;
        private readonly JsonSchema _schema;
        private readonly Type _returnType;

        internal MethodTool(MethodInfo method, Func<object?[], object?> invoke, ToolFactoryOptions? options)
        {
            if (method.ContainsGenericParameters || method.ReturnType.IsByRef || method.ReturnType.IsPointer)
                throw new ArgumentException("Tools require a closed method with a serializable return type.", nameof(method));
            if (method.ReturnType == typeof(void) && method.IsDefined(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), false))
                throw new ArgumentException("Async void methods cannot be awaited; return Task or ValueTask.", nameof(method));
            _parameters = method.GetParameters();
            if (_parameters.Any(parameter => parameter.ParameterType.IsByRef || parameter.ParameterType.IsPointer || parameter.IsOut))
                throw new ArgumentException("Tool parameters cannot use ref, out or pointer types.", nameof(method));
            if (_parameters.Count(parameter => parameter.ParameterType == typeof(ToolContext)) > 1 || _parameters.Count(parameter => parameter.ParameterType == typeof(CancellationToken)) > 1)
                throw new ArgumentException("Only one ToolContext and one CancellationToken may be injected.", nameof(method));
            _invoke = invoke;
            _returnType = method.ReturnType;
            var resultType = UnwrapReturnType(_returnType);
            IsStreaming = typeof(IAsyncEnumerable<IToolStreamEvent>).IsAssignableFrom(resultType);
            _serializer = options?.SerializerOptions is { } supplied ? new(supplied) : new(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true, NumberHandling = JsonNumberHandling.Strict };
            _serializer.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();
            _serializer.MakeReadOnly();
            _names = new string?[_parameters.Length];
            var properties = new JsonObject();
            var required = new JsonArray();
            for (var index = 0; index < _parameters.Length; index++)
            {
                var parameter = _parameters[index];
                if (IsInjected(parameter.ParameterType)) continue;
                var metadata = parameter.GetCustomAttribute<ToolParameterAttribute>();
                var name = metadata?.Name ?? parameter.Name ?? throw new ArgumentException("A tool parameter has no name.", nameof(method));
                Guard.NotNullOrWhiteSpace(name, nameof(method));
                if (properties.ContainsKey(name)) throw new ArgumentException($"Duplicate tool parameter name '{name}'.", nameof(method));
                _names[index] = name;
                var node = _serializer.GetJsonSchemaAsNode(parameter.ParameterType, new() { TreatNullObliviousAsNonNullable = true });
                var nullable = !parameter.ParameterType.IsValueType && (AllowsNull(parameter) || parameter.HasDefaultValue && parameter.DefaultValue is null);
                RebaseReferences(node, "#/properties/" + name.Replace("~", "~0").Replace("/", "~1") + (nullable ? "/anyOf/0" : ""));
                if (nullable)
                    node = new JsonObject { ["anyOf"] = new JsonArray(node, new JsonObject { ["type"] = "null" }) };
                var schema = node as JsonObject ?? new JsonObject { ["allOf"] = new JsonArray(node) };
                if (metadata?.Description is { } description) schema["description"] = description;
                else if (parameter.GetCustomAttribute<DescriptionAttribute>() is { } described) schema["description"] = described.Description;
                if (parameter.HasDefaultValue)
                {
                    if (parameter.DefaultValue is not Missing and not DBNull)
                        schema["default"] = JsonSerializer.SerializeToNode(parameter.DefaultValue, parameter.ParameterType, _serializer);
                }
                else required.Add(name);
                properties.Add(name, schema);
            }
            var input = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
            var attribute = method.GetCustomAttribute<ToolAttribute>();
            Spec = new(options?.Name ?? attribute?.Name ?? method.Name, options?.Description ?? attribute?.Description ?? method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? method.Name,
                JsonSerializer.SerializeToElement(input));
            _schema = ToolSchema.Compile(Spec.InputSchema);
        }

        public ToolSpec Spec { get; }
        internal bool IsStreaming { get; }

        public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            var result = await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false);
            return result.Content.Length > 0 ? result.Content : string.Concat(result.ContentBlocks.OfType<JsonBlock>().Select(block => block.Json.GetRawText()));
        }

        public async ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            var result = await InvokeBoundAsync(input, context, cancellationToken).ConfigureAwait(false);
            if (result is ToolResultBlock { Status: not ToolResultStatus.Success and not ToolResultStatus.Error })
                throw new InvalidOperationException("A tool returned an invalid result status.");
            return result switch
            {
                ToolResultBlock rich => rich with { ToolUseId = context.ToolUse.ToolUseId, ContentBlocks = Array.AsReadOnly(rich.ContentBlocks.Select(Message.CloneBlock).ToArray()), StructuredContent = rich.StructuredContent?.Clone() },
                string text => new(context.ToolUse.ToolUseId, text),
                ContentBlock block => new(context.ToolUse.ToolUseId, new[] { block }),
                IReadOnlyList<ContentBlock> blocks => new(context.ToolUse.ToolUseId, blocks),
                _ => new(context.ToolUse.ToolUseId, new ContentBlock[] { new JsonBlock(JsonSerializer.SerializeToElement(result, result?.GetType() ?? typeof(object), _serializer)) })
            };
        }

        internal async IAsyncEnumerable<IToolStreamEvent> StreamAsync(JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var stream = await InvokeBoundAsync(input, context, cancellationToken).ConfigureAwait(false) as IAsyncEnumerable<IToolStreamEvent>
                ?? throw new InvalidOperationException("A streaming method returned a null stream.");
            await foreach (var item in stream.WithCancellation(cancellationToken).ConfigureAwait(false)) yield return item;
        }

        private async ValueTask<object?> InvokeBoundAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken)
        {
            Guard.NotNull(context, nameof(context));
            cancellationToken.ThrowIfCancellationRequested();
            if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1) || !_schema.Evaluate(input).IsValid)
                throw new ArgumentException("Tool input does not match the generated parameter schema.", nameof(input));
            var arguments = new object?[_parameters.Length];
            for (var index = 0; index < _parameters.Length; index++)
            {
                var parameter = _parameters[index];
                arguments[index] = parameter.ParameterType == typeof(ToolContext) ? context
                    : parameter.ParameterType == typeof(CancellationToken) ? cancellationToken
                    : input.TryGetProperty(_names[index]!, out var value) ? JsonSerializer.Deserialize(value.GetRawText(), parameter.ParameterType, _serializer)
                    : parameter.DefaultValue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            object? result;
            try { result = _invoke(arguments); }
            catch (TargetInvocationException error) when (error.InnerException is { } original)
            { ExceptionDispatchInfo.Capture(original).Throw(); throw; }
            return await AwaitResultAsync(result, _returnType).ConfigureAwait(false);
        }

        private static async ValueTask<object?> AwaitResultAsync(object? result, Type returnType)
        {
            if (result is null && typeof(Task).IsAssignableFrom(returnType))
                throw new InvalidOperationException("An asynchronous tool method returned a null task.");
            if (result is ValueTask pending) { await pending.ConfigureAwait(false); return null; }
            if (result is not null && returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
                result = result.GetType().GetMethod("AsTask")!.Invoke(result, null);
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                if (returnType == typeof(Task)) return null;
                return task.GetType().GetProperty("Result")?.GetValue(task);
            }
            return result;
        }
    }

    private static bool IsInjected(Type type) => type == typeof(ToolContext) || type == typeof(CancellationToken);

    private static Type UnwrapReturnType(Type type) => type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)) ? type.GetGenericArguments()[0] : type;

    private static bool AllowsNull(ParameterInfo parameter)
    {
        var flag = NullableFlag(parameter.CustomAttributes, "System.Runtime.CompilerServices.NullableAttribute");
        if (flag is not null) return flag == 2;
        flag = NullableFlag(parameter.Member.CustomAttributes, "System.Runtime.CompilerServices.NullableContextAttribute");
        for (var type = parameter.Member.DeclaringType; flag is null && type is not null; type = type.DeclaringType)
            flag = NullableFlag(type.CustomAttributes, "System.Runtime.CompilerServices.NullableContextAttribute");
        return flag == 2;
    }

    private static byte? NullableFlag(IEnumerable<CustomAttributeData> attributes, string name)
    {
        var attribute = attributes.FirstOrDefault(value => value.AttributeType.FullName == name);
        if (attribute?.ConstructorArguments.Count != 1) return null;
        var argument = attribute.ConstructorArguments[0];
        if (argument.Value is byte flag) return flag;
        return argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> flags ? flags.FirstOrDefault().Value as byte? : null;
    }

    private static void RebaseReferences(JsonNode? node, string prefix)
    {
        if (node is JsonObject map)
        {
            if (map["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var text) && text.StartsWith("#", StringComparison.Ordinal))
                map["$ref"] = prefix + text.Substring(1);
            foreach (var child in map.ToArray()) RebaseReferences(child.Value, prefix);
        }
        else if (node is JsonArray array) foreach (var child in array) RebaseReferences(child, prefix);
    }
}
