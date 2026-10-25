using System.Diagnostics;

namespace Strands.Agents;

public sealed partial class Agent
{
    internal ActivityContext? MemoryInvocationTraceContext => _invocationTrace?.RecordingContext ?? _memoryInvocationTraceContext;
}
