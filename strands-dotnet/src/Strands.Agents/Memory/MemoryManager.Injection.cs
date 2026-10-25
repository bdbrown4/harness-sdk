
namespace Strands.Agents.Memory;

public sealed partial class MemoryManager
{
    private void RegisterInjection(Agent agent, MemoryInjectionOptions options) => agent.Hooks.Register<BeforeModelCallEvent>(async (value, token) =>
    {
        MemoryTelemetry.Operation? operation = null;
        var injected = false;
        var entryCount = 0;
        var formatError = false;
        try
        {
            var messages = value.Request.Messages;
            var userTurn = messages.Count > 0 && messages[messages.Count - 1].Role == Role.User && !messages[messages.Count - 1].Content.OfType<ToolResultBlock>().Any();
            if (!(options.Predicate?.Invoke(messages) ?? (options.Trigger == MemoryInjectionTrigger.EveryTurn || userTurn))) return;
            operation = _telemetry.Inject(options.MaxEntries);
            await operation.RunActiveAsync(async () =>
            {
                var query = options.Query is { } derive ? await derive(messages, token).ConfigureAwait(false)
                    : messages.LastOrDefault(message => message.Role == (userTurn ? Role.User : Role.Assistant))?.Text.Trim();
                if (string.IsNullOrWhiteSpace(query)) return false;
                var entries = (await SearchAsync(query!, new() { MaxSearchResults = options.MaxEntries }, token).ConfigureAwait(false)).Take(options.MaxEntries).ToArray();
                if (entries.Length == 0) return false;
                string? text;
                try
                {
                    text = options.Format is { } format ? await format(entries, token).ConfigureAwait(false)
                        : "<memory>\n" + string.Join("\n", entries.Select(entry => "<entry" + (entry.StoreName is { } source ? " source=\"" + Escape(source) + "\"" : "") + ">" + Escape(entry.Content) + "</entry>")) + "\n</memory>";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { formatError = true; throw; }
                // Upstream reports successful rendering before the input projection applies its empty-content guards.
                injected = true;
                entryCount = entries.Length;
                if (string.IsNullOrWhiteSpace(text)) return false;
                var index = messages.Count - 1;
                while (index >= 0 && messages[index].Role != Role.User) index--;
                if (index < 0) return false;
                var original = messages[index];
                var separator = original.Content.Count > 0 && !text!.StartsWith("\n", StringComparison.Ordinal) ? "\n\n" : "";
                var projected = messages.ToArray();
                projected[index] = new(original.Role, original.Content.Concat(new[] { new TextBlock(separator + text) }).ToArray(), original.TrackingId, original.Metadata);
                value.Request = value.Request with { Messages = projected, DynamicTrailingBlocks = checked(value.Request.DynamicTrailingBlocks + (index == projected.Length - 1 ? 1 : 0)) };
                return true;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        {
            var failure = NormalizeCallerCancellation(error, token);
            if (!ReferenceEquals(error, failure)) throw failure;
            throw;
        }
        catch (Exception error)
        {
            try { SdkLogging.Warning(error, "Memory injection failed; continuing without retrieved memory: {Error}", error.Message); }
            catch (Exception) { /* Injection remains fail-open when diagnostic listeners fail. */ }
        }
        finally { operation?.CompleteInjection(injected, entryCount, formatError); }
    });

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
}
