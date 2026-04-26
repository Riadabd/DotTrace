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
  --symbol Your.Namespace.EntryPoint.Run(System.String[]) \
  --format text
```

Write HTML output to a file:

```bash
dotnet run --project src/DotTrace.Cli -- tree --db trace.db \
  --symbol Your.Namespace.EntryPoint.Run(System.String[]) \
  --format html \
  --out trace.html
```

## CLI

```text
dottrace cache build <path-to-sln-or-csproj> --db <path-to-cache.db>

dottrace cache list --db <path-to-cache.db>

dottrace tree --db <path-to-cache.db> --symbol <fully-qualified-method-signature>
  [--snapshot <id>]
  [--max-depth <n>]
  [--format text|html]
  [--out <path>]
  [--no-color]
```

Example root symbols:

- `MyCompany.App.Program.Main(System.String[])`
- `MyCompany.Core.OrderService.Submit(MyCompany.Core.OrderRequest)`

## Cache Behavior

- Every successful `cache build` creates a new complete snapshot in the SQLite DB
- `tree` reads the active snapshot by default
- `tree --snapshot <id>` renders an older snapshot
- Historical snapshots are retained until a future pruning command exists

## Inspecting the SQLite Cache

The cache is an ordinary SQLite database. The examples below require the `sqlite3` CLI and read from the active snapshot through `cache_state.active_snapshot_id`; replace `dottrace.db` with the path passed to `--db`.

If your shell shows continuation prompts such as Nu's `:::`, do not paste those prompt markers as part of the command.

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

HTML output uses the same node labels as text output:

| Label | Meaning |
| --- | --- |
| `source` | A method resolved to source code in the analyzed solution or project. Source nodes expand recursively. |
| `external` | Roslyn resolved the call, but the callee is outside the analyzed source, such as framework or package code. External nodes render as leaves. |
| `cycle` | The callee is already in the current recursion path, such as `A -> B -> A`. Cycle nodes render as leaves to avoid infinite expansion. |
| `seen` | The source method was already expanded elsewhere in the rendered tree, but is not in the current recursion path. This marks shared or repeated calls without expanding the same method again. |
| `max-depth` | Expansion stopped because `--max-depth` was reached. The method may still have calls below it, but they were intentionally omitted from this render. |
| `unresolved` | The call syntax was found, but Roslyn could not resolve it to a method symbol, so DotTrace renders the raw call text as a leaf. |
