using System.Text.Json;

namespace Strands.Agents.Sandbox;

/// <summary>Creates a stateless shell tool using an explicitly bound or invocation-selected sandbox.</summary>
public static class SandboxShell
{
    /// <summary>Describes fresh shell execution and its result fields.</summary>
    public const string DefaultDescription = "Executes shell commands and returns output (stdout), error (stderr), and exit_code (non-zero means the command failed). Each call runs in a fresh shell; state such as variables and the working directory does not persist across calls.";

    /// <summary>Creates a tool resolving the invocation Agent's sandbox.</summary>
    public static ITool Create() => CreateForAgent();
    /// <summary>Creates a configured tool resolving the invocation Agent's sandbox.</summary>
    public static ITool CreateForAgent(SandboxShellOptions? options = null) => CreateTool(null, options);
    /// <summary>Creates a tool borrowing the selected sandbox.</summary>
    public static ITool Create(Sandbox sandbox, SandboxShellOptions? options = null)
    {
        Guard.NotNull(sandbox, nameof(sandbox));
        return CreateTool(sandbox, options);
    }

    private static ITool CreateTool(Sandbox? boundSandbox, SandboxShellOptions? options)
    {
        options ??= new();
        var schema = options.InputSchema ?? JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                command = new { type = "string", description = "The shell command to execute." },
                timeout = new { type = "number", exclusiveMinimum = 0, description = "Timeout in seconds (default: 120)." },
            },
            required = new[] { "command" },
        });
        var spec = new ToolSpec(options.Name, options.Description ?? DefaultDescription, schema);
        _ = ToolSchema.Compile(spec.InputSchema);
        return new FunctionTool(spec, async (input, context, cancellationToken) =>
        {
            Guard.NotNull(context, nameof(context));
            cancellationToken.ThrowIfCancellationRequested();
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String)
                throw new ArgumentException("command must be a string.", nameof(input));
            var text = command.GetString()!;
            if (text.IndexOf('\0') >= 0) throw new ArgumentException("A command cannot contain NUL.", nameof(input));
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
                var result = await ConsumeAsync(sandbox, text, new() { Timeout = timeout }, cancellationToken).ConfigureAwait(false);
                return Output(result.Stdout, result.Stderr, result.ExitCode);
            }
            catch (SandboxTimeoutException error)
            {
                error.AppendDiagnostic(Output(error.PartialResult.Stdout, error.PartialResult.Stderr, 124));
                throw;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw new SandboxShellException(error.Message, error); }
        });
    }

    private static async ValueTask<ExecutionResult> ConsumeAsync(Sandbox sandbox, string command, ExecuteOptions options, CancellationToken cancellationToken)
    {
        var iterator = sandbox.ExecuteStreamingAsync(command, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        ExecutionResult? result = null;
        Exception? failure = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? dispatch = null;
        try
        {
            while (await iterator.MoveNextAsync().ConfigureAwait(false))
                if (iterator.Current is ExecutionResult terminal) { result = terminal; break; }
            if (result is null) throw new InvalidOperationException("Sandbox command execution ended without a terminal result.");
        }
        catch (Exception error)
        {
            failure = error;
            dispatch = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
        }
        finally
        {
            try { await iterator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                if (failure is null) failure = cleanup;
                else if (!ReferenceEquals(failure, cleanup))
                {
                    const string key = "Strands.CleanupErrors";
                    var errors = failure.Data[key] is List<Exception> list ? list
                        : failure.Data[key] is IEnumerable<Exception> previous ? previous.ToList() : new List<Exception>();
                    if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
                    failure.Data[key] = errors;
                }
            }
        }
        if (failure is not null) (dispatch ?? System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure)).Throw();
        return result!;
    }

    private static string Output(string output, string error, int exitCode) => JsonSerializer.Serialize(new { output, error, exit_code = exitCode });
}

/// <summary>The selected sandbox failed to execute a shell command; its original failure remains the cause.</summary>
public sealed class SandboxShellException(string message, Exception? cause = null) : Exception(message, cause);
