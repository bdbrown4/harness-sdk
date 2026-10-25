// The standard handler lists below are derived from JsonSchema.Net8.0.5,
// source commit 3520d7ac43e5c6c9b91abeac5af992eb82ffbf63.
// Private local handlers modify integer, bound, divisibility and equality evaluation.
/*
MIT License

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System.Text.Json;
using Strands.Agents.SchemaImplementation;
using Strands.Agents.SchemaImplementation.Keywords;

namespace Strands.Agents;

// The standard handler lists follow MIT-licensed JsonSchema.Net 8.0.5,
// commit 3520d7ac43e5c6c9b91abeac5af992eb82ffbf63, Dialect.{06,07,201909,202012}.cs.
// Each compilation owns its registries. Global defaults and registries are never mutated.
internal static partial class ToolSchema
{
    internal static JsonSchema Compile(JsonElement schema)
        => JsonSchema.FromText(schema.GetRawText(), CreateOptions());

    private static BuildOptions CreateOptions()
    {
        var dialects = new[]
        {
            CreateDialect(MetaSchemas.Draft6Id, Draft6, true),
            CreateDialect(MetaSchemas.Draft7Id, Draft7, true),
            CreateDialect(MetaSchemas.Draft201909Id, Draft2019, false),
            CreateDialect(MetaSchemas.Draft202012Id, Draft2020, false)
        };
        var registry = new DialectRegistry();
        foreach (var dialect in dialects) registry.Register(dialect);
        return new BuildOptions
        {
            Dialect = dialects[3],
            DialectRegistry = registry,
            SchemaRegistry = new SchemaRegistry(),
            VocabularyRegistry = new VocabularyRegistry()
        };
    }

    private static Dialect CreateDialect(Uri id, IEnumerable<IKeywordHandler> handlers, bool ignoreRefSiblings)
        => new(handlers.Select(SelectHandler))
        {
            Id = id,
            AllowUnknownKeywords = true,
            RefIgnoresSiblingKeywords = ignoreRefSiblings
        };

    private sealed class ExactIntegerType : IKeywordHandler
    {
        internal static readonly ExactIntegerType Instance = new();
        public string Name => "type";
        public object? ValidateKeywordValue(JsonElement value) => TypeKeyword.Instance.ValidateKeywordValue(value);
        public void BuildSubschemas(KeywordData keyword, BuildContext context) => TypeKeyword.Instance.BuildSubschemas(keyword, context);
        public KeywordEvaluation Evaluate(KeywordData keyword, EvaluationContext context)
        {
            var expected = (SchemaValueType)keyword.Value!;
            if (context.Instance.ValueKind != JsonValueKind.Number || !expected.HasFlag(SchemaValueType.Integer) || expected.HasFlag(SchemaValueType.Number))
                return TypeKeyword.Instance.Evaluate(keyword, context);
            return new KeywordEvaluation
            {
                Keyword = Name,
                IsValid = IsInteger(context.Instance),
                Error = "The value must be an integer."
            };
        }
    }

    // The BCL has already validated the token. Decimal scale, arbitrary integer
    // magnitude and exponent size do not require numeric conversion or powers.
    private static bool IsInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) return false;
        var raw = value.GetRawText().AsSpan();
        var start = raw[0] == '-' ? 1 : 0;
        var exponentAt = raw.IndexOfAny('e', 'E');
        var mantissaEnd = exponentAt < 0 ? raw.Length : exponentAt;
        var fractionalDigits = 0;
        var trailingZeros = 0;
        var fraction = false;
        var nonzero = false;
        for (var index = start; index < mantissaEnd; index++)
        {
            var digit = raw[index];
            if (digit == '.') { fraction = true; continue; }
            if (fraction) fractionalDigits++;
            if (digit == '0') trailingZeros++;
            else { trailingZeros = 0; nonzero = true; }
        }
        if (!nonzero) return true;
        long exponent = 0;
        if (exponentAt >= 0)
        {
            var index = exponentAt + 1;
            var negative = raw[index] == '-';
            if (raw[index] is '-' or '+') index++;
            var saturation = (long)raw.Length + 1;
            for (; index < raw.Length; index++)
                exponent = Math.Min(saturation, exponent * 10 + raw[index] - '0');
            if (negative) exponent = -exponent;
        }
        return exponent >= (long)fractionalDigits - trailingZeros;
    }

    private static readonly IKeywordHandler[] Draft6 =
    {
        Strands.Agents.SchemaImplementation.Keywords.Draft06.AdditionalItemsKeyword.Instance,
        AdditionalPropertiesKeyword.Instance,
        AllOfKeyword.Instance,
        AnyOfKeyword.Instance,
        CommentKeyword.Instance,
        ConstKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.ContainsKeyword.Instance,
        DefaultKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.DefinitionsKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.DependenciesKeyword.Instance,
        DescriptionKeyword.Instance,
        EnumKeyword.Instance,
        ExamplesKeyword.Instance,
        ExclusiveMaximumKeyword.Instance,
        ExclusiveMinimumKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.FormatKeyword.Annotate,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.IdKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.ItemsKeyword.Instance,
        MaximumKeyword.Instance,
        MaxItemsKeyword.Instance,
        MaxLengthKeyword.Instance,
        MaxPropertiesKeyword.Instance,
        MinimumKeyword.Instance,
        MinItemsKeyword.Instance,
        MinLengthKeyword.Instance,
        MinPropertiesKeyword.Instance,
        MultipleOfKeyword.Instance,
        NotKeyword.Instance,
        OneOfKeyword.Instance,
        PatternKeyword.Instance,
        PatternPropertiesKeyword.Instance,
        PropertiesKeyword.Instance,
        PropertyNamesKeyword.Instance,
        RefKeyword.Instance,
        RequiredKeyword.Instance,
        SchemaKeyword.Instance,
        TitleKeyword.Instance,
        TypeKeyword.Instance,
        UniqueItemsKeyword.Instance,
    };

    private static readonly IKeywordHandler[] Draft7 =
    {
        Strands.Agents.SchemaImplementation.Keywords.Draft06.AdditionalItemsKeyword.Instance,
        AdditionalPropertiesKeyword.Instance,
        AllOfKeyword.Instance,
        AnyOfKeyword.Instance,
        CommentKeyword.Instance,
        ConstKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.ContainsKeyword.Instance,
        ContentEncodingKeyword.Instance,
        ContentMediaTypeKeyword.Instance,
        DefaultKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.DefinitionsKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.DependenciesKeyword.Instance,
        DescriptionKeyword.Instance,
        ElseKeyword.Instance,
        EnumKeyword.Instance,
        ExamplesKeyword.Instance,
        ExclusiveMaximumKeyword.Instance,
        ExclusiveMinimumKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.FormatKeyword.Annotate,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.IdKeyword.Instance,
        IfKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.ItemsKeyword.Instance,
        MaximumKeyword.Instance,
        MaxItemsKeyword.Instance,
        MaxLengthKeyword.Instance,
        MaxPropertiesKeyword.Instance,
        MinimumKeyword.Instance,
        MinItemsKeyword.Instance,
        MinLengthKeyword.Instance,
        MinPropertiesKeyword.Instance,
        MultipleOfKeyword.Instance,
        NotKeyword.Instance,
        OneOfKeyword.Instance,
        PatternKeyword.Instance,
        PatternPropertiesKeyword.Instance,
        PropertiesKeyword.Instance,
        PropertyNamesKeyword.Instance,
        ReadOnlyKeyword.Instance,
        RefKeyword.Instance,
        RequiredKeyword.Instance,
        SchemaKeyword.Instance,
        ThenKeyword.Instance,
        TitleKeyword.Instance,
        TypeKeyword.Instance,
        UniqueItemsKeyword.Instance,
        WriteOnlyKeyword.Instance,
    };

    private static readonly IKeywordHandler[] Draft2019 =
    {
        Strands.Agents.SchemaImplementation.Keywords.Draft06.AdditionalItemsKeyword.Instance,
        AdditionalPropertiesKeyword.Instance,
        AllOfKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft201909.AnchorKeyword.Instance,
        AnyOfKeyword.Instance,
        CommentKeyword.Instance,
        ConstKeyword.Instance,
        ContainsKeyword.Instance,
        ContentEncodingKeyword.Instance,
        ContentMediaTypeKeyword.Instance,
        ContentSchemaKeyword.Instance,
        DefaultKeyword.Instance,
        DefsKeyword.Instance,
        DependentRequiredKeyword.Instance,
        DependentSchemasKeyword.Instance,
        DeprecatedKeyword.Instance,
        DescriptionKeyword.Instance,
        ElseKeyword.Instance,
        EnumKeyword.Instance,
        ExamplesKeyword.Instance,
        ExclusiveMaximumKeyword.Instance,
        ExclusiveMinimumKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.FormatKeyword.Annotate,
        IdKeyword.Instance,
        IfKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.ItemsKeyword.Instance,
        MaxContainsKeyword.Instance,
        MaximumKeyword.Instance,
        MaxItemsKeyword.Instance,
        MaxLengthKeyword.Instance,
        MaxPropertiesKeyword.Instance,
        MinContainsKeyword.Instance,
        MinimumKeyword.Instance,
        MinItemsKeyword.Instance,
        MinLengthKeyword.Instance,
        MinPropertiesKeyword.Instance,
        MultipleOfKeyword.Instance,
        NotKeyword.Instance,
        OneOfKeyword.Instance,
        PatternKeyword.Instance,
        PatternPropertiesKeyword.Instance,
        PropertiesKeyword.Instance,
        PropertyNamesKeyword.Instance,
        ReadOnlyKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft201909.RecursiveAnchorKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft201909.RecursiveRefKeyword.Instance,
        RefKeyword.Instance,
        RequiredKeyword.Instance,
        SchemaKeyword.Instance,
        ThenKeyword.Instance,
        TitleKeyword.Instance,
        TypeKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft201909.UnevaluatedItemsKeyword.Instance,
        UnevaluatedPropertiesKeyword.Instance,
        UniqueItemsKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft201909.VocabularyKeyword.Instance,
        WriteOnlyKeyword.Instance,
    };

    private static readonly IKeywordHandler[] Draft2020 =
    {
        AdditionalPropertiesKeyword.Instance,
        AllOfKeyword.Instance,
        AnchorKeyword.Instance,
        AnyOfKeyword.Instance,
        CommentKeyword.Instance,
        ConstKeyword.Instance,
        ContainsKeyword.Instance,
        ContentEncodingKeyword.Instance,
        ContentMediaTypeKeyword.Instance,
        ContentSchemaKeyword.Instance,
        DefaultKeyword.Instance,
        DefsKeyword.Instance,
        DependentRequiredKeyword.Instance,
        DependentSchemasKeyword.Instance,
        DeprecatedKeyword.Instance,
        DescriptionKeyword.Instance,
        DynamicAnchorKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft202012.DynamicRefKeyword.Instance,
        ElseKeyword.Instance,
        EnumKeyword.Instance,
        ExamplesKeyword.Instance,
        ExclusiveMaximumKeyword.Instance,
        ExclusiveMinimumKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft06.FormatKeyword.Annotate,
        IdKeyword.Instance,
        IfKeyword.Instance,
        ItemsKeyword.Instance,
        MaxContainsKeyword.Instance,
        MaximumKeyword.Instance,
        MaxItemsKeyword.Instance,
        MaxLengthKeyword.Instance,
        MaxPropertiesKeyword.Instance,
        MinContainsKeyword.Instance,
        MinimumKeyword.Instance,
        MinItemsKeyword.Instance,
        MinLengthKeyword.Instance,
        MinPropertiesKeyword.Instance,
        MultipleOfKeyword.Instance,
        NotKeyword.Instance,
        OneOfKeyword.Instance,
        PatternKeyword.Instance,
        PatternPropertiesKeyword.Instance,
        PrefixItemsKeyword.Instance,
        PropertiesKeyword.Instance,
        PropertyNamesKeyword.Instance,
        ReadOnlyKeyword.Instance,
        RefKeyword.Instance,
        RequiredKeyword.Instance,
        SchemaKeyword.Instance,
        ThenKeyword.Instance,
        TitleKeyword.Instance,
        TypeKeyword.Instance,
        UnevaluatedItemsKeyword.Instance,
        UnevaluatedPropertiesKeyword.Instance,
        UniqueItemsKeyword.Instance,
        Strands.Agents.SchemaImplementation.Keywords.Draft201909.VocabularyKeyword.Instance,
        WriteOnlyKeyword.Instance,
    };
}
