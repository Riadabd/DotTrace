using Microsoft.Data.Sqlite;
using DotTrace.Core.Analysis;

namespace DotTrace.Core.Persistence;

internal static class SqliteSchema
{
    public const int CurrentVersion = 1;

    public static async Task EnsureCreatedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS snapshots (
              id INTEGER PRIMARY KEY,
              input_path TEXT NOT NULL,
              workspace_fingerprint TEXT NOT NULL,
              tool_version TEXT NOT NULL,
              created_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS cache_state (
              id INTEGER PRIMARY KEY CHECK (id = 1),
              active_snapshot_id INTEGER REFERENCES snapshots(id)
            );

            CREATE TABLE IF NOT EXISTS projects (
              id INTEGER PRIMARY KEY,
              snapshot_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
              stable_id TEXT NOT NULL,
              name TEXT NOT NULL,
              assembly_name TEXT NOT NULL,
              file_path TEXT NOT NULL,
              UNIQUE (snapshot_id, stable_id),
              UNIQUE (snapshot_id, id)
            );

            CREATE TABLE IF NOT EXISTS symbols (
              id INTEGER PRIMARY KEY,
              snapshot_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
              project_id INTEGER,
              stable_id TEXT NOT NULL,
              qualified_name TEXT NOT NULL,
              signature_text TEXT NOT NULL,
              normalized_qualified_name TEXT NOT NULL,
              normalized_signature_text TEXT NOT NULL,
              origin_kind TEXT NOT NULL,
              file_path TEXT,
              line INTEGER,
              column INTEGER,
              UNIQUE (snapshot_id, stable_id),
              UNIQUE (snapshot_id, id),
              FOREIGN KEY (snapshot_id, project_id) REFERENCES projects(snapshot_id, id)
            );

            CREATE TABLE IF NOT EXISTS calls (
              id INTEGER PRIMARY KEY,
              snapshot_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
              caller_symbol_id INTEGER NOT NULL,
              callee_symbol_id INTEGER,
              call_text TEXT NOT NULL,
              file_path TEXT,
              line INTEGER,
              column INTEGER,
              ordinal INTEGER NOT NULL,
              FOREIGN KEY (snapshot_id, caller_symbol_id) REFERENCES symbols(snapshot_id, id),
              FOREIGN KEY (snapshot_id, callee_symbol_id) REFERENCES symbols(snapshot_id, id)
            );

            CREATE TABLE IF NOT EXISTS diagnostics (
              id INTEGER PRIMARY KEY,
              snapshot_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
              message TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_projects_snapshot_stable
              ON projects(snapshot_id, stable_id);

            CREATE INDEX IF NOT EXISTS ix_symbols_snapshot_stable
              ON symbols(snapshot_id, stable_id);

            CREATE INDEX IF NOT EXISTS ix_symbols_snapshot_normalized_qualified
              ON symbols(snapshot_id, normalized_qualified_name);

            CREATE INDEX IF NOT EXISTS ix_symbols_snapshot_normalized_signature
              ON symbols(snapshot_id, normalized_signature_text);

            CREATE INDEX IF NOT EXISTS ix_calls_snapshot_caller_ordinal
              ON calls(snapshot_id, caller_symbol_id, ordinal);

            CREATE INDEX IF NOT EXISTS ix_calls_snapshot_callee
              ON calls(snapshot_id, callee_symbol_id);
            """,
            cancellationToken);

        await ExecuteNonQueryAsync(connection, $"PRAGMA user_version = {CurrentVersion};", cancellationToken);
        await ExecuteNonQueryAsync(connection, "INSERT OR IGNORE INTO cache_state(id, active_snapshot_id) VALUES (1, NULL);", cancellationToken);
    }

    public static async Task ValidateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var version = await ReadUserVersionAsync(connection, cancellationToken);
        if (version != CurrentVersion)
        {
            throw new DotTraceException($"Unsupported SQLite cache schema version {version}. Expected version {CurrentVersion}.");
        }
    }

    private static async Task<int> ReadUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
