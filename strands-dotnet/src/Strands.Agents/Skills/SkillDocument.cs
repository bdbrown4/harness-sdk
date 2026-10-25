using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Strands.Agents.Skills;

internal static class SkillDocument
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    internal static Skill Parse(string content, bool strict, string? path)
    {
        Guard.NotNull(content, nameof(content));
        var stripped = content.Trim().TrimStart('\uFEFF').TrimStart();
        if (!stripped.StartsWith("---", StringComparison.Ordinal)) throw new ArgumentException("SKILL.md must start with a --- frontmatter delimiter.", nameof(content));
        var closing = Regex.Match(stripped.Substring(3), @"\r?\n---[ \t]*(?:\r?\n|$)", RegexOptions.CultureInvariant, RegexTimeout);
        if (!closing.Success) throw new ArgumentException("SKILL.md frontmatter is missing its closing --- delimiter.", nameof(content));
        var frontmatter = ReadYaml(stripped.Substring(3, closing.Index).Trim());
        var name = RequiredText(frontmatter, "name");
        var description = RequiredText(frontmatter, "description");
        ValidateName(name, path, strict);
        var budget = 10000;
        var metadata = Read(frontmatter, "metadata") is IDictionary mapping
            ? JsonSerializer.SerializeToElement(JsonValue(mapping, new List<object>(), 0, ref budget))
            : JsonSerializer.SerializeToElement(new { });
        return new(new()
        {
            Name = name,
            Description = description,
            Instructions = stripped.Substring(3 + closing.Index + closing.Length).Trim(),
            Path = path,
            AllowedTools = AllowedTools(Read(frontmatter, "allowed-tools") ?? Read(frontmatter, "allowed_tools")),
            Metadata = metadata,
            License = Text(Read(frontmatter, "license")),
            Compatibility = Text(Read(frontmatter, "compatibility")),
        });
    }

    private static IDictionary ReadYaml(string text)
    {
        var parser = new DeserializerBuilder().WithDuplicateKeyChecking().WithAttemptingUnquotedStringTypeDeserialization().WithMaximumRecursion(64).Build();
        try { return parser.Deserialize<object>(text) as IDictionary ?? new Dictionary<object, object>(); }
        catch (YamlException)
        {
            SdkLogging.Warning("YAML parse failed, retrying with colon-quoting fallback");
            try { return parser.Deserialize<object>(FixColons(text)) as IDictionary ?? new Dictionary<object, object>(); }
            catch (YamlException exception) { throw new ArgumentException("Invalid SKILL.md YAML frontmatter: " + exception.Message, nameof(text), exception); }
        }
    }

    private static string FixColons(string text) => string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line =>
    {
        var match = Regex.Match(line, @"^(\s*\w[\w-]*):\s+(.+)$", RegexOptions.CultureInvariant, RegexTimeout);
        if (!match.Success) return line;
        var value = match.Groups[2].Value;
        return value.IndexOf(':') >= 0 && !value.StartsWith("\"", StringComparison.Ordinal) && !value.StartsWith("'", StringComparison.Ordinal)
            ? match.Groups[1].Value + ": \"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : line;
    }));

    private static object? Read(IDictionary mapping, string name) => mapping.Contains(name) ? mapping[name] : null;
    private static string RequiredText(IDictionary mapping, string name) => Read(mapping, name) is string { Length: > 0 } value
        ? value : throw new ArgumentException("SKILL.md must have a nonempty string '" + name + "' field in frontmatter.");
    private static string? Text(object? value) => value is null ? null : value is bool flag ? flag ? "true" : "false" : Convert.ToString(value, CultureInfo.InvariantCulture);
    private static IReadOnlyList<string>? AllowedTools(object? value)
    {
        if (value is string text) return string.IsNullOrWhiteSpace(text) ? null : Array.AsReadOnly(Regex.Split(text.Trim(), @"\s+", RegexOptions.CultureInvariant, RegexTimeout));
        return value is IList list ? Array.AsReadOnly(list.Cast<object?>().Where(item => item is not null).Select(item => Text(item)!).ToArray()) : null;
    }

    private static void ValidateName(string name, string? path, bool strict)
    {
        if (name.Length > 64) Issue("skill name exceeds the 64 character limit");
        if (!Regex.IsMatch(name, @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant, RegexTimeout)) Issue("skill name should contain lowercase alphanumeric characters or hyphens and must not start or end with a hyphen");
        if (name.IndexOf("--", StringComparison.Ordinal) >= 0) Issue("skill name contains consecutive hyphens");
        if (path is not null)
        {
            var directory = path.TrimEnd('/', '\\');
            directory = directory.Substring(Math.Max(directory.LastIndexOf('/'), directory.LastIndexOf('\\')) + 1);
            if (!string.Equals(directory, name, StringComparison.Ordinal)) Issue("skill name does not match its parent directory name");
        }
        void Issue(string message)
        {
            if (strict) throw new ArgumentException("name=<" + name + "> | " + message);
            SdkLogging.Warning("name=<{Name}> | {Message}", name, message);
        }
    }

    private static object? JsonValue(object? value, List<object> ancestors, int depth, ref int budget)
    {
        if (--budget < 0 || depth >= 64) throw new ArgumentException("Skill metadata exceeds its JSON depth or node-count limit.");
        if (value is not (IDictionary or IList)) return value;
        if (ancestors.Any(ancestor => ReferenceEquals(ancestor, value))) throw new ArgumentException("Skill metadata must not contain cyclic YAML aliases.");
        ancestors.Add(value);
        try
        {
            if (value is IDictionary mapping)
            {
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in mapping)
                {
                    if (entry.Key is not string key) throw new ArgumentException("Skill metadata keys must be strings.");
                    result.Add(key, JsonValue(entry.Value, ancestors, depth + 1, ref budget));
                }
                return result;
            }
            var sequence = new List<object?>();
            foreach (var item in (IList)value) sequence.Add(JsonValue(item, ancestors, depth + 1, ref budget));
            return sequence;
        }
        finally { ancestors.RemoveAt(ancestors.Count - 1); }
    }
}
