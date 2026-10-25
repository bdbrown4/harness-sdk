using System;
using System.Collections.Generic;
using System.Text;

namespace Strands.Agents.Storage;

internal static class QmdUnicode17
{
    private static readonly HashSet<string> StopWords = new HashSet<string>(QmdStopWords.Text.Split(' '), StringComparer.Ordinal);
    internal static bool IsLetterNumber(int value) => Contains(QmdUnicode17Data.LetterNumber, value);
    internal static bool IsCjk(int value) => Contains(QmdUnicode17Data.CjkScript, value);
    internal static bool IsCased(int value) => Contains(QmdUnicode17Data.Cased, value);
    internal static bool IsCaseIgnorable(int value) => Contains(QmdUnicode17Data.CaseIgnorable, value);
    internal static bool IsWhitespace(int value) => Contains(QmdUnicode17Data.EcmaWhitespace, value);

    private static bool Contains(int[] ranges, int value)
    {
        var low = 0; var high = ranges.Length / 2 - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2; var index = mid * 2;
            if (value < ranges[index]) high = mid - 1;
            else if (value > ranges[index + 1]) low = mid + 1;
            else return true;
        }
        return false;
    }

    internal static int DefaultLower(int value, out string? expansion)
    {
        var expansionIndex = Array.BinarySearch(QmdUnicode17Data.LowerExpansionKeys, value);
        if (expansionIndex >= 0) { expansion = QmdUnicode17Data.LowerExpansionValues[expansionIndex]; return value; }
        expansion = null;
        var ranges = QmdUnicode17Data.LowerRanges; var low = 0; var high = ranges.Length / 4 - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2; var index = mid * 4;
            if (value < ranges[index]) high = mid - 1;
            else if (value > ranges[index + 1]) low = mid + 1;
            else return (value - ranges[index]) % ranges[index + 2] == 0 ? value + ranges[index + 3] : value;
        }
        return value;
    }

    internal static string Lower(string text)
    {
        var points = Points(text); var result = new StringBuilder(text.Length);
        for (var index = 0; index < points.Count; index++)
        {
            var value = points[index];
            if (value == 0x03A3 && IsFinalSigma(points, index)) Append(result, 0x03C2);
            else
            {
                var lower = DefaultLower(value, out var expansion);
                if (expansion is null) Append(result, lower); else result.Append(expansion);
            }
        }
        return result.ToString();
    }

    private static bool IsFinalSigma(List<int> original, int sigma)
    {
        var before = sigma - 1;
        while (before >= 0 && IsCaseIgnorable(original[before])) before--;
        if (before < 0 || !IsCased(original[before])) return false;
        var after = sigma + 1;
        while (after < original.Count && IsCaseIgnorable(original[after])) after++;
        return after == original.Count || !IsCased(original[after]);
    }

    internal static List<string> Tokens(string text)
    {
        var result = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal); var term = new StringBuilder();
        foreach (var point in Points(Lower(text)))
        {
            if (point == '_' || IsLetterNumber(point)) Append(term, point);
            else Flush(term, seen, result);
        }
        Flush(term, seen, result); return result;
    }

    private static void Flush(StringBuilder term, HashSet<string> seen, List<string> result)
    {
        // JavaScript string length counts UTF16 code units, not scalar values.
        if (term.Length > 1)
        {
            var value = term.ToString();
            if (!StopWords.Contains(value) && seen.Add(value)) result.Add(value);
        }
        term.Clear();
    }

    internal static string NormalizeCjk(string text)
    {
        var result = new StringBuilder(text.Length); var inRun = false;
        foreach (var point in Points(text))
        {
            if (IsCjk(point))
            {
                result.Append(' '); Append(result, point); inRun = true;
            }
            else
            {
                if (inRun) { result.Append(' '); inRun = false; }
                Append(result, point);
            }
        }
        if (inRun) result.Append(' ');
        return result.ToString();
    }

    internal static string Trim(string text)
    {
        var points = Points(text); var first = 0; var last = points.Count - 1;
        while (first <= last && IsWhitespace(points[first])) first++;
        while (last >= first && IsWhitespace(points[last])) last--;
        var result = new StringBuilder();
        for (var index = first; index <= last; index++) Append(result, points[index]);
        return result.ToString();
    }

    private static List<int> Points(string text)
    {
        var result = new List<int>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                result.Add(char.ConvertToUtf32(character, text[++index]));
            else result.Add(character);
        }
        return result;
    }

    internal static void Append(StringBuilder target, int point)
    {
        if (point <= 0xFFFF) target.Append((char)point);
        else target.Append(char.ConvertFromUtf32(point));
    }
}
