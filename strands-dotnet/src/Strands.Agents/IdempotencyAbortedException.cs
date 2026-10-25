namespace Strands.Agents;

/// <summary>The primary invocation closed without a terminal result or another failure.</summary>
public sealed class IdempotencyAbortedException() : InvalidOperationException("The primary invocation was aborted before producing a terminal result.");
