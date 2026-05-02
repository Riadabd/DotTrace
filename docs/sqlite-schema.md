# SQLite Cache Schema

This documents SQLite cache schema version `1`, created by `src/DotTrace.Core/Persistence/SqliteSchema.cs`.

Treat `SqliteSchema` as the source of truth. When changing the DDL, `PRAGMA user_version`, or cache persistence behavior, update this file in the same change.

## Lifecycle

- `SqliteGraphCache` opens SQLite connections with foreign keys enabled.
- `SqliteSchema.EnsureCreatedAsync` creates all tables and indexes, sets `PRAGMA user_version = 1`, and inserts the singleton `cache_state` row if it is missing.
- `cache build` writes one complete snapshot inside a transaction, then points `cache_state.active_snapshot_id` at the new snapshot.
- `tree --db <path>` reads `cache_state.active_snapshot_id` by default. `tree --snapshot <id>` reads a specific historical snapshot.

## Tables

### `snapshots`

One row per complete cache build.

| Column | Type | Nullable | Meaning |
| --- | --- | --- | --- |
| `id` | `INTEGER PRIMARY KEY` | No | Snapshot id used by `tree --snapshot <id>` and direct queries. |
| `input_path` | `TEXT` | No | The `.sln` or `.csproj` path passed to `cache build`. |
| `workspace_fingerprint` | `TEXT` | No | Hash of the input file, project files, and source documents used to identify the cached workspace contents. |
| `tool_version` | `TEXT` | No | DotTrace assembly version that produced the snapshot. |
| `created_utc` | `TEXT` | No | UTC creation time written with .NET round-trip date/time formatting. |

### `cache_state`

Singleton table for the default snapshot.

| Column | Type | Nullable | Meaning |
| --- | --- | --- | --- |
| `id` | `INTEGER PRIMARY KEY CHECK (id = 1)` | No | Enforces exactly one logical cache-state row. |
| `active_snapshot_id` | `INTEGER REFERENCES snapshots(id)` | Yes | Default snapshot used by `tree` when `--snapshot` is not supplied. `NULL` means the cache exists but no snapshot has been built yet. |

### `projects`

Projects loaded for one snapshot.

| Column | Type | Nullable | Meaning |
| --- | --- | --- | --- |
| `id` | `INTEGER PRIMARY KEY` | No | Database-local project row id. |
| `snapshot_id` | `INTEGER REFERENCES snapshots(id) ON DELETE CASCADE` | No | Snapshot that owns this project row. |
| `stable_id` | `TEXT` | No | Stable project identity generated from assembly name and project path. |
| `name` | `TEXT` | No | Roslyn project name. |
| `assembly_name` | `TEXT` | No | Roslyn project assembly name. |
| `file_path` | `TEXT` | No | Project file path. |

Constraints:

- `UNIQUE (snapshot_id, stable_id)` prevents duplicate project identities within one snapshot.
- `UNIQUE (snapshot_id, id)` supports composite foreign keys from other snapshot-scoped tables.

### `symbols`

Source and external method symbols for one snapshot.

| Column | Type | Nullable | Meaning |
| --- | --- | --- | --- |
| `id` | `INTEGER PRIMARY KEY` | No | Database-local symbol row id. |
| `snapshot_id` | `INTEGER REFERENCES snapshots(id) ON DELETE CASCADE` | No | Snapshot that owns this symbol row. |
| `project_id` | `INTEGER` | Yes | Owning `projects.id` for source symbols. Usually `NULL` for external symbols. |
| `stable_id` | `TEXT` | No | Stable symbol identity used internally to connect projects, symbols, and calls before rows are inserted. |
| `qualified_name` | `TEXT` | No | Human-readable qualified method name. |
| `signature_text` | `TEXT` | No | Human-readable method signature, used for display and exact `--symbol` selection. |
| `normalized_qualified_name` | `TEXT` | No | Normalized qualified name used for selector matching. |
| `normalized_signature_text` | `TEXT` | No | Normalized signature used for selector matching. |
| `origin_kind` | `TEXT` | No | Symbol origin. Current values are `source` and `external`. |
| `file_path` | `TEXT` | Yes | Source file path for source symbols with locations. |
| `line` | `INTEGER` | Yes | 1-based source line for symbols with locations. |
| `column` | `INTEGER` | Yes | 1-based source column for symbols with locations. |

