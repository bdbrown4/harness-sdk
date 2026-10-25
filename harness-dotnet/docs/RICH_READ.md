# Rich file reads

`read` returns typed image or document blocks for PNG, JPG/JPEG, GIF, WebP,
PDF, DOC/DOCX and XLS/XLSX paths, matched without regard to extension case.
It preserves bytes, including a BOM, without validating or parsing the file.
The model provider remains responsible for accepting that media format.
CSV, HTML, TXT, Markdown and video paths keep the existing UTF-8 text route.
Text defaults to 2,000 numbered lines and supports `offset`/`limit` paging.
Media payloads are not paged. NativeIntegral accepts nullable exact integer
fields and source-style zero, negative and large slice windows. ReadConfig
can explicitly select Python/Pydantic2.5 coercion or TypeScript numeric
slicing/labels; those explicit profiles validate fields before media I/O.
The default native media route keeps its earlier paging independence. See
[Read paging profiles](READ_PAGING_PROFILES.md) for precise contracts.

`FileTools.CreateRead(ISandbox)` retains its existing signature and assumes
media support. The new overload accepts `ReadConfig`. An `IRichTool` caller
gets typed content and the requested tool-use identifier. The legacy `ITool`
string method returns only textual content; it cannot represent media blocks.
The ordinary Agent rich-tool pipeline preserves media in tool results, history,
snapshots and native FileSessionStore sessions.

The harness uses `HarnessOptions.Read.Media`, or portable
`builtinTools.read.media`, when explicitly supplied. Otherwise it mirrors
the pinned `supportsMedia` policy: a Bedrock `openai.*` family disables media,
after stripping one of `global.`, `apac.`, `us.`, `eu.`, `au.`, `jp.`; other
families and unknown borrowed models are presumed capable. A RoutingModel
requires every candidate, including nested routers, to be capable. A resolved
string identifier remains available as provenance for an application model
factory. Explicit core models take precedence over convenience identifiers.
The standard subagent rebinds this policy to its actual resolved model while
retaining its accepted read tool, selected filesystem and explicit override.
Application-supplied read replacements retain their original instance and
standalone presentation settings when delegated; only the harness's built-in
reader is rebound, matching the upstream consumer/built-in distinction.

Disabled media reads still read the selected file's bytes and return exactly:

```
[Image file: PATH (FORMAT, SIZE bytes). This model cannot view image files, so its contents are not shown. Use shell tools if the file has a text-extractable form.]
```

Documents use `Document`/`document` in the corresponding positions. JPG uses
`jpeg` as FORMAT. Document names replace characters outside ASCII letters,
digits, whitespace, hyphens, parentheses and square brackets with spaces,
collapse whitespace and trim, with `document` as the empty-name fallback.

Unbounded binary reads use the selected `IFileSystem`; an explicitly bounded
read requires only `IBoundedFileReader` on the selected read-only filesystem,
without requiring byte-write/delete authority. A text-only custom sandbox fails
clearly for recognized media paths. An explicit AgentOptions.FileSystem wins
over the sandbox's byte reader. There is no fallback to the host filesystem.
The default local byte reader retains its existing rejection of symbolic links
and reparse points. This policy does not provide OS isolation or eliminate
filesystem races.

Both pinned local backends read entire media files without a byte limit. The
native default is therefore `MaximumMediaBytes = null`, independently of the
existing 5 MiB local text limit. Applications can set a positive native-only
`ReadConfig.MaximumMediaBytes`; this requires `IBoundedFileReader` on the
selected byte filesystem. The local implementation checks size before reading,
checks growth while reading, and throws instead of truncating. A custom backend
without that capability fails before an unbounded read. Generic storage byte
reads remain unbounded. The limit bounds file bytes, not total managed heap
overhead. No new portable JSON field is introduced for this native policy.

Source anchors at pinned upstream commit
`3bd98e618320f0f286acbfb2252f72388030421b`:

- `harness-ts/src/tools/file-tools.ts:17-24,48-109` and
  `harness-py/src/strands_harness/tools/file_tools.py:19-24,48-114`:
  formats, placeholder, sanitization and media-before-text routing.
- `harness-ts/src/models.ts:501-526` and
  `harness-py/src/strands_harness/models.py:401-425`: media defaults.
- `harness-ts/src/builtin-tools.ts:164` and `agent.ts:400-436`:
  explicit setting and unresolved child configuration.
- `strands-ts/src/sandbox/not-a-sandbox-local-environment.ts:63-64` and
  `strands-py/src/strands/sandbox/not_a_sandbox_local_environment.py:126-140`:
  unbounded binary file reads.

Protocol projection tests inspect actual serialized requests for OpenAI Chat,
Responses, Anthropic and Google through local HTTP fixtures. They establish
byte projection and lifetime behavior without asserting live provider acceptance.
