using System.Globalization;
using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Plugins;

/// <summary>One item in the agent's structured task list.</summary>
/// <param name="Content">The imperative task description.</param>
/// <param name="ActiveForm">The present-continuous description displayed while work is in progress.</param>
/// <param name="Status">One of pending, in_progress, or completed.</param>
public sealed record TodoItem(string Content, string ActiveForm, string Status);

/// <summary>Configuration for the Todos feature.</summary>
public sealed record TodosConfig
{
    /// <summary>The plugin name used for duplicate detection.</summary>
    public string Name { get; init; } = "strands:todos";
    /// <summary>The durable per-agent state key containing the task list.</summary>
    public string StateKey { get; init; } = "todos";
}

/// <summary>Provides todo_write and re-surfaces the current task list before every model call without changing durable messages.</summary>
public sealed class Todos : IPlugin
{
    private readonly string _stateKey;

    /// <summary>Creates a task-list feature whose state belongs to each agent independently.</summary>
    public Todos(TodosConfig? config = null)
    {
        config ??= new();
        Guard.NotBlank(config.Name, nameof(config.Name));
        Guard.NotBlank(config.StateKey, nameof(config.StateKey));
        Name = config.Name;
        _stateKey = config.StateKey;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IReadOnlyList<ITool> GetTools()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"todos":{"type":"array","items":{"type":"object","properties":{"content":{"type":"string"},"activeForm":{"type":"string"},"status":{"type":"string","enum":["pending","in_progress","completed"]}},"required":["content","activeForm","status"],"additionalProperties":false}}},"required":["todos"],"additionalProperties":false}""");
        return new ITool[]
        {
            new FunctionTool(new ToolSpec("todo_write", "Create and maintain a structured task list for the current session. Use proactively for multi-step work (roughly 3+ distinct steps). Keep exactly one item in_progress at a time, and update status as you go rather than batching. Pass an empty list to clear it when the work is done.", schema.RootElement),
                (input, context, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var list = input.GetProperty("todos");
                    var items = ReadItems(list);
                    if (items.Count == 0)
                    {
                        context.Agent.State.Delete(_stateKey);
                        return new ValueTask<string>("Todo list cleared");
                    }
                    context.Agent.State.Set(_stateKey, list);
                    var remaining = items.Count(item => item.Status != "completed");
                    return new ValueTask<string>(remaining.ToString(CultureInfo.InvariantCulture) + " todos remaining\n" + RenderList(items));
                }),
        };
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent));
        cancellationToken.ThrowIfCancellationRequested();
        agent.Hooks.Register<BeforeModelCallEvent>((value, token) =>
        {
            if (!ReferenceEquals(value.Agent, agent)) return default;
            token.ThrowIfCancellationRequested();
            try
            {
                var stored = agent.State.Get<JsonElement>(_stateKey);
                if (stored.ValueKind != JsonValueKind.Array || stored.GetArrayLength() == 0) return default;
                var reminder = "<system-reminder>\nYour current todo list:\n" + RenderList(ReadItems(stored))
                    + "\nKeep it up to date with todo_write as you work.\n</system-reminder>";
                value.Request = ContextInjection.Fold(value.Request, reminder);
            }
            catch (Exception error) when (!(error is OperationCanceledException && token.IsCancellationRequested))
            {
                HarnessLogging.Warning("plugin=<{Plugin}> | todo context could not be rendered; skipping injection", Name);
            }
            return default;
        });
        return default;
    }

    private static IReadOnlyList<TodoItem> ReadItems(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) throw new ArgumentException("todos must be an array.", nameof(list));
        var items = new List<TodoItem>();
        foreach (var entry in list.EnumerateArray())
        {
            var content = entry.GetProperty("content").GetString() ?? throw new ArgumentException("content must be a string.", nameof(list));
            var activeForm = entry.GetProperty("activeForm").GetString() ?? throw new ArgumentException("activeForm must be a string.", nameof(list));
            var status = entry.GetProperty("status").GetString();
            if (status is not ("pending" or "in_progress" or "completed")) throw new ArgumentException("Invalid todo status.", nameof(list));
            items.Add(new(content, activeForm, status));
        }
        return items;
    }

    private static string RenderList(IEnumerable<TodoItem> items) => string.Join("\n", items.Select(item => "  [" + item.Status + "] "
        + (item.Status == "in_progress" && item.ActiveForm.Length > 0 ? item.ActiveForm : item.Content)));
}
