using Strands.Agents.SchemaImplementation;

namespace Strands.Agents;

public sealed partial class Agent
{
    // Track the tools supplied by this sandbox binding separately from user tools
    // so child construction can rebind them to the selected child sandbox.
    private readonly List<ITool> _sandboxVendedTools = [];

    private void InitializeSandboxTools(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tools = (Sandbox.GetTools() ?? throw new InvalidOperationException("The selected sandbox returned null tools.")).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var names = new HashSet<string>(_tools.Keys, StringComparer.Ordinal);
        var pending = new List<KeyValuePair<string, (ITool Tool, JsonSchema Schema)>>();
        foreach (var tool in tools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guard.NotNull(tool, nameof(tools));
            var spec = tool.Spec ?? throw new InvalidOperationException("A sandbox tool returned a null specification.");
            cancellationToken.ThrowIfCancellationRequested();
            if (names.Contains(spec.Name)) continue;
            var included = _options.ToolFilter?.Invoke(tool) != false;
            cancellationToken.ThrowIfCancellationRequested();
            if (!included) continue;
            ValidateNormalizedToolName(spec.Name, names, initialization: true);
            var schema = ToolSchema.Compile(spec.InputSchema);
            names.Add(spec.Name);
            pending.Add(new(spec.Name, (tool, schema)));
        }
        var preparedTools = pending.Select(value => value.Value.Tool).ToArray();
        ValidateDelegationRegistration(preparedTools);
        _backgroundTasks?.Policy.Validate(preparedTools);
        cancellationToken.ThrowIfCancellationRequested();
        if (pending.Count == 0) return;
        var snapshot = Array.AsReadOnly(Tools.Concat(preparedTools).ToArray());
        lock (_initializationGate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            // Cached initialization owns registration admission. Provider updates
            // stay pending and public application mutation is refused until it ends.
            foreach (var entry in pending) _tools.Add(entry.Key, entry.Value);
            _applicationTools.AddRange(preparedTools);
            _sandboxVendedTools.AddRange(preparedTools);
            Volatile.Write(ref _toolSnapshot, snapshot);
        }
    }

    private ITool[] GetChildApplicationTools() => _applicationTools
        .Where(tool => !_sandboxVendedTools.Any(value => ReferenceEquals(value, tool)))
        .ToArray();

    private void MarkSandboxToolsAsApplicationOwned(IEnumerable<ITool> tools)
    {
        foreach (var tool in tools) _sandboxVendedTools.RemoveAll(value => ReferenceEquals(value, tool));
    }

    private void PruneSandboxToolOrigins(IEnumerable<ITool> registered)
    {
        var retained = registered.ToArray();
        _sandboxVendedTools.RemoveAll(origin => !retained.Any(tool => ReferenceEquals(tool, origin)));
    }

    private void RollbackSandboxTools()
    {
        if (_sandboxVendedTools.Count == 0) return;
        lock (_initializationGate)
        {
            bool IsVended(ITool tool) => _sandboxVendedTools.Any(value => ReferenceEquals(value, tool));
            var snapshot = Array.AsReadOnly(Tools.Where(tool => !IsVended(tool)).ToArray());
            foreach (var name in _tools.Where(value => IsVended(value.Value.Tool)).Select(value => value.Key).ToArray()) _tools.Remove(name);
            _applicationTools.RemoveAll(tool => IsVended(tool));
            _sandboxVendedTools.Clear();
            Volatile.Write(ref _toolSnapshot, snapshot);
        }
    }
}
