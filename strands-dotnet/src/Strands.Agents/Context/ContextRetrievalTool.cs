using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Strands.Agents.Context;

internal sealed class ContextRetrievalTool(ToolResultOffloadOptions options, Func<Agent, string, CancellationToken, ValueTask<ContentBlock?>>? source = null) : IRichTool
{
    internal const string ToolName = "retrieve_context";
    private static readonly Regex NestedQuantifier = new(@"[+*]\s*\)\s*[+*]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public ToolSpec Spec { get; } = new(ToolName, "Retrieve original tool content using a reference from an offload preview. Optionally search a regex or keyword with pattern, select a one-based inclusive line_range, and include context_lines (default 5).", JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"reference":{"type":"string","minLength":1},"pattern":{"type":"string"},"line_range":{"type":"object","properties":{"start":{"type":"integer","minimum":1},"end":{"type":"integer","minimum":1}},"required":["start","end"],"additionalProperties":false},"context_lines":{"type":"integer","minimum":0}},"required":["reference"],"additionalProperties":false}
        """));

    public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => (await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false)).Content;

    public async ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context, nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        var reference = input.GetProperty("reference").GetString() ?? throw new ArgumentException("A reference is required.", nameof(input));
        var block = source is null ? ContextReference.Resolve(context.Agent.Messages, reference, cancellationToken)
            : await source(context.Agent, reference, cancellationToken).ConfigureAwait(false);
        return await RenderAsync(block, reference, input, context, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ToolResultBlock> RenderAsync(ContentBlock? block, string reference, JsonElement input, ToolContext context, CancellationToken cancellationToken)
    {
        if (block is null) return new(Error(context, "reference not found: " + reference));
        var filtered = input.TryGetProperty("pattern", out var pattern) && !string.IsNullOrEmpty(pattern.GetString());
        var hasRange = input.TryGetProperty("line_range", out var range);
        var maxChars = options.MaxRetrievalTokens * 4;
        if (!filtered && !hasRange)
        {
            if (block is not (TextBlock or JsonBlock)) return new(new ToolResultBlock(context.ToolUse.ToolUseId, [block]));
            var text = block is TextBlock textBlock ? textBlock.Text : ((JsonBlock)block).Json.GetRawText();
            if (text.Length <= maxChars) return new(new ToolResultBlock(context.ToolUse.ToolUseId, [block]));
            return new(new ToolResultBlock(context.ToolUse.ToolUseId, Bound(text, maxChars)));
        }
        if (block is not (TextBlock or JsonBlock)) return new(Error(context, "cannot search non-text content; omit pattern and line_range to retrieve typed content"));
        var content = block is TextBlock value ? value.Text : ((JsonBlock)block).Json.GetRawText();
        if (content.Length == 0) return new(new ToolResultBlock(context.ToolUse.ToolUseId, Bound("Content is empty (0 lines).", maxChars)));
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var start = hasRange ? range.GetProperty("start").GetInt32() : 1;
        var end = hasRange ? range.GetProperty("end").GetInt32() : lines.Length;
        if (start < 1) return new(Error(context, $"line_range.start ({start.ToString(CultureInfo.InvariantCulture)}) must be >= 1."));
        if (start > end) return new(Error(context, $"line_range.start ({start.ToString(CultureInfo.InvariantCulture)}) must be <= line_range.end ({end.ToString(CultureInfo.InvariantCulture)})."));
        if (start > lines.Length) return new(Error(context, $"line_range.start ({start.ToString(CultureInfo.InvariantCulture)}) is beyond content length ({lines.Length.ToString(CultureInfo.InvariantCulture)} lines)."));
        end = Math.Min(end, lines.Length);
        var contextLines = input.TryGetProperty("context_lines", out var count) ? count.GetInt32() : 5;
        if (contextLines < 0) return new(Error(context, "context_lines must not be negative"));
        try
        {
            var originalPattern = filtered ? pattern.GetString()! : null;
            var regex = originalPattern is not null ? CreateRegex(originalPattern) : null;
            var selected = SelectLines(lines, start - 1, end - 1, contextLines, regex, cancellationToken);
            var scope = hasRange ? $" in lines {start.ToString(CultureInfo.InvariantCulture)}-{end.ToString(CultureInfo.InvariantCulture)}" : "";
            if (originalPattern is not null && selected.Matches == 0)
                return new(new ToolResultBlock(context.ToolUse.ToolUseId, Bound($"No matches found for pattern '{originalPattern}'{scope} (searched {(end - start + 1).ToString(CultureInfo.InvariantCulture)} lines).", maxChars)));
            var header = originalPattern is null
                ? $"[Lines {start.ToString(CultureInfo.InvariantCulture)}-{end.ToString(CultureInfo.InvariantCulture)} of {lines.Length.ToString(CultureInfo.InvariantCulture)}]"
                : $"[{selected.Matches.ToString(CultureInfo.InvariantCulture)} {(selected.Matches == 1 ? "match" : "matches")} for /{SafePattern(originalPattern)}/{scope}]";
            return new(new ToolResultBlock(context.ToolUse.ToolUseId, FormatLines(lines, selected.Visible, selected.Matched, header, originalPattern is not null, maxChars, cancellationToken)));
        }
        catch (RegexMatchTimeoutException) { return new(Error(context, "pattern matching exceeded the configured timeout")); }
    }

    private Regex CreateRegex(string pattern)
    {
        var capped = pattern.Length > 200 ? pattern.Substring(0, 200) : pattern;
        if (NestedQuantifier.IsMatch(capped)) return new(Regex.Escape(capped), RegexOptions.CultureInvariant, options.RegexTimeout);
        try { return new(capped, RegexOptions.CultureInvariant, options.RegexTimeout); }
        catch (ArgumentException) { return new(Regex.Escape(capped), RegexOptions.CultureInvariant, options.RegexTimeout); }
    }

    private static string SafePattern(string pattern)
    {
        var safe = new string(pattern.Take(50).Select(value => value is '\n' or '\r' or '/' or ']' ? ' ' : value).ToArray());
        return safe;
    }

    private static (bool[] Visible, bool[] Matched, int Matches) SelectLines(string[] lines, int start, int end, int contextLines, Regex? regex, CancellationToken cancellationToken)
    {
        var boundaries = new long[lines.Length + 1];
        var matched = new bool[lines.Length];
        var matches = 0;
        for (var i = start; i <= end; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (regex is not null && !regex.IsMatch(lines[i])) continue;
            if (regex is not null) { matched[i] = true; matches++; }
            var first = (int)Math.Max(start, (long)i - contextLines);
            var last = (int)Math.Min(end, (long)i + contextLines);
            boundaries[first]++;
            boundaries[last + 1]--;
        }
        var selected = new bool[lines.Length];
        long active = 0;
        for (var i = start; i <= end; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            active += boundaries[i];
            selected[i] = active > 0;
        }
        return (selected, matched, matches);
    }

    private static string FormatLines(string[] lines, bool[] selected, bool[] matched, string header, bool searched, int maxChars, CancellationToken cancellationToken)
    {
        var result = new StringBuilder(header).Append("\n\n");
        var last = Array.LastIndexOf(selected, true);
        var width = (last + 1).ToString(CultureInfo.InvariantCulture).Length;
        var previous = -1;
        var marker = searched ? "\n\n[output truncated, narrow your search]" : "\n\n[output truncated, narrow your range]";
        for (var i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!selected[i]) continue;
            var gap = previous >= 0 && i > previous + 1 ? "---\n" : "";
            var line = $"{gap}{(matched[i] ? '>' : ' ')} {(i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width)}| {lines[i]}\n";
            if ((long)result.Length + line.Length > maxChars) return Bound(result + line, maxChars, marker);
            result.Append(line);
            previous = i;
        }
        return Bound(result.ToString().TrimEnd('\n'), maxChars, marker);
    }

    private static string Bound(string text, int maxChars, string marker = "\n[truncated]")
    {
        return text.Length <= maxChars ? text : maxChars <= marker.Length ? marker.Substring(0, maxChars) : text.Substring(0, maxChars - marker.Length) + marker;
    }

    private ToolResultBlock Error(ToolContext context, string error) => new(context.ToolUse.ToolUseId, Bound("Error: " + error, options.MaxRetrievalTokens * 4), ToolResultStatus.Error);
}
