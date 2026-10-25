using System.Text.Json;
using Strands.Agents.SchemaImplementation;
using Strands.Agents.SchemaMoreImplementation;

namespace Strands.Agents;

internal static partial class ToolSchema
{
    private sealed class ExactEqualityKeyword(IKeywordHandler original) : IKeywordHandler
    {
        public string Name => original.Name;
        public object? ValidateKeywordValue(JsonElement value) => original.ValidateKeywordValue(value);
        public void BuildSubschemas(KeywordData keyword, BuildContext context) => original.BuildSubschemas(keyword, context);

        public KeywordEvaluation Evaluate(KeywordData keyword, EvaluationContext context)
        {
            if (Name == "uniqueItems")
            {
                if (context.Instance.ValueKind != JsonValueKind.Array || keyword.RawValue.ValueKind == JsonValueKind.False)
                    return original.Evaluate(keyword, context);
                var seen = new Dictionary<JsonElement, int>(ExactValueComparer.Instance);
                List<string>? matches = null;
                var index = 0;
                foreach (var item in context.Instance.EnumerateArray())
                {
                    if (seen.TryGetValue(item, out var first)) (matches ??= new()).Add($"({first},{index})");
                    else seen[item] = index;
                    index++;
                }
                return new KeywordEvaluation
                {
                    Keyword = Name,
                    IsValid = matches is null,
                    Error = matches is null ? null : ErrorMessages.GetUniqueItems(context.Options.Culture).ReplaceToken("duplicates", matches.ToArray())
                };
            }

            var valid = Name == "const"
                ? ExactValueComparer.Instance.Equals(keyword.RawValue, context.Instance)
                : keyword.RawValue.EnumerateArray().Any(value => ExactValueComparer.Instance.Equals(value, context.Instance));
            var error = valid ? null : Name == "const"
                ? ErrorMessages.GetConst(context.Options.Culture).ReplaceToken("value", keyword.RawValue.ToJsonString())
                : ErrorMessages.GetEnum(context.Options.Culture).ReplaceToken("received", context.Instance).ReplaceToken("values", keyword.RawValue);
            return new KeywordEvaluation { Keyword = Name, IsValid = valid, Error = error };
        }
    }

    // Distinct property names are unordered. Repeated names retain every value
    // and compare their occurrence order, matching the native BCL object profile.
    private sealed class ExactValueComparer : IEqualityComparer<JsonElement>
    {
        internal static readonly ExactValueComparer Instance = new();

        public bool Equals(JsonElement left, JsonElement right)
        {
            var pending = new Stack<(JsonElement Left, JsonElement Right)>();
            pending.Push((left, right));
            while (pending.Count != 0)
            {
                var pair = pending.Pop();
                if (pair.Left.ValueKind != pair.Right.ValueKind) return false;
                switch (pair.Left.ValueKind)
                {
                    case JsonValueKind.Number:
                        if (new ExactNumber(pair.Left).CompareTo(new ExactNumber(pair.Right)) != 0) return false;
                        break;
                    case JsonValueKind.String:
                        if (!StringComparer.Ordinal.Equals(pair.Left.GetString(), pair.Right.GetString())) return false;
                        break;
                    case JsonValueKind.Array:
                        if (pair.Left.GetArrayLength() != pair.Right.GetArrayLength()) return false;
                        var other = pair.Right.EnumerateArray();
                        foreach (var item in pair.Left.EnumerateArray()) { other.MoveNext(); pending.Push((item, other.Current)); }
                        break;
                    case JsonValueKind.Object:
                        var properties = new Dictionary<string, Queue<JsonElement>>(StringComparer.Ordinal);
                        var count = 0;
                        foreach (var property in pair.Left.EnumerateObject())
                        {
                            if (!properties.TryGetValue(property.Name, out var values)) properties.Add(property.Name, values = new());
                            values.Enqueue(property.Value);
                            count++;
                        }
                        foreach (var property in pair.Right.EnumerateObject())
                        {
                            if (!properties.TryGetValue(property.Name, out var values) || values.Count == 0) return false;
                            pending.Push((values.Dequeue(), property.Value));
                            count--;
                        }
                        if (count != 0) return false;
                        break;
                    case JsonValueKind.Null:
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        public int GetHashCode(JsonElement value)
        {
            var pending = new Stack<(JsonElement Value, bool Visited)>();
            var hashes = new Stack<int>();
            pending.Push((value, false));
            while (pending.Count != 0)
            {
                var item = pending.Pop();
                var hash = (int)item.Value.ValueKind;
                switch (item.Value.ValueKind)
                {
                    case JsonValueKind.Number:
                        hash = CombineHash(hash, new ExactNumber(item.Value).SemanticHash());
                        break;
                    case JsonValueKind.String:
                        hash = CombineHash(hash, StringComparer.Ordinal.GetHashCode(item.Value.GetString()!));
                        break;
                    case JsonValueKind.Array:
                        if (!item.Visited)
                        {
                            pending.Push((item.Value, true));
                            foreach (var child in item.Value.EnumerateArray()) pending.Push((child, false));
                            continue;
                        }
                        foreach (var child in item.Value.EnumerateArray()) hash = CombineHash(hash, hashes.Pop());
                        break;
                    case JsonValueKind.Object:
                        if (!item.Visited)
                        {
                            pending.Push((item.Value, true));
                            foreach (var property in item.Value.EnumerateObject()) pending.Push((property.Value, false));
                            continue;
                        }
                        var sum = 0;
                        foreach (var property in item.Value.EnumerateObject())
                            sum = unchecked(sum + CombineHash(StringComparer.Ordinal.GetHashCode(property.Name), hashes.Pop()));
                        hash = CombineHash(hash, sum);
                        break;
                }
                hashes.Push(hash);
            }
            return hashes.Pop();
        }
    }

    private static int CombineHash(int left, int right) => unchecked(left * 31 ^ right);
}
