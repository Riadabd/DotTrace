# DotTrace

`DotTrace` is a Roslyn-powered static call-tree explorer for C# solutions and projects. It builds a SQLite-backed call-graph cache from a `.sln` or `.csproj`, projects a chosen method symbol from a cached snapshot, and emits either:

- a Unicode tree for terminals, logs, markdown, or plain files
- a minimal HTML document with color-coded nodes plus browser-friendly scrolling and zoom controls

## Scope

This rewrite is intentionally static-only in `v1`.

- No runtime argument or return-value capture yet
- No deobfuscation pipeline yet
- No large SVG graph rendering
  - Future work may target diagram rendering based on the results of the current approach.

Instead, the tool resolves source method calls into a persistent cache and renders compact trees from cached snapshots.

## Requirements

- .NET SDK `10.0.100` or later in the `10.0` feature band
- For Nix users: `nix develop`

The repository pins the SDK line with `global.json`, following Microsoft's `global.json` guidance:
https://learn.microsoft.com/en-us/dotnet/core/tools/global-json

## Quick Start

```bash
nix develop
dotnet restore
dotnet build
dotnet run --project src/DotTrace.Cli -- cache build ./YourSolution.sln --db trace.db
dotnet run --project src/DotTrace.Cli -- tree --db trace.db \
  --symbol 'Your.Namespace.EntryPoint.Run(System.String[])' \
  --format text
```

Write HTML output to a file:

```bash
dotnet run --project src/DotTrace.Cli -- tree --db trace.db \
  --symbol 'Your.Namespace.EntryPoint.Run(System.String[])' \
  --format html \
  --out trace.html
```

## CLI

The synopsis below uses `dottrace` for the built or published executable. During local development, replace `dottrace` with:

```bash
dotnet run --project src/DotTrace.Cli --
```

To create an executable output directory:

```bash
dotnet publish src/DotTrace.Cli -c Release -o ./artifacts/dottrace ./artifacts/dottrace/dottrace --help
```

```text
dottrace cache build <path-to-sln-or-csproj> --db <path-to-cache.db>

dottrace cache list --db <path-to-cache.db>

dottrace tree --db <path-to-cache.db> --symbol <fully-qualified-method-signature>
  [--snapshot <id>]
  [--max-depth <n>]
  [--view callees|callers|both]
  [--format text|html]
  [--out <path>]
  [--no-color]
```

Example root symbols:

- `MyCompany.App.Program.Main(System.String[])`
- `MyCompany.Core.OrderService.Submit(MyCompany.Core.OrderRequest)`

Quote `--symbol` values in zsh, bash, and similar shells because method signatures contain parentheses:

```bash
dottrace tree --db trace.db --symbol 'MyCompany.App.Program.Main(System.String[])'
```

## Symbol Selection

`--symbol` selects a source method from the requested snapshot. External symbols can appear in rendered trees, but they cannot be used as tree roots.

- A selector with parentheses is matched as a full method signature against `symbols.signature_text`.
- A selector without parentheses is matched as a fully qualified method name against `symbols.qualified_name`.
- Whitespace inside signatures is ignored for matching, so `Foo(System.String, System.Int32)` and `Foo(System.String,System.Int32)` are equivalent.
- If a selector matches more than one source method, DotTrace reports the ambiguity and lists the candidate signatures. Use one of those full signatures.
- If a selector matches nothing, inspect the active snapshot with `cache list` and the `sqlite3` examples below, then rebuild the cache if the code changed.

## Cache Behavior

- Every successful `cache build` creates a new complete snapshot in the SQLite DB
- `tree` reads the active snapshot by default
- `tree --snapshot <id>` renders an older snapshot
- Historical snapshots are retained until a future pruning command exists

`cache list` prints tab-separated snapshot metadata:

| Column | Meaning |
| --- | --- |
| `active` | `*` for the snapshot used by default when `tree` runs without `--snapshot`. |
| `id` | Snapshot id for `tree --snapshot <id>` and direct SQLite queries. |
| `created_utc` | Snapshot creation time in UTC. |
| `tool_version` | DotTrace assembly version that produced the snapshot. |
| `input_path` | The `.sln` or `.csproj` path passed to `cache build`. |
| `workspace_fingerprint` | Hash of the input file, project files, and source documents used to detect exactly what was cached. |

## Inspecting the SQLite Cache

The cache is an ordinary SQLite database. The examples below require the `sqlite3` CLI and read from the active snapshot through `cache_state.active_snapshot_id`; replace `dottrace.db` with the path passed to `--db`.

If your shell shows continuation prompts such as Nu's `:::`, do not paste those prompt markers as part of the command.

The current cache schema is intended to be inspectable. Prefer DotTrace commands for workflows that need to survive future schema changes.

| Table | Purpose | Important columns |
| --- | --- | --- |
| `snapshots` | One complete cache build. | `id`, `input_path`, `workspace_fingerprint`, `tool_version`, `created_utc` |
| `cache_state` | Singleton table for the default snapshot. | `active_snapshot_id` |
| `projects` | Projects loaded for a snapshot. | `snapshot_id`, `stable_id`, `name`, `assembly_name`, `file_path` |
| `symbols` | Source and external methods. | `snapshot_id`, `project_id`, `stable_id`, `qualified_name`, `signature_text`, `origin_kind`, `file_path`, `line`, `column` |
| `calls` | Direct call edges from one source symbol to another symbol or unresolved call text. | `snapshot_id`, `caller_symbol_id`, `callee_symbol_id`, `call_text`, `file_path`, `line`, `column`, `ordinal` |
| `diagnostics` | MSBuildWorkspace diagnostics captured during cache build. | `snapshot_id`, `message` |

