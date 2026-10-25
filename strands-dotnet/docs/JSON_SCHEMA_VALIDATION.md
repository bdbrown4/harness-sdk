# Native tool schema validation

Native tool schemas without `$schema` use JSON Schema draft 2020-12. Explicit
draft 6, draft 7, draft 2019-09 and draft 2020-12 declarations select their own
standard handlers, including declared and referenced resources. Legacy tuple
`items` arrays and `dependencies` require a suitable explicit older draft.
The earlier unpublished prototype's mixed-draft default is intentionally replaced.

Each compilation owns local registries and standard dialect clones. It does not
change process-global schema or dialect defaults. The remaining official keyword
handlers and reference machinery use pinned MIT-licensed JsonSchema.Net 8.0.5
source in the distinct unsigned `Strands.Agents.Schema.Private` implementation
assembly. Its namespace is separate from official vendor packages. Two local
registry null guards repair portable initialization; the source audit records
those changes and the mechanical namespace/resource-prefix transformation.
The implementation's compatibility shim is internal, keeping its target-specific
extension bridges outside the package's public API; method bodies are unchanged.
The Core package carries the implementation, localization satellites and exact
upstream MIT notice. It does not introduce another public NuGet package or expose
schema-engine types in Core's public API. See the implementation's `UPSTREAM.md`
and provenance manifest for maintenance and replacement requirements.
The same private assembly includes the pinned Pointer 6.0.1 and More 2.2.0
source dependencies with distinct namespaces and unchanged bodies. It avoids
forcing official Pointer or More package versions into an application. Shared
Humanizer, System.Text.Json and Immutable floors remain at the native baseline.
This is a native validation policy; it does not claim that the pinned
TypeScript Zod and Python Pydantic execution paths are identical validators.

Private handlers evaluate integer classification, numeric bounds, `multipleOf`,
`const`, `enum` and `uniqueItems` without decimal or double rounding. Numeric
coefficient and exponent text are normalized without expanding powers of ten.
Bound comparison uses signed decimal-string orders; divisibility uses BCL
`BigInteger` coefficient arithmetic and factors of two and five. Allocation and
work depend on the physical input, not the magnitude represented by an exponent.
Coefficient arithmetic still has ordinary CPU and memory costs; it is not a
constant-time operation. No new artificial 1,024-character numeric cap is imposed.
The standard positive-divisor requirement is enforced when compiling `multipleOf`.

Structural equality uses ordinal decoded strings and property names, ordered
arrays and unordered distinct object property names. Every repeated object member
is retained: occurrences of the same name compare in their original order, with
other names allowed between them. Thus `{"x":1,"x":2}` differs from
`{"x":2,"x":1}` and from `{"x":2}`. This explicit native duplicate-member
profile follows the documented native BCL object-equality profile; duplicate JSON
member names do not have an unambiguous JSON Schema object data model. The profile
does not silently choose the first or last value. Equivalent numeric spellings
have matching semantic hashes for `uniqueItems`; hash collisions are resolved by
the same structural comparer. The comparer does not replace global JSON equality.

The official handler lists are attributed to their pinned source commit and carry
the original MIT copyright and permission notice in `ToolSchema.cs`. Private
numeric handlers can be retired only after a compatible, acceptably licensed
official dependency demonstrates exact classification, comparison, divisibility
and structural equality without exponent-sized allocation. No version 9 license
or upgrade is assumed.

JSON Schema's mathematical integer type includes `1.0` and `1e2`. A tool's later
native argument binder can still impose its own lexical or CLR-type constraints.
The Read paging integral/null prerequisite and its remaining TypeScript fractional
and Python coercion profiles are separate from this schema migration. Selected
numeric and entrypoint tests do not establish complete JSON Schema conformance.

The standard CI formatter excludes only the pinned implementation source
directories. A portable offline audit verifies all 183 vendor files and inverts
the 153 C# source transformations against their original SHA-256 and Git blob
identities. Authored code keeps the ordinary formatter, compiler and API checks.
