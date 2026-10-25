namespace Strands.Agents.MultiAgent;

/// <summary>Incrementally declares graph topology. Defaults match Graph; use GraphOptions.Python explicitly for Python compatibility.</summary>
public sealed class GraphBuilder
{
    private readonly List<Node> _nodes;
    private readonly List<Edge> _edges;
    private readonly List<string> _sources;
    private GraphOptions _options;
    /// <summary>Copies optional defaults. Topology and execution bounds are validated when Build is called.</summary>
    public GraphBuilder(GraphOptions? options = null)
    {
        _options = options ?? new();
        Guard.NotNull(_options.Nodes, nameof(options)); Guard.NotNull(_options.Edges, nameof(options));
        _nodes = new(_options.Nodes); _edges = new(_options.Edges); _sources = new(_options.Sources ?? []);
    }
    /// <summary>Adds an already constructed node and returns the builder.</summary>
    public GraphBuilder AddNode(Node node) { Guard.NotNull(node, nameof(node)); _nodes.Add(node); return this; }
    /// <summary>Adds a borrowed native agent and returns its node.</summary>
    public AgentNode AddNode(Agent agent, string? id = null, AgentNodeOptions? options = null) { var node = new AgentNode(agent, id, options); node.CaptureConstructionBaseline(); _nodes.Add(node); return node; }
    /// <summary>Adds a borrowed nested orchestrator and returns its node.</summary>
    public MultiAgentNode AddNode(IMultiAgent orchestrator, string? id = null) { var node = new MultiAgentNode(orchestrator, id); _nodes.Add(node); return node; }
    /// <summary>Adds an optional conditional connection between named nodes.</summary>
    public GraphBuilder AddEdge(string source, string target, EdgeCondition? condition = null) { _edges.Add(new(source, target, condition)); return this; }
    /// <summary>Adds an explicit source. With no explicit sources, Build infers nodes without incoming edges.</summary>
    public GraphBuilder SetEntryPoint(string nodeId) { Guard.NotNullOrWhiteSpace(nodeId, nameof(nodeId)); _sources.Add(nodeId); return this; }
    /// <summary>Transforms execution policies. Topology remains the nodes, edges and entry points declared on this builder.</summary>
    public GraphBuilder Configure(Func<GraphOptions, GraphOptions> configure)
    { Guard.NotNull(configure, nameof(configure)); _options = configure(_options) ?? throw new ArgumentException("Graph configuration cannot be null.", nameof(configure)); return this; }
    /// <summary>Creates a validated graph with an independent topology snapshot.</summary>
    public Graph Build() => new(_options with { Nodes = _nodes.ToArray(), Edges = _edges.ToArray(), Sources = _sources.Count == 0 ? null : _sources.ToArray() });
}
