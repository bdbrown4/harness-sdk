# Explicit null authority choices

`Option.Null(name, description)` creates a named developer option whose `Value` is actually null. This is distinct from the existing constructor, where an omitted or null value resolves to the option's name.

```csharp
new Option("default").Value;                 // "default"
new Option("default", null).Value;           // "default"
new Option("default", "literal").Value;      // "literal"
Option.Null("default", "Use the default").Value; // null
```

The public constructor, its parameter order and defaults, and implicit conversion from a string are preserved. In particular, a second positional string passed to `new Option` remains the value. The factory's second string is its description. `Value` is annotated `object?` so nullable consumers see the actual supported representation; its CLR property type remains `object`.

An explicit null instruction or model choice can resolve before a preset fallback. A selected null context choice resolves to the existing native `"none"` context mode, matching its validation and the existing fixed-null default. It does not copy parent history. Tool and MCP-server choices must name real grants and reject null values before tool publication.

Options are developer configuration. This addition does not introduce an authority codec or change the existing constructor's JSON deserialization semantics. A serializer that uses that constructor still applies its legacy null-to-name rule; no general explicit-null JSON round trip is promised. Arbitrary Python values and Python `str(None)` labels are separate source-profile boundaries.
