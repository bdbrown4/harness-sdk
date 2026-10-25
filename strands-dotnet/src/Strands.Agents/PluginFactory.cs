using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Strands.Agents;

/// <summary>Adapts explicitly attributed methods on a borrowed object to a plugin.</summary>
public static class PluginFactory
{
    /// <summary>Creates a named plugin whose initializer runs before its discovered hooks and tools are registered.</summary>
    /// <param name="instance">The caller-owned object containing public instance methods marked with Hook or Tool attributes.</param>
    /// <param name="name">The plugin's unique identifier within an agent.</param>
    /// <param name="initialize">Optional initialization for each agent binding. Its own side effects remain caller-controlled.</param>
    /// <returns>An immutable declaration that can be shared by agents without transferring ownership of the target.</returns>
    /// <remarks>Discovery uses parent metadata declaration order and resolves each name to its most-derived public method. Ambiguous overload names are rejected. An unmarked override suppresses an inherited declaration.</remarks>
    public static IPlugin FromObject(object instance, string name, Func<Agent, CancellationToken, ValueTask>? initialize = null)
    {
        Guard.NotNull(instance, nameof(instance));
        Guard.NotNullOrWhiteSpace(name, nameof(name));
        var hooks = new List<AttributedHook>();
        var tools = new List<ITool>();
        foreach (var method in Discover(instance.GetType()))
        {
            var hook = method.GetCustomAttribute<HookAttribute>(inherit: false);
            var tool = method.GetCustomAttribute<ToolAttribute>(inherit: false);
            if (hook is not null && tool is not null)
                throw new ArgumentException("A plugin method cannot be both a hook and a tool.", nameof(instance));
            if (hook is not null) hooks.Add(CreateHook(method, instance, hook.Order));
            if (tool is not null) tools.Add(ToolFactory.Create(method, instance));
        }
        if (tools.GroupBy(tool => tool.Spec.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Attributed plugin tools must have unique registered names.", nameof(instance));
        return new ObjectPlugin(name, hooks.ToArray(), Array.AsReadOnly(tools.ToArray()), initialize);
    }

    internal static bool IsAttributed(IPlugin plugin) => plugin is ObjectPlugin;

    private static IEnumerable<MethodInfo> Discover(Type type)
    {
        var hierarchy = new Stack<Type>();
        for (Type? current = type; current is not null; current = current.BaseType) hierarchy.Push(current);
        var orderedNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (hierarchy.Count > 0)
        {
            foreach (var method in hierarchy.Pop().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(method => method.MetadataToken))
            {
                if (seen.Add(method.Name))
                    orderedNames.Add(method.Name);
            }
        }
        var visible = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        foreach (var name in orderedNames)
        {
            var methods = visible.Where(method => method.Name == name).ToArray();
            if (!methods.Any(method => method.IsDefined(typeof(HookAttribute), false) || method.IsDefined(typeof(ToolAttribute), false))) continue;
            if (methods.Length != 1)
                throw new ArgumentException($"Attributed plugin method '{name}' must have one unambiguous public instance declaration.", nameof(type));
            yield return methods[0];
        }
    }

    private static AttributedHook CreateHook(MethodInfo method, object target, int order)
    {
        var parameters = method.GetParameters();
        if (method.ContainsGenericParameters || parameters.Length is < 1 or > 2 ||
            parameters.Any(parameter => parameter.ParameterType.IsByRef || parameter.IsOut) ||
            !typeof(IAgentStreamEvent).IsAssignableFrom(parameters[0].ParameterType) ||
            (!parameters[0].ParameterType.IsClass && !parameters[0].ParameterType.IsInterface) ||
            (parameters.Length == 2 && parameters[1].ParameterType != typeof(CancellationToken)) ||
            (method.ReturnType != typeof(void) && method.ReturnType != typeof(Task) && method.ReturnType != typeof(ValueTask)) ||
            (method.ReturnType == typeof(void) && method.IsDefined(typeof(AsyncStateMachineAttribute), false)))
            throw new ArgumentException($"Hook '{method.Name}' requires one typed event, an optional CancellationToken, and a void, Task or ValueTask return; async void is unsupported.", nameof(method));

        return new(parameters[0].ParameterType, async (value, cancellationToken) =>
        {
            object? returned;
            try
            {
                returned = method.Invoke(target, parameters.Length == 1 ? [value] : [value, cancellationToken]);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
            if (method.ReturnType == typeof(Task))
                await ((Task?)returned ?? throw new InvalidOperationException($"Hook '{method.Name}' returned a null Task.")).ConfigureAwait(false);
            else if (method.ReturnType == typeof(ValueTask))
                await ((ValueTask)returned!).ConfigureAwait(false);
        }, order);
    }

    private sealed class ObjectPlugin(string name, AttributedHook[] hooks, IReadOnlyList<ITool> tools,
        Func<Agent, CancellationToken, ValueTask>? initialize) : IPlugin
    {
        public string Name => name;
        public async ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (initialize is not null) await initialize(agent, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            agent.RegisterAttributedPluginHooks(hooks);
        }
        public IReadOnlyList<ITool> GetTools() => tools;
    }
}

internal sealed record AttributedHook(Type EventType, Func<IAgentStreamEvent, CancellationToken, ValueTask> Callback, int Order);
