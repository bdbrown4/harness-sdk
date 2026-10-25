namespace Strands.Agents.Middleware;

/// <summary>A streamed stage update or its single authoritative completed result.</summary>
/// <typeparam name="TResult">The stage result type.</typeparam>
public interface IMiddlewareEvent<out TResult>;

/// <summary>Forwards an agent event independently of the final stage result.</summary>
/// <typeparam name="TResult">The stage result type.</typeparam>
/// <param name="Event">The event to expose to the agent stream.</param>
public sealed record MiddlewareStreamEvent<TResult>(IAgentStreamEvent Event) : IMiddlewareEvent<TResult>;

/// <summary>Completes a stage. A middleware chain must emit exactly one completed result, after its stream updates.</summary>
/// <typeparam name="TResult">The stage result type.</typeparam>
/// <param name="Result">The authoritative result.</param>
public sealed record MiddlewareResultEvent<TResult>(TResult Result) : IMiddlewareEvent<TResult>;

/// <summary>Continues a stage with transformed input. The executor's cancellation token remains authoritative.</summary>
public delegate IAsyncEnumerable<IMiddlewareEvent<TResult>> MiddlewareNext<TContext, TResult>(TContext context);

/// <summary>Wraps a stage, allowing explicit retries, short-circuiting and event transformation.</summary>
public delegate IAsyncEnumerable<IMiddlewareEvent<TResult>> MiddlewareHandler<TContext, TResult>(TContext context, MiddlewareNext<TContext, TResult> next, CancellationToken cancellationToken);

/// <summary>Performs the actual model or tool operation at the end of a middleware chain.</summary>
public delegate IAsyncEnumerable<IMiddlewareEvent<TResult>> MiddlewareTerminal<TContext, TResult>(TContext context, CancellationToken cancellationToken);
