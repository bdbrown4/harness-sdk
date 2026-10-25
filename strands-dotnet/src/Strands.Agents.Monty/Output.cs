using System.Text;

namespace Strands.Agents.Monty;

internal sealed class Output
{
    internal const int Limit = 200_000;
    private readonly StringBuilder _text = new();
    internal void Write(string text)
    {
        var count = Math.Min(text.Length, Limit + 2 - _text.Length);
        if (count > 0) _text.Append(text, 0, count);
    }
    internal string Text() => Cap(_text.ToString(), Limit);
    internal string WithError(string error)
    {
        var raw = _text.ToString();
        var messageLimit = Math.Max(Limit - raw.Length, Limit / 2);
        var message = Cap(error, messageLimit);
        var printed = Cap(raw, Limit - Math.Min(message.Length, messageLimit));
        return printed.Length == 0 ? message : printed + "\n\n" + message;
    }
    internal static string Cap(string value, int limit = Limit)
    {
        if (value.Length <= limit) return value.Trim();
        var count = limit;
        if (count > 0 && char.IsHighSurrogate(value[count - 1]) && char.IsLowSurrogate(value[count])) count--;
        return value.Substring(0, count).Trim() + "\n[output truncated at " + limit + " characters]";
    }
}
