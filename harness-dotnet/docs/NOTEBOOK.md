# Optional state-backed notebooks

`Strands.Harness.Tools.Notebook.Create(config)` returns an ordinary `ITool`.
Add it to `AgentOptions.Tools` or `HarnessOptions.Tools`. It is optional and does
not change the eight default Harness tools. Core consumers can use the factory
by referencing the Harness package, as with the other optional vended tools.

```csharp
var notebook = Notebook.Create(new() { MaximumNotebookSizeBytes = 1_048_576 });
var options = new HarnessOptions { Tools = [notebook] };
```

The tool uses the current Agent's `State["notebooks"]`, a JSON object of notebook
names to strings. There is no tool-local notebook database or filesystem path.
Create overwrites; list reports line counts; read returns text or an inclusive
one-based line range; write appends, replaces the first literal match, or inserts
after a line; clear empties an existing notebook. A fresh empty map has a virtual
`default` notebook. Read and list do not persist that initialization.

The default cap is one MiB of UTF8 content per notebook. Create/write calculate a
detached candidate and check the selected notebook before committing a single
state write. An oversized candidate leaves existing state unchanged. Read and
clear do not enforce the growth cap, and an oversized unrelated notebook does
not prevent a write to another notebook. The cap is not a total-state limit.

Default `TypeScript` behavior accepts `newStr`, `oldStr`, `insertLine`, and
`readRange`; optional fields accept omission, not JSON null. Explicit `Python`
behavior uses `new_str`, `old_str`, `insert_line`, and `read_range`, with null
treated as absent. Python ranges contain exactly two entries; TypeScript ranges
use the first two and permit additional integral entries. Both native schemas
advertise integer line indices: finite integral JSON numbers are required, without
string/bool coercion or fractional-index emulation from JavaScript/Pydantic.

Negative read indices count from the end; endpoints are clamped before iteration.
Insertion anchor zero prepends, one inserts after the first line, and minus one
appends. A string anchor searches the first containing line, including numeric
looking strings. The source's accepted end anchor is clamped to the end; its
reported insertion line is preserved. Replacement changes only the first literal
occurrence and preserves dollar-sign text. Append adds a separator only when
nonempty existing text lacks a trailing newline; empty appended text is a no-op.
Existing newlines and empty notebook names remain literal.

Names are ordinal, case-sensitive JSON keys. TypeScript list projection places
canonical integer-index names first in numeric order; Python retains map insertion
order. Native keys have no JavaScript prototype behavior. Malformed state or
duplicate JSON keys are rejected before mutation.

The pinned factories measure UTF8 bytes. Valid Unicode, including astral text,
is supported. Public `System.Text.Json` 10.0.13 characterization found that reading
an escaped lone surrogate throws, while serializing a directly constructed
invalid .NET string replaces it. This transport boundary is common to the native
profiles; they do not claim TypeScript replacement/Python strict behavior for
malformed surrogate strings. Native Python byte counting remains strict for
strings that reach it.

Notebook instances privately serialize their own read/compute/write operations
on each borrowed StateStore through a weak gate. This is not an atomic transaction
with arbitrary application writes to StateStore. No callback, I/O, await, or
resource disposal runs inside that gate. The explicit caller token and captured
tool signal are checked before work and state commit. The tool never disposes
the Agent, its state, or its model and owns no persistent resource.

State snapshot/session persistence follows the existing Agent configuration.
The notebook tool does not write a session file itself, automatically inject
content into model context, or grant filesystem authority. A direct tool call
alone does not trigger an independent disk save.
