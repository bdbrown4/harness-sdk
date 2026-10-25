# Pure model capability queries

`HarnessModelCapabilities.SupportsThinking(identifier)` and
`SupportsWebSearch(identifier)` classify the existing Harness provider policy.
They do not resolve a model, invoke a factory, inspect credentials or environment,
log, access files, or contact a provider. These are pinned naming heuristics;
they do not establish live service availability.

Omit the identifier or pass null to use `Harness.DefaultModel`. An empty string
or whitespace is an unrecognized bare Bedrock identifier and returns false.
This follows TypeScript's nullish default; Python also defaults an empty string.
Identifiers are split at the first slash without trimming or case conversion.
These queries do not replace the stricter validation used when constructing a
configured model.

A ready `IModel`, including `RoutingModel`, returns true for thinking and false
for native search. The caller configured that instance. The helpers read no
properties, metadata, candidate lists or methods, and retain no ownership.
Strongly typed generic overloads keep `SupportsThinking(null)` and
`SupportsWebSearch(null)` unambiguous; a typed null model uses the default too.

Bedrock Nova2 reasoning follows the accepted native and pinned Python table.
The pinned TypeScript table omits that family. Claude versions, Bedrock region
prefixes, and Mantle GPT5/GPT6 native-search boundaries reuse the existing native
policy. Ready-model answers describe the source's conservative instance rule,
not a claim that every model actually implements those provider features.