Constraints:

- `UNIQUE (snapshot_id, stable_id)` prevents duplicate symbol identities within one snapshot.
- `UNIQUE (snapshot_id, id)` supports composite foreign keys from `calls`.
- `FOREIGN KEY (snapshot_id, project_id) REFERENCES projects(snapshot_id, id)` keeps project references inside the same snapshot.

### `calls`

Direct call edges from one caller symbol to another resolved callee symbol, or to unresolved call text.

| Column | Type | Nullable | Meaning |
| --- | --- | --- | --- |
| `id` | `INTEGER PRIMARY KEY` | No | Database-local call row id. |
| `snapshot_id` | `INTEGER REFERENCES snapshots(id) ON DELETE CASCADE` | No | Snapshot that owns this call row. |
| `caller_symbol_id` | `INTEGER` | No | Calling `symbols.id`. |
| `callee_symbol_id` | `INTEGER` | Yes | Resolved called `symbols.id`. `NULL` means Roslyn did not resolve the call target. |
| `call_text` | `TEXT` | No | Per-call display text captured from the call site. |
| `file_path` | `TEXT` | Yes | Source file path for call sites with locations. |
| `line` | `INTEGER` | Yes | 1-based source line for call sites with locations. |
| `column` | `INTEGER` | Yes | 1-based source column for call sites with locations. |
| `ordinal` | `INTEGER` | No | Caller-local call order used for stable rendering. |

Constraints:

- `FOREIGN KEY (snapshot_id, caller_symbol_id) REFERENCES symbols(snapshot_id, id)` keeps caller references inside the same snapshot.
- `FOREIGN KEY (snapshot_id, callee_symbol_id) REFERENCES symbols(snapshot_id, id)` keeps resolved callee references inside the same snapshot.

### `diagnostics`

MSBuildWorkspace diagnostics captured while building a snapshot.

| Column | Type | Nullable | Meaning |
| --- | --- | --- | --- |
| `id` | `INTEGER PRIMARY KEY` | No | Database-local diagnostic row id. |
| `snapshot_id` | `INTEGER REFERENCES snapshots(id) ON DELETE CASCADE` | No | Snapshot that produced this diagnostic. |
| `message` | `TEXT` | No | Diagnostic message text. Duplicate messages are removed before insert. |

## Indexes

| Index | Table | Columns | Purpose |
| --- | --- | --- | --- |
| `ix_projects_snapshot_stable` | `projects` | `snapshot_id`, `stable_id` | Lookup projects by stable identity inside a snapshot. |
| `ix_symbols_snapshot_stable` | `symbols` | `snapshot_id`, `stable_id` | Lookup symbols by stable identity inside a snapshot. |
| `ix_symbols_snapshot_normalized_qualified` | `symbols` | `snapshot_id`, `normalized_qualified_name` | Resolve `--symbol` selectors by normalized qualified name. |
| `ix_symbols_snapshot_normalized_signature` | `symbols` | `snapshot_id`, `normalized_signature_text` | Resolve `--symbol` selectors by normalized signature. |
| `ix_calls_snapshot_caller_ordinal` | `calls` | `snapshot_id`, `caller_symbol_id`, `ordinal` | Load outgoing calls in caller render order. |
| `ix_calls_snapshot_callee` | `calls` | `snapshot_id`, `callee_symbol_id` | Load incoming calls for callers/callees views. |

## Join Rules

- All graph tables are scoped by `snapshot_id`. Do not join `projects`, `symbols`, or `calls` by row id alone.
- Join `calls.caller_symbol_id` to `symbols.id` with both `snapshot_id` and `id`.
- Join `calls.callee_symbol_id` to `symbols.id` with both `snapshot_id` and `id`, using a left join because unresolved calls store `NULL`.
- Join `symbols.project_id` to `projects.id` with both `snapshot_id` and `id`, using a left join because external symbols may not have a project.
- Deleting a `snapshots` row cascades to `projects`, `symbols`, `calls`, and `diagnostics`. The current CLI keeps historical snapshots; pruning is future work.
