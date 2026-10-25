namespace Strands.Harness;

/// <summary>Optional source execution in the selected environment. Implementations own its authority and execution bounds.</summary>
/// <remarks>Advertising shell commands alone does not advertise this capability. The caller retains backend ownership.</remarks>
public interface ICodeSandbox : ISandbox
{
    /// <summary>Runs source in a fresh selected interpreter, streaming output and a terminal result. Separate StandardInput is not accepted for source execution.</summary>
    IAsyncEnumerable<Strands.Agents.Sandbox.SandboxStreamEvent> ExecuteCodeStreamingAsync(string code, string language,
        Strands.Agents.Sandbox.ExecuteOptions? options = null, CancellationToken cancellationToken = default);
}
