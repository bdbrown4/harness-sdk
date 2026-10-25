using System.Text.Json;

namespace Strands.Agents.Sandbox;

/// <summary>Creates an optional Python tool borrowing an explicitly selected sandbox.</summary>
public static class PythonRepl
{
    /// <summary>Describes fresh interpreter execution and file-based persistence.</summary>
    public const string DefaultDescription = "Executes Python code and returns output (stdout), error (stderr), and exit_code (non-zero means the code failed). Each call runs in a fresh interpreter; variables, imports, and definitions do not persist across calls. Files written to the working directory persist while the sandbox is alive, so save intermediate results to files when later calls need them. stdin is not available (input() fails). Use print() to surface values.";

    /// <summary>Creates a tool that resolves the invocation Agent's sandbox.</summary>
    public static ITool Create() => CreateForAgent();

    /// <summary>Creates a configured tool that resolves the invocation Agent's sandbox.</summary>
    public static ITool CreateForAgent(PythonReplOptions? options = null) => CreateTool(null, options);

    /// <summary>Creates a tool bound to the selected borrowed sandbox; it never chooses a host fallback.</summary>
    public static ITool Create(Sandbox sandbox, PythonReplOptions? options = null)
    {
        Guard.NotNull(sandbox, nameof(sandbox));
        return CreateTool(sandbox, options);
    }

    private static ITool CreateTool(Sandbox? boundSandbox, PythonReplOptions? options)
    {
        options ??= new();
        SandboxPolicy.Language(options.Language, nameof(options.Language));
        var language = options.Language;
        var spec = new ToolSpec(options.Name, options.Description ?? DefaultDescription, JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                code = new { type = "string", description = "Python source to execute." },
                timeout = new { type = "number", exclusiveMinimum = 0, description = "Timeout in seconds (default: 120)." },
            },
            required = new[] { "code" },
        }));
        return new FunctionTool(spec, async (input, context, cancellationToken) =>
        {
            Guard.NotNull(context, nameof(context));
            cancellationToken.ThrowIfCancellationRequested();
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("code", out var source) || source.ValueKind != JsonValueKind.String)
                throw new ArgumentException("code must be a string.", nameof(input));
            var timeout = TimeSpan.FromSeconds(120);
            if (input.TryGetProperty("timeout", out var configured))
            {
                if (configured.ValueKind != JsonValueKind.Number || !configured.TryGetDouble(out var seconds) || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0 || seconds > int.MaxValue / 1000d)
                    throw new ArgumentOutOfRangeException(nameof(input), "timeout must be a positive finite number of seconds within the native timer range.");
                timeout = TimeSpan.FromSeconds(seconds);
                SandboxPolicy.Duration(timeout, nameof(input));
            }
            var sandbox = boundSandbox;
            if (sandbox is null)
            {
                Guard.NotNull(context.Agent, nameof(context.Agent));
                sandbox = context.Agent.Sandbox;
            }
            try
            {
                var result = await sandbox.ExecuteCodeAsync(source.GetString()!, language, new() { Timeout = timeout }, cancellationToken).ConfigureAwait(false);
                return Output(result.Stdout, result.Stderr, result.ExitCode);
            }
            catch (SandboxTimeoutException error)
            {
                error.AppendDiagnostic(Output(error.PartialResult.Stdout, error.PartialResult.Stderr, 124));
                throw;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw new PythonReplException(error.Message, error); }
        });
    }

    private static string Output(string output, string error, int exitCode) => JsonSerializer.Serialize(new { output, error, exit_code = exitCode });
}

/// <summary>A selected sandbox could not execute Python; the original transport failure remains its cause.</summary>
public sealed class PythonReplException(string message, Exception? cause = null) : Exception(message, cause);
