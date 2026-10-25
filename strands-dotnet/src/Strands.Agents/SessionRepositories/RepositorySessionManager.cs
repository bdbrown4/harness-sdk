namespace Strands.Agents.SessionRepositories;

/// <summary>Binds accepted native agent messages and state to a borrowed indexed repository.</summary>
public sealed class RepositorySessionManager : IPlugin
{
    private readonly ISessionRepository _repository;
    private readonly object _bindingsGate = new();
    private readonly Dictionary<string, RepositoryAgentJournal> _bindings = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private bool _sessionReady;
    private static readonly AsyncLocal<SessionOperationScope?> SessionOperations = new();
    /// <summary>Creates a lazy session binding; construction performs no repository I/O.</summary>
    public RepositorySessionManager(string sessionId, ISessionRepository repository)
    {
        Guard.NotNullOrWhiteSpace(sessionId, nameof(sessionId));
        Guard.NotNull(repository, nameof(repository));
        SessionId = sessionId;
        _repository = repository;
    }
    /// <inheritdoc />
    public string Name => "strands:repository-session-manager";
    /// <summary>The opaque indexed session identity.</summary>
    public string SessionId { get; }
    /// <inheritdoc />
    public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent));
        cancellationToken.ThrowIfCancellationRequested();
        var journal = agent.GetRepositoryJournal(this);
        agent.Hooks.Register<InitializedEvent>((_, token) => agent.SynchronizeRepositoryJournalAsync(journal, token), int.MinValue);
        return default;
    }
    /// <inheritdoc />
    public IReadOnlyList<ITool> GetTools() => Array.Empty<ITool>();

    internal static RepositorySessionManager? Select(AgentOptions options)
    {
        var managers = options.Plugins.OfType<RepositorySessionManager>().ToArray();
        if (managers.Length > 1) throw new ArgumentException("Only one indexed session manager can bind to an agent.", nameof(options));
        if (managers.Length == 0) return null;
        if (options.SessionStore is not null) throw new ArgumentException("An indexed session manager cannot be combined with a whole-snapshot session store.", nameof(options));
        if (options.SessionId is not null && options.SessionId != managers[0].SessionId) throw new ArgumentException("The configured session identifiers conflict.", nameof(options));
        if (options.ContextManager is not null || options.Plugins.OfType<Context.ContextManager>().Any())
            throw new NotSupportedException("Indexed live sessions do not serialize the first-class context manager's active projection.");
        return managers[0];
    }

    internal RepositoryAgentJournal Bind(Agent agent)
    {
        var journal = new RepositoryAgentJournal(this, _repository, agent);
        lock (_bindingsGate)
        {
            if (_bindings.ContainsKey(journal.BindingId)) throw new ArgumentException("Agent identifiers must be unique within an indexed session manager.", nameof(agent));
            _bindings.Add(journal.BindingId, journal);
        }
        return journal;
    }

    internal void Release(RepositoryAgentJournal journal)
    {
        lock (_bindingsGate)
            if (_bindings.TryGetValue(journal.BindingId, out var current) && ReferenceEquals(current, journal)) _bindings.Remove(journal.BindingId);
    }

    internal async ValueTask<bool> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        for (var scope = SessionOperations.Value; scope is not null; scope = scope.Parent)
            if (scope.IsOpen && ReferenceEquals(scope.Manager, this)) throw new InvalidOperationException("A repository callback cannot await its owning session initialization gate.");
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previous = SessionOperations.Value;
        var currentScope = new SessionOperationScope(this, previous);
        SessionOperations.Value = currentScope;
        try
        {
            if (_sessionReady) return false;
            var current = await _repository.ReadSessionAsync(SessionId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var created = current is null;
            if (created) await _repository.CreateSessionAsync(new(SessionId), cancellationToken).ConfigureAwait(false);
            else if (current!.SessionId != SessionId) throw new InvalidDataException("The repository returned another session's metadata.");
            cancellationToken.ThrowIfCancellationRequested();
            _sessionReady = true;
            return created;
        }
        finally { currentScope.Close(); SessionOperations.Value = previous; _sessionGate.Release(); }
    }

    private sealed class SessionOperationScope(RepositorySessionManager manager, SessionOperationScope? parent)
    {
        private int _open = 1;
        internal RepositorySessionManager Manager { get; } = manager;
        internal SessionOperationScope? Parent { get; } = parent;
        internal bool IsOpen => Volatile.Read(ref _open) != 0;
        internal void Close() => Volatile.Write(ref _open, 0);
    }
}