All graph tables are scoped by `snapshot_id`. When joining `calls` to `symbols`, join on both `snapshot_id` and symbol id. `calls.callee_symbol_id` is nullable; `NULL` means DotTrace recorded the call syntax but Roslyn did not resolve a callee method symbol.

Find symbols to use with `--symbol`:

```bash
sqlite3 -header -column dottrace.db "
SELECT s.signature_text
FROM symbols AS s
JOIN cache_state AS cs ON s.snapshot_id = cs.active_snapshot_id
WHERE s.signature_text LIKE '%CreateTreeCommand%'
ORDER BY s.signature_text;
"
```

Inspect direct calls from one source symbol:

```bash
sqlite3 -header -column dottrace.db "
SELECT
  c.ordinal,
  c.call_text,
  COALESCE(callee.signature_text, '[unresolved]') AS callee,
  c.file_path,
  c.line,
  c.column
FROM calls AS c
JOIN cache_state AS cs ON c.snapshot_id = cs.active_snapshot_id
JOIN symbols AS caller
  ON caller.snapshot_id = c.snapshot_id
 AND caller.id = c.caller_symbol_id
LEFT JOIN symbols AS callee
  ON callee.snapshot_id = c.snapshot_id
 AND callee.id = c.callee_symbol_id
WHERE caller.signature_text = 'ProgramEntry.CreateTreeCommand()'
ORDER BY c.ordinal;
"
```

List unresolved call sites:

```bash
sqlite3 -header -column dottrace.db "
SELECT
  caller.signature_text AS caller,
  c.call_text,
  c.file_path,
  c.line,
  c.column
FROM calls AS c
JOIN cache_state AS cs ON c.snapshot_id = cs.active_snapshot_id
JOIN symbols AS caller
  ON caller.snapshot_id = c.snapshot_id
 AND caller.id = c.caller_symbol_id
WHERE c.callee_symbol_id IS NULL
ORDER BY caller.signature_text, c.ordinal;
"
```

## Output Behavior

- Source methods expand recursively
- External framework or package calls render as marked leaves
- Repeated nodes and recursion render as marked leaves instead of expanding forever
- `--max-depth` truncates deeper expansion with an explicit marker
- `--view callees` is the default downstream call tree
- `--view callers` renders recursive callers for the selected method
- `--view both` renders caller paths with a duplicated highlighted target tree row and callees nested under each target; HTML includes that full view as an extra tab alongside the separate callers and callees tabs

## Call Model

DotTrace builds a static call graph from Roslyn symbols. It records direct calls from executable source declarations, then projects a tree from the cached graph.

Captured source declarations include methods, constructors, destructors, operators, conversion operators, property/indexer accessors, and local functions with bodies.

Captured direct call sites include:

- method invocations
- object creation and implicit object creation
- constructor initializers
- property and indexer getter/setter accesses

Current limitations:

- The program is not executed, so runtime argument values, return values, call counts, and timing are not available.
- Dynamic dispatch is not expanded into every possible runtime implementation.
- Reflection, dependency injection wiring, event subscription effects, callbacks, and generated runtime behavior are not inferred beyond what Roslyn resolves at the call site.
- Calls inside lambda and anonymous method bodies are not currently folded into the containing method's direct-call list.

HTML output uses the same node labels as text output:

| Label | Meaning |
| --- | --- |
| `source` | A method resolved to source code in the analyzed solution or project. Source nodes expand recursively. |
| `external` | Roslyn resolved the call, but the callee is outside the analyzed source, such as framework or package code. External nodes render as leaves. |
| `cycle` | The callee is already in the current recursion path, such as `A -> B -> A`. Cycle nodes render as leaves to avoid infinite expansion. |
| `seen` | The source method was already expanded elsewhere in the rendered tree, but is not in the current recursion path. This marks shared or repeated calls without expanding the same method again. |
| `max-depth` | Expansion stopped because `--max-depth` was reached. The method may still have calls below it, but they were intentionally omitted from this render. |
| `unresolved` | The call syntax was found, but Roslyn could not resolve it to a method symbol, so DotTrace renders the raw call text as a leaf. |

## Troubleshooting

- `zsh: no matches found` or another shell parse error near `(` usually means the `--symbol` value was not quoted.
- `No method matched symbol ...` means no source symbol in the selected snapshot matched the selector. Run `cache list`, inspect `symbols.signature_text`, check whether you need `--snapshot`, and rebuild the cache if the source changed.
- `The symbol ... is ambiguous` means the selector matched multiple overloads or methods. Use one of the full signatures printed in the error.
- `Only .sln and .csproj inputs are supported.` means `cache build` was given another file type or a directory.
- `SQLite cache does not exist` or `SQLite cache does not have an active snapshot` means the selected `--db` path has not been built yet. Run `cache build` with that same `--db` path.
- `warning:` lines during `cache build` are MSBuildWorkspace diagnostics. If expected symbols are missing, restore/build the target solution first and make sure the SDK, workloads, and project references load cleanly.
- `Unsupported SQLite cache schema version ...` means the database was created by a different cache schema. Rebuild the cache with the current DotTrace version.

## Development Validation

Use the Nix shell when relying on the repo-provided toolchain:

```bash
nix develop
```

Before handing off code changes, run:

```bash
dotnet restore
dotnet build
dotnet test
```

For docs-only changes, read back the rendered Markdown section or diff and run any included SQL examples that were changed.
