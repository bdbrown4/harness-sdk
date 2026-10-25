using System.Collections.ObjectModel;

namespace Strands.Agents.Telemetry;

/// <summary>An in-memory execution trace, collected even when no tracing exporter is configured.</summary>
public sealed class AgentTrace
{
    private readonly object _gate = new();
    private readonly List<AgentTrace> _children = [];
    private readonly Dictionary<string, string> _metadata = new(StringComparer.Ordinal);
    private long? _endTime;
    private Message? _message;

    /// <summary>Creates an execution trace and optionally attaches it to a parent.</summary>
    /// <param name="name">The operation's display name.</param>
    /// <param name="parent">The parent execution trace, when nested.</param>
    /// <param name="startTime">Unix epoch milliseconds, or null to use the current time.</param>
    public AgentTrace(string name, AgentTrace? parent = null, long? startTime = null)
    {
        Guard.NotNullOrWhiteSpace(name, nameof(name));
        Name = name;
        ParentId = parent?.Id;
        StartTime = startTime ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (parent is not null)
        {
            lock (parent._gate) parent._children.Add(this);
        }
    }

    /// <summary>A unique identifier independent of an exported span identifier.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("D");
    /// <summary>The operation's display name.</summary>
    public string Name { get; }
    /// <summary>The parent execution trace identifier, or null for a root cycle.</summary>
    public string? ParentId { get; }
    /// <summary>Start time in Unix epoch milliseconds.</summary>
    public long StartTime { get; }
    /// <summary>End time in Unix epoch milliseconds, or null while the operation is running.</summary>
    public long? EndTime { get { lock (_gate) return _endTime; } }
    /// <summary>Elapsed milliseconds; zero while the operation is running.</summary>
    public long Duration { get { lock (_gate) return _endTime is { } end ? Math.Max(0, end - StartTime) : 0; } }
    /// <summary>A detached, read-only list of child trace references.</summary>
    public IReadOnlyList<AgentTrace> Children { get { lock (_gate) return Array.AsReadOnly(_children.ToArray()); } }
    /// <summary>A detached, read-only snapshot of operation metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get { lock (_gate) return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(_metadata, StringComparer.Ordinal)); } }
    /// <summary>The immutable output message, when attached at completion.</summary>
    public Message? Message { get { lock (_gate) return _message; } }

    /// <summary>Sets an operation metadata value.</summary>
    /// <param name="name">The metadata name.</param>
    /// <param name="value">The metadata value.</param>
    public void SetMetadata(string name, string value)
    {
        Guard.NotNullOrWhiteSpace(name, nameof(name));
        Guard.NotNull(value, nameof(value));
        lock (_gate) _metadata[name] = value;
    }

    /// <summary>Completes the trace once, retaining the first completion's message and timing.</summary>
    /// <param name="endTime">Unix epoch milliseconds, or null to use the current time.</param>
    /// <param name="message">An optional immutable output message.</param>
    public void End(long? endTime = null, Message? message = null)
    {
        lock (_gate)
        {
            if (_endTime is not null) return;
            _message = message;
            _endTime = endTime ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}
