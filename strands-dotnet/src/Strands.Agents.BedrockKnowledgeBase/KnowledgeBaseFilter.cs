using Amazon.BedrockAgentRuntime.Model;
using Amazon.Runtime.Documents;

namespace Strands.Agents.BedrockKnowledgeBase;

internal static class KnowledgeBaseFilter
{
    internal static RetrievalFilter? Copy(RetrievalFilter? filter) => Copy(filter, []);
    private static RetrievalFilter? Copy(RetrievalFilter? filter, List<RetrievalFilter> ancestors)
    {
        if (filter is null) return null;
        if (ancestors.Any(parent => ReferenceEquals(parent, filter))) throw new ArgumentException("A retrieval filter cannot contain a cycle.", nameof(filter));
        ancestors.Add(filter);
        try
        {
            return new()
            {
                AndAll = filter.AndAll?.Select(item => Copy(item, ancestors) ?? throw new ArgumentException("Filter groups cannot contain null.", nameof(filter))).ToList(),
                OrAll = filter.OrAll?.Select(item => Copy(item, ancestors) ?? throw new ArgumentException("Filter groups cannot contain null.", nameof(filter))).ToList(),
                Equals = Attribute(filter.Equals),
                NotEquals = Attribute(filter.NotEquals),
                GreaterThan = Attribute(filter.GreaterThan),
                GreaterThanOrEquals = Attribute(filter.GreaterThanOrEquals),
                LessThan = Attribute(filter.LessThan),
                LessThanOrEquals = Attribute(filter.LessThanOrEquals),
                In = Attribute(filter.In),
                NotIn = Attribute(filter.NotIn),
                ListContains = Attribute(filter.ListContains),
                StartsWith = Attribute(filter.StartsWith),
                StringContains = Attribute(filter.StringContains)
            };
        }
        finally { ancestors.RemoveAt(ancestors.Count - 1); }
    }
    internal static bool Empty(RetrievalFilter filter) => filter.AndAll is null && filter.OrAll is null &&
        filter.Equals is null && filter.NotEquals is null && filter.GreaterThan is null && filter.GreaterThanOrEquals is null &&
        filter.LessThan is null && filter.LessThanOrEquals is null && filter.In is null && filter.NotIn is null &&
        filter.ListContains is null && filter.StartsWith is null && filter.StringContains is null;
    private static FilterAttribute? Attribute(FilterAttribute? attribute) => attribute is null ? null : new() { Key = attribute.Key, Value = Document.FromObject(Plain(attribute.Value)) };

    private static object? Plain(Document document) => Plain(document, []);
    private static object? Plain(Document document, List<object> ancestors)
    {
        if (document.IsNull()) return null;
        if (document.IsBool()) return document.AsBool();
        if (document.IsInt()) return document.AsInt();
        if (document.IsLong()) return document.AsLong();
        if (document.IsDouble()) return document.AsDouble();
        if (document.IsString()) return document.AsString();
        object? collection = document.IsList() ? document.AsList() : document.IsDictionary() ? document.AsDictionary() : null;
        if (collection is not null)
        {
            if (ancestors.Any(parent => ReferenceEquals(parent, collection))) throw new ArgumentException("A retrieval Document cannot contain a cycle.", nameof(document));
            ancestors.Add(collection);
            try
            {
                if (document.IsList()) return document.AsList().Select(item => Plain(item, ancestors)).ToList();
                return document.AsDictionary().ToDictionary(item => item.Key, item => Plain(item.Value, ancestors), StringComparer.Ordinal);
            }
            finally { ancestors.RemoveAt(ancestors.Count - 1); }
        }
        throw new ArgumentException("Unsupported retrieval Document value.", nameof(document));
    }
}
