# Environment instruction character counting

`EnvironmentContextConfig.CharacterCounting` selects the unit for the16000-unit
AGENTS.md injection cap. The default, `Utf16CodeUnits`, retains TypeScript string
slicing and the existing native behavior. `UnicodeCodePoints` selects Python
string counting: a valid surrogate pair counts once, a combining mark counts
separately, and each isolated surrogate counts once. Neither mode counts bytes
or grapheme clusters. The truncation marker and selected-sandbox authority are unchanged.

```csharp
var environment = new EnvironmentContext(sandbox, new EnvironmentContextConfig
{
    CharacterCounting = EnvironmentCharacterCounting.UnicodeCodePoints
});
```

Exactly16000 selected units have no truncation marker. Longer instructions retain
the first16000 units and the existing marker. Discovery remains memoized per
Agent, the date refreshes each turn, and injected context never becomes durable
conversation history. Invalid enum values reject during construction, before
discovery. The existing plugin name argument identity is preserved.

Pinned sources: `harness-py/src/strands_harness/plugins/environment.py` uses
Python string length/slices; `harness-ts/src/plugins/environment.ts` uses
TypeScript string length/slices. Their astral-character units differ, so the
native API exposes this choice explicitly.
