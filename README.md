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

## Output Behavior

- Source methods expand recursively
- External framework or package calls render as marked leaves
- Repeated nodes and recursion render as marked leaves instead of expanding forever
- `--max-depth` truncates deeper expansion with an explicit marker
