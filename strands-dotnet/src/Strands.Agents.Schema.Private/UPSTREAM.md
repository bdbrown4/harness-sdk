# Private schema implementation source

This implementation is derived from json-everything/json-everything:

- JsonSchema.Net 8.0.5, commit `3520d7ac43e5c6c9b91abeac5af992eb82ffbf63`:
  126 active C# files, 19 meta-schema JSON files and ten localization resources.
- JsonPointer.Net 6.0.1, commit `694b3e47897b6bca03ec8285acb426822c129308`:
  12 active C# files and no resources.
- Json.More.Net 2.2.0, commit `f53d4d27eeed6a73d377bf4188175f0b8a46f856`:
  15 active C# files and no resources.

All three pinned source sets carry the exact same MIT license bytes, retained in
`LICENSE.MIT` and included in the Core NuGet package. Original copyright headers
are retained. The schema project's two excluded compile files remain excluded.
JSON, localization and license bytes are unchanged. C# files use UTF-8 without BOM
and LF. Token-aware namespace transforms replace `Json.Schema`, `Json.Pointer`
and `Json.More` with `Strands.Agents.SchemaImplementation`,
`Strands.Agents.SchemaPointerImplementation` and
`Strands.Agents.SchemaMoreImplementation`, respectively. Dependency imports use
the same private namespaces. The only engine algorithm changes are two null
guards in `DialectRegistry` when a local registry has no built-in well-known
dialect set. Pointer and More method bodies and conditional compilation are
unchanged. The per-file audit records every transformation.

The implementation has a distinct unsigned assembly name,
`Strands.Agents.Schema.Private`. It does not impersonate the vendor's signed
assemblies. Only the upstream `DotnetCompatibility` shim is internal, so its
target-specific extension bridges remain implementation details. Other vendor
types retain their visibility in the distinct implementation namespaces; they are
technically referenceable but are not supported Core API. No public Core signature
exposes them. There is no separate public NuGet package.

The Core package bundles the implementation and all nine satellite assemblies.
It does not require official JsonSchema.Net, JsonPointer.Net or Json.More.Net
packages. The small source closure avoids selecting incompatible official Pointer
or More versions in caller applications. Shared dependencies retain the native
baseline: Humanizer.Core 2.14.1, System.Text.Json 10.0.13 and the existing
System.Collections.Immutable 10.0.10 floor for .NET Standard. No source is
downloaded at build time. The provenance regeneration tool is manual and requires
already captured originals for all three source sets.

`provenance/verify.py` is the offline CI audit. It verifies the exact captured
output hashes, reconstructs every original C# input by reversing only the
permitted transformations, and checks its original SHA-256 and Git blob identity.
It takes an optional project-root argument and needs no scratch paths or network.
The solution includes this project explicitly. The main formatter excludes only
the three pinned C# source directories; authored aliases, integration code and
tests remain subject to the ordinary style rules. Resources and license bytes
are also checked by the audit. No compiler or API warning is disabled.

Updates must preserve the original-input hash audit, two-guard semantic scope,
local standard-draft behavior, exact numeric wrappers, API checks, complete
package/satellite closure and separate official-vendor coexistence probes on each
supported target family. Replace this source only when a compatible, acceptably
licensed official dependency passes those gates. No version 9 license,
collectible-load-context, unload, AOT or trimming guarantee is implied.
