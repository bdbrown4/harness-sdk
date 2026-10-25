using CoreSandbox = Strands.Agents.Sandbox.Sandbox;
using LocalEnvironment = Strands.Agents.Sandbox.NotASandboxLocalEnvironment;

namespace Strands.Agents;

public sealed partial class Agent
{
    private readonly Lazy<CoreSandbox> _defaultSandbox = new(static () => new LocalEnvironment(), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The selected borrowed environment, or this Agent's lazy local default.</summary>
    public CoreSandbox Sandbox => _options.Sandbox ?? _defaultSandbox.Value;
}
