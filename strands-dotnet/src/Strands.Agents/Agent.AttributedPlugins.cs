namespace Strands.Agents;

public sealed partial class Agent
{
    private readonly List<IDisposable> _attributedPluginHooks = [];
    private readonly List<string> _attributedPluginTools = [];

    internal void RegisterAttributedPluginHooks(IReadOnlyList<AttributedHook> hooks)
    {
        foreach (var hook in hooks)
            _attributedPluginHooks.Add(Hooks.Register(hook.EventType, hook.Callback, hook.Order));
    }

    private void RollbackAttributedPlugins()
    {
        if (_attributedPluginHooks.Count == 0 && _attributedPluginTools.Count == 0) return;
        for (var index = _attributedPluginHooks.Count - 1; index >= 0; index--)
            _attributedPluginHooks[index].Dispose();
        _attributedPluginHooks.Clear();
        foreach (var name in _attributedPluginTools) _tools.Remove(name);
        _attributedPluginTools.Clear();
        Volatile.Write(ref _toolSnapshot, Array.AsReadOnly(_tools.Values.Select(value => value.Tool).ToArray()));
    }
}
