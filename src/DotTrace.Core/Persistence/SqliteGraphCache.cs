using System.Globalization;
using DotTrace.Core.Analysis;
using Microsoft.Data.Sqlite;

namespace DotTrace.Core.Persistence;

public sealed class SqliteGraphCache
{
    public async Task<long> WriteSnapshotAsync(
        string dbPath,
        CallGraphBuildResult buildResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(buildResult);

        var fullPath = Path.GetFullPath(dbPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = CreateConnection(fullPath, SqliteOpenMode.ReadWriteCreate);
        await connection.OpenAsync(cancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, cancellationToken);

        using var transaction = connection.BeginTransaction();
        try
        {
            var snapshotId = await InsertSnapshotAsync(connection, transaction, buildResult, cancellationToken);
            var projectIds = await InsertProjectsAsync(connection, transaction, snapshotId, buildResult.Projects, cancellationToken);
            var symbolIds = await InsertSymbolsAsync(connection, transaction, snapshotId, buildResult.Symbols, projectIds, cancellationToken);
            await InsertRootSymbolsAsync(connection, transaction, snapshotId, buildResult.RootSymbols, projectIds, symbolIds, cancellationToken);
            await InsertCallsAsync(connection, transaction, snapshotId, buildResult.Calls, symbolIds, cancellationToken);
            await InsertDiagnosticsAsync(connection, transaction, snapshotId, buildResult.Diagnostics, cancellationToken);
            await SetActiveSnapshotAsync(connection, transaction, snapshotId, cancellationToken);
            transaction.Commit();
            return snapshotId;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<IReadOnlyList<CallGraphSnapshotInfo>> ListSnapshotsAsync(
        string dbPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        var fullPath = Path.GetFullPath(dbPath);
        if (!File.Exists(fullPath))
        {
            throw new DotTraceException($"SQLite cache does not exist: {fullPath}");
        }

        await using var connection = CreateConnection(fullPath, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken);
        await SqliteSchema.ValidateAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              s.id,
              s.input_path,
              s.workspace_fingerprint,
              s.tool_version,
              s.created_utc,
              CASE WHEN s.id = cs.active_snapshot_id THEN 1 ELSE 0 END AS is_active
            FROM snapshots AS s
            CROSS JOIN cache_state AS cs
            ORDER BY s.id DESC;
            """;

        var snapshots = new List<CallGraphSnapshotInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            snapshots.Add(new CallGraphSnapshotInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.GetInt32(5) == 1));
        }

        return snapshots;
    }

    public async Task<IReadOnlyList<CallGraphProjectInfo>> ListProjectsAsync(
        string dbPath,
        long? snapshotId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReadOnlyValidatedAsync(dbPath, cancellationToken);
        var selectedSnapshotId = await ResolveSnapshotIdAsync(connection, snapshotId, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              p.id,
              p.snapshot_id,
              p.name,
              p.assembly_name,
              p.file_path,
              (
                SELECT COUNT(*)
                FROM symbols AS s
                WHERE s.snapshot_id = p.snapshot_id
                  AND s.project_id = p.id
                  AND s.origin_kind = 'source'
              ) AS source_symbol_count,
              (
                SELECT COUNT(*)
                FROM root_symbols AS r
                WHERE r.snapshot_id = p.snapshot_id
                  AND r.project_id = p.id
              ) AS root_symbol_count,
              (
                SELECT COUNT(*)
                FROM calls AS c
                JOIN symbols AS caller
                  ON caller.snapshot_id = c.snapshot_id
                 AND caller.id = c.caller_symbol_id
                WHERE c.snapshot_id = p.snapshot_id
                  AND caller.project_id = p.id
              ) AS direct_call_count
            FROM projects AS p
            WHERE p.snapshot_id = $snapshot_id
            ORDER BY p.name, p.assembly_name, p.file_path, p.id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", selectedSnapshotId);

        var projects = new List<CallGraphProjectInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            projects.Add(new CallGraphProjectInfo(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7)));
        }

        return projects;
    }

    public async Task<CallTreeNode> ProjectTreeAsync(
        string dbPath,
        string symbolSelector,
        AnalysisOptions? options = null,
        long? snapshotId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolSelector);

        var projector = await CreateProjectorAsync(dbPath, options, snapshotId, cancellationToken);
        return projector.ProjectCallees(symbolSelector);
    }

    public async Task<CallTreeDocument> ProjectDocumentAsync(
        string dbPath,
        string symbolSelector,
        AnalysisOptions? options = null,
        long? snapshotId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolSelector);

        var projector = await CreateProjectorAsync(dbPath, options, snapshotId, cancellationToken);
        return projector.ProjectDocument(symbolSelector);
    }

    public async Task<CallTreeDocument> ProjectDocumentBySymbolIdAsync(
        string dbPath,
        long symbolId,
        AnalysisOptions? options = null,
        long? snapshotId = null,
        CancellationToken cancellationToken = default)
    {
        if (symbolId <= 0)
        {
            throw new DotTraceException("Symbol id must be a positive integer.");
        }

        var projector = await CreateProjectorAsync(dbPath, options, snapshotId, cancellationToken);
        return projector.ProjectDocument(symbolId);
    }

    public async Task<CallTreeNode> ProjectMapAsync(
        string dbPath,
        long projectId,
        AnalysisOptions? options = null,
        long? snapshotId = null,
        CancellationToken cancellationToken = default)
    {
        if (projectId <= 0)
        {
            throw new DotTraceException("Project id must be a positive integer.");
        }

        var projector = await CreateProjectorAsync(dbPath, options, snapshotId, cancellationToken);
        return projector.ProjectMap(projectId);
    }

    public async Task<IReadOnlyList<CallGraphSymbolInfo>> SearchSymbolsAsync(
        string dbPath,
        string? query = null,
        long? snapshotId = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (page <= 0)
        {
            throw new DotTraceException("Page must be a positive integer.");
        }

        if (pageSize <= 0)
        {
            throw new DotTraceException("Page size must be a positive integer.");
        }

        pageSize = Math.Min(pageSize, 200);

        await using var connection = await OpenReadOnlyValidatedAsync(dbPath, cancellationToken);
        var selectedSnapshotId = await ResolveSnapshotIdAsync(connection, snapshotId, cancellationToken);
        var searchText = query?.Trim();
        var offset = (long)(page - 1) * pageSize;

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              s.id,
              s.snapshot_id,
              s.qualified_name,
              s.signature_text,
              s.origin_kind,
              p.name,
              p.assembly_name,
              p.file_path,
              s.file_path,
              s.line,
              s.column,
              (
                SELECT COUNT(DISTINCT incoming.caller_symbol_id)
                FROM calls AS incoming
                WHERE incoming.snapshot_id = s.snapshot_id
                  AND incoming.callee_symbol_id = s.id
              ) AS direct_caller_count,
              (
                SELECT COUNT(*)
                FROM calls AS outgoing
                WHERE outgoing.snapshot_id = s.snapshot_id
                  AND outgoing.caller_symbol_id = s.id
              ) AS direct_callee_count
            FROM symbols AS s
            LEFT JOIN projects AS p
              ON p.snapshot_id = s.snapshot_id
             AND p.id = s.project_id
            WHERE s.snapshot_id = $snapshot_id
              AND s.origin_kind = 'source'
              AND (
                $query IS NULL
                OR s.qualified_name LIKE $query ESCAPE '\'
                OR s.signature_text LIKE $query ESCAPE '\'
              )
            ORDER BY s.qualified_name, s.signature_text, s.id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$snapshot_id", selectedSnapshotId);
        command.Parameters.AddWithValue("$query", string.IsNullOrWhiteSpace(searchText) ? DBNull.Value : $"%{EscapeLike(searchText)}%");
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", offset);

        var symbols = new List<CallGraphSymbolInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            symbols.Add(ReadSymbolInfo(reader));
        }

        return symbols;
    }

    public async Task<CallGraphSymbolInfo> GetSymbolAsync(
        string dbPath,
        long symbolId,
        long? snapshotId = null,
        CancellationToken cancellationToken = default)
    {
        if (symbolId <= 0)
        {
            throw new DotTraceException("Symbol id must be a positive integer.");
        }

        await using var connection = await OpenReadOnlyValidatedAsync(dbPath, cancellationToken);
        var selectedSnapshotId = await ResolveSnapshotIdAsync(connection, snapshotId, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              s.id,
              s.snapshot_id,
              s.qualified_name,
              s.signature_text,
              s.origin_kind,
              p.name,
              p.assembly_name,
              p.file_path,
              s.file_path,
              s.line,
              s.column,
              (
                SELECT COUNT(DISTINCT incoming.caller_symbol_id)
                FROM calls AS incoming
                WHERE incoming.snapshot_id = s.snapshot_id
                  AND incoming.callee_symbol_id = s.id
              ) AS direct_caller_count,
              (
                SELECT COUNT(*)
                FROM calls AS outgoing
                WHERE outgoing.snapshot_id = s.snapshot_id
                  AND outgoing.caller_symbol_id = s.id
              ) AS direct_callee_count
            FROM symbols AS s
            LEFT JOIN projects AS p
              ON p.snapshot_id = s.snapshot_id
             AND p.id = s.project_id
            WHERE s.snapshot_id = $snapshot_id
              AND s.id = $symbol_id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", selectedSnapshotId);
        command.Parameters.AddWithValue("$symbol_id", symbolId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new DotTraceException($"SQLite cache symbol {symbolId} does not exist in snapshot {selectedSnapshotId}.");
        }

        return ReadSymbolInfo(reader);
    }

    private static SqliteConnection CreateConnection(string dbPath, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = mode,
            ForeignKeys = true,
            Pooling = false
        };

        return new SqliteConnection(builder.ToString());
    }

    private static async Task<SqliteConnection> OpenReadOnlyValidatedAsync(
        string dbPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        var fullPath = Path.GetFullPath(dbPath);
        if (!File.Exists(fullPath))
        {
            throw new DotTraceException($"SQLite cache does not exist: {fullPath}");
        }

        var connection = CreateConnection(fullPath, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken);
        await SqliteSchema.ValidateAsync(connection, cancellationToken);
        return connection;
    }

    private static async Task<CallTreeProjector> CreateProjectorAsync(
        string dbPath,
        AnalysisOptions? options,
        long? snapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        await using var connection = await OpenReadOnlyValidatedAsync(dbPath, cancellationToken);
        var selectedSnapshotId = await ResolveSnapshotIdAsync(connection, snapshotId, cancellationToken);
        await EnsureSnapshotExistsAsync(connection, selectedSnapshotId, cancellationToken);

        var projects = await LoadProjectsForProjectionAsync(connection, selectedSnapshotId, cancellationToken);
        var symbols = await LoadSymbolsAsync(connection, selectedSnapshotId, cancellationToken);
        var calls = await LoadCallsAsync(connection, selectedSnapshotId, cancellationToken);
        var rootSymbols = await LoadRootSymbolsAsync(connection, selectedSnapshotId, cancellationToken);
        return new CallTreeProjector(projects, symbols, calls, rootSymbols, options ?? new AnalysisOptions());
    }

    private static async Task<long> ResolveSnapshotIdAsync(
        SqliteConnection connection,
        long? snapshotId,
        CancellationToken cancellationToken)
    {
        var selectedSnapshotId = snapshotId ?? await GetActiveSnapshotIdAsync(connection, cancellationToken);
        await EnsureSnapshotExistsAsync(connection, selectedSnapshotId, cancellationToken);
        return selectedSnapshotId;
    }

    private static async Task<long> InsertSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CallGraphBuildResult buildResult,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO snapshots(input_path, workspace_fingerprint, tool_version, created_utc)
            VALUES ($input_path, $workspace_fingerprint, $tool_version, $created_utc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$input_path", buildResult.InputPath);
        command.Parameters.AddWithValue("$workspace_fingerprint", buildResult.WorkspaceFingerprint);
        command.Parameters.AddWithValue("$tool_version", buildResult.ToolVersion);
        command.Parameters.AddWithValue("$created_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<Dictionary<string, long>> InsertProjectsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long snapshotId,
        IReadOnlyList<CallGraphProject> projects,
        CancellationToken cancellationToken)
    {
        var projectIds = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO projects(snapshot_id, stable_id, name, assembly_name, file_path)
            VALUES ($snapshot_id, $stable_id, $name, $assembly_name, $file_path);
            SELECT last_insert_rowid();
            """;
        var snapshotParameter = command.Parameters.Add("$snapshot_id", SqliteType.Integer);
        var stableIdParameter = command.Parameters.Add("$stable_id", SqliteType.Text);
        var nameParameter = command.Parameters.Add("$name", SqliteType.Text);
        var assemblyNameParameter = command.Parameters.Add("$assembly_name", SqliteType.Text);
        var filePathParameter = command.Parameters.Add("$file_path", SqliteType.Text);

        foreach (var project in projects)
        {
            snapshotParameter.Value = snapshotId;
            stableIdParameter.Value = project.StableId;
            nameParameter.Value = project.Name;
            assemblyNameParameter.Value = project.AssemblyName;
            filePathParameter.Value = project.FilePath;

            var result = await command.ExecuteScalarAsync(cancellationToken);
            projectIds.Add(project.StableId, Convert.ToInt64(result, CultureInfo.InvariantCulture));
        }

        return projectIds;
    }

    private static async Task<Dictionary<string, long>> InsertSymbolsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long snapshotId,
        IReadOnlyList<CallGraphSymbol> symbols,
        IReadOnlyDictionary<string, long> projectIds,
        CancellationToken cancellationToken)
    {
        var symbolIds = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO symbols(
              snapshot_id,
              project_id,
              stable_id,
              qualified_name,
              signature_text,
              normalized_qualified_name,
              normalized_signature_text,
              origin_kind,
              file_path,
              line,
              column)
            VALUES (
              $snapshot_id,
              $project_id,
              $stable_id,
              $qualified_name,
              $signature_text,
              $normalized_qualified_name,
              $normalized_signature_text,
              $origin_kind,
              $file_path,
              $line,
              $column);
            SELECT last_insert_rowid();
            """;
        var snapshotParameter = command.Parameters.Add("$snapshot_id", SqliteType.Integer);
        var projectParameter = command.Parameters.Add("$project_id", SqliteType.Integer);
        var stableIdParameter = command.Parameters.Add("$stable_id", SqliteType.Text);
        var qualifiedNameParameter = command.Parameters.Add("$qualified_name", SqliteType.Text);
        var signatureTextParameter = command.Parameters.Add("$signature_text", SqliteType.Text);
        var normalizedQualifiedNameParameter = command.Parameters.Add("$normalized_qualified_name", SqliteType.Text);
        var normalizedSignatureTextParameter = command.Parameters.Add("$normalized_signature_text", SqliteType.Text);
        var originKindParameter = command.Parameters.Add("$origin_kind", SqliteType.Text);
        var filePathParameter = command.Parameters.Add("$file_path", SqliteType.Text);
        var lineParameter = command.Parameters.Add("$line", SqliteType.Integer);
        var columnParameter = command.Parameters.Add("$column", SqliteType.Integer);

        foreach (var symbol in symbols)
        {
            snapshotParameter.Value = snapshotId;
            projectParameter.Value = symbol.ProjectStableId is null ? DBNull.Value : projectIds[symbol.ProjectStableId];
            stableIdParameter.Value = symbol.StableId;
            qualifiedNameParameter.Value = symbol.QualifiedName;
            signatureTextParameter.Value = symbol.SignatureText;
            normalizedQualifiedNameParameter.Value = symbol.NormalizedQualifiedName;
            normalizedSignatureTextParameter.Value = symbol.NormalizedSignatureText;
            originKindParameter.Value = ToDatabaseValue(symbol.OriginKind);
            filePathParameter.Value = symbol.Location?.FilePath is null ? DBNull.Value : symbol.Location.FilePath;
            lineParameter.Value = symbol.Location?.Line is null ? DBNull.Value : symbol.Location.Line;
            columnParameter.Value = symbol.Location?.Column is null ? DBNull.Value : symbol.Location.Column;

            var result = await command.ExecuteScalarAsync(cancellationToken);
            symbolIds.Add(symbol.StableId, Convert.ToInt64(result, CultureInfo.InvariantCulture));
        }

        return symbolIds;
    }

    private static async Task InsertRootSymbolsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long snapshotId,
        IReadOnlyList<CallGraphRootSymbol> rootSymbols,
        IReadOnlyDictionary<string, long> projectIds,
        IReadOnlyDictionary<string, long> symbolIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO root_symbols(
              snapshot_id,
              project_id,
              symbol_id,
              kind,
              metadata_json)
            VALUES (
              $snapshot_id,
              $project_id,
              $symbol_id,
              $kind,
              $metadata_json);
            """;
        var snapshotParameter = command.Parameters.Add("$snapshot_id", SqliteType.Integer);
        var projectParameter = command.Parameters.Add("$project_id", SqliteType.Integer);
        var symbolParameter = command.Parameters.Add("$symbol_id", SqliteType.Integer);
        var kindParameter = command.Parameters.Add("$kind", SqliteType.Text);
        var metadataParameter = command.Parameters.Add("$metadata_json", SqliteType.Text);

        foreach (var rootSymbol in rootSymbols)
        {
            snapshotParameter.Value = snapshotId;
            projectParameter.Value = projectIds[rootSymbol.ProjectStableId];
            symbolParameter.Value = symbolIds[rootSymbol.SymbolStableId];
            kindParameter.Value = ToDatabaseValue(rootSymbol.Kind);
            metadataParameter.Value = rootSymbol.MetadataJson is null ? DBNull.Value : rootSymbol.MetadataJson;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertCallsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long snapshotId,
        IReadOnlyList<CallGraphCall> calls,
        IReadOnlyDictionary<string, long> symbolIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO calls(
              snapshot_id,
              caller_symbol_id,
              callee_symbol_id,
              call_text,
              file_path,
              line,
              column,
              ordinal)
            VALUES (
              $snapshot_id,
              $caller_symbol_id,
              $callee_symbol_id,
              $call_text,
              $file_path,
              $line,
              $column,
              $ordinal);
            """;
        var snapshotParameter = command.Parameters.Add("$snapshot_id", SqliteType.Integer);
        var callerParameter = command.Parameters.Add("$caller_symbol_id", SqliteType.Integer);
        var calleeParameter = command.Parameters.Add("$callee_symbol_id", SqliteType.Integer);
        var callTextParameter = command.Parameters.Add("$call_text", SqliteType.Text);
        var filePathParameter = command.Parameters.Add("$file_path", SqliteType.Text);
        var lineParameter = command.Parameters.Add("$line", SqliteType.Integer);
        var columnParameter = command.Parameters.Add("$column", SqliteType.Integer);
        var ordinalParameter = command.Parameters.Add("$ordinal", SqliteType.Integer);

        foreach (var call in calls)
        {
            snapshotParameter.Value = snapshotId;
            callerParameter.Value = symbolIds[call.CallerStableId];
            calleeParameter.Value = call.CalleeStableId is null ? DBNull.Value : symbolIds[call.CalleeStableId];
            callTextParameter.Value = call.CallText;
            filePathParameter.Value = call.Location?.FilePath is null ? DBNull.Value : call.Location.FilePath;
            lineParameter.Value = call.Location?.Line is null ? DBNull.Value : call.Location.Line;
            columnParameter.Value = call.Location?.Column is null ? DBNull.Value : call.Location.Column;
            ordinalParameter.Value = call.Ordinal;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertDiagnosticsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long snapshotId,
        IReadOnlyList<string> diagnostics,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO diagnostics(snapshot_id, message)
            VALUES ($snapshot_id, $message);
            """;
        var snapshotParameter = command.Parameters.Add("$snapshot_id", SqliteType.Integer);
        var messageParameter = command.Parameters.Add("$message", SqliteType.Text);

        foreach (var diagnostic in diagnostics.Distinct(StringComparer.Ordinal))
        {
            snapshotParameter.Value = snapshotId;
            messageParameter.Value = diagnostic;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task SetActiveSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO cache_state(id, active_snapshot_id)
            VALUES (1, $snapshot_id)
            ON CONFLICT(id) DO UPDATE SET active_snapshot_id = excluded.active_snapshot_id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> GetActiveSnapshotIdAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT active_snapshot_id FROM cache_state WHERE id = 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result == DBNull.Value)
        {
            throw new DotTraceException("SQLite cache does not have an active snapshot. Run 'cache build' first.");
        }

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task EnsureSnapshotExistsAsync(
        SqliteConnection connection,
        long snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM snapshots WHERE id = $snapshot_id;";
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (Convert.ToInt64(result, CultureInfo.InvariantCulture) == 0)
        {
            throw new DotTraceException($"SQLite cache snapshot {snapshotId} does not exist.");
        }
    }

    private static async Task<Dictionary<long, CachedSymbol>> LoadSymbolsAsync(
        SqliteConnection connection,
        long snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, project_id, stable_id, qualified_name, signature_text, normalized_qualified_name, normalized_signature_text, origin_kind, file_path, line, column
            FROM symbols
            WHERE snapshot_id = $snapshot_id
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);

        var symbols = new Dictionary<long, CachedSymbol>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            symbols.Add(
                id,
                new CachedSymbol(
                    id,
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    FromDatabaseValue(reader.GetString(7)),
                    ReadLocation(reader, filePathOrdinal: 8, lineOrdinal: 9, columnOrdinal: 10)));
        }

        return symbols;
    }

    private static async Task<Dictionary<long, CachedProject>> LoadProjectsForProjectionAsync(
        SqliteConnection connection,
        long snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, stable_id, name, assembly_name, file_path
            FROM projects
            WHERE snapshot_id = $snapshot_id
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);

        var projects = new Dictionary<long, CachedProject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            projects.Add(
                id,
                new CachedProject(
                    id,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4)));
        }

        return projects;
    }

    private static async Task<IReadOnlyList<CachedRootSymbol>> LoadRootSymbolsAsync(
        SqliteConnection connection,
        long snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT project_id, symbol_id, kind, metadata_json
            FROM root_symbols
            WHERE snapshot_id = $snapshot_id
            ORDER BY project_id, kind, symbol_id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);

        var rootSymbols = new List<CachedRootSymbol>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rootSymbols.Add(new CachedRootSymbol(
                reader.GetInt64(0),
                reader.GetInt64(1),
                FromRootKindDatabaseValue(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rootSymbols;
    }

    private static async Task<CachedCalls> LoadCallsAsync(
        SqliteConnection connection,
        long snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT caller_symbol_id, callee_symbol_id, call_text, file_path, line, column, ordinal
            FROM calls
            WHERE snapshot_id = $snapshot_id
            ORDER BY caller_symbol_id, ordinal;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);

        var callsByCaller = new Dictionary<long, List<CachedCall>>();
        var callsByCallee = new Dictionary<long, List<CachedCall>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var callerId = reader.GetInt64(0);
            var call = new CachedCall(
                callerId,
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetString(2),
                ReadLocation(reader, filePathOrdinal: 3, lineOrdinal: 4, columnOrdinal: 5),
                reader.GetInt32(6));

            if (!callsByCaller.TryGetValue(callerId, out var calls))
            {
                calls = new List<CachedCall>();
                callsByCaller.Add(callerId, calls);
            }

            calls.Add(call);

            if (call.CalleeSymbolId is long calleeId)
            {
                if (!callsByCallee.TryGetValue(calleeId, out var callerCalls))
                {
                    callerCalls = new List<CachedCall>();
                    callsByCallee.Add(calleeId, callerCalls);
                }

                callerCalls.Add(call);
            }
        }

        return new CachedCalls(
            callsByCaller.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<CachedCall>)pair.Value.OrderBy(call => call.Ordinal).ToArray()),
            callsByCallee.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<CachedCall>)pair.Value
                    .OrderBy(call => call.CallerSymbolId)
                    .ThenBy(call => call.Ordinal)
                    .ToArray()));
    }

    private static SourceLocationInfo? ReadLocation(
        SqliteDataReader reader,
        int filePathOrdinal,
        int lineOrdinal,
        int columnOrdinal)
    {
        if (reader.IsDBNull(filePathOrdinal) || reader.IsDBNull(lineOrdinal) || reader.IsDBNull(columnOrdinal))
        {
            return null;
        }

        return new SourceLocationInfo(
            reader.GetString(filePathOrdinal),
            reader.GetInt32(lineOrdinal),
            reader.GetInt32(columnOrdinal));
    }

    private static CallGraphSymbolInfo ReadSymbolInfo(SqliteDataReader reader)
    {
        return new CallGraphSymbolInfo(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            FromDatabaseValue(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            ReadLocation(reader, filePathOrdinal: 8, lineOrdinal: 9, columnOrdinal: 10),
            reader.GetInt64(11),
            reader.GetInt64(12));
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }

    private static string ToDatabaseValue(SymbolOriginKind kind)
    {
        return kind switch
        {
            SymbolOriginKind.Source => "source",
            SymbolOriginKind.External => "external",
            _ => throw new DotTraceException($"Unsupported symbol origin kind '{kind}'.")
        };
    }

    private static string ToDatabaseValue(RootSymbolKind kind)
    {
        return kind switch
        {
            RootSymbolKind.CompilerEntryPoint => "compiler-entry-point",
            RootSymbolKind.AspNetControllerAction => "aspnet-controller-action",
            _ => throw new DotTraceException($"Unsupported root symbol kind '{kind}'.")
        };
    }

    private static SymbolOriginKind FromDatabaseValue(string value)
    {
        return value switch
        {
            "source" => SymbolOriginKind.Source,
            "external" => SymbolOriginKind.External,
            _ => throw new DotTraceException($"Unsupported symbol origin kind '{value}'.")
        };
    }

    private static RootSymbolKind FromRootKindDatabaseValue(string value)
    {
        return value switch
        {
            "compiler-entry-point" => RootSymbolKind.CompilerEntryPoint,
            "aspnet-controller-action" => RootSymbolKind.AspNetControllerAction,
            _ => throw new DotTraceException($"Unsupported root symbol kind '{value}'.")
        };
    }

    private sealed class CallTreeProjector
    {
        private readonly IReadOnlyDictionary<long, CachedProject> projects;
        private readonly IReadOnlyDictionary<long, CachedSymbol> symbols;
        private readonly IReadOnlyDictionary<long, IReadOnlyList<CachedCall>> callsByCaller;
        private readonly IReadOnlyDictionary<long, IReadOnlyList<CachedCall>> callsByCallee;
        private readonly IReadOnlyList<CachedRootSymbol> rootSymbols;
        private readonly AnalysisOptions options;

        public CallTreeProjector(
            IReadOnlyDictionary<long, CachedProject> projects,
            IReadOnlyDictionary<long, CachedSymbol> symbols,
            CachedCalls calls,
            IReadOnlyList<CachedRootSymbol> rootSymbols,
            AnalysisOptions options)
        {
            this.projects = projects;
            this.symbols = symbols;
            this.callsByCaller = calls.ByCaller;
            this.callsByCallee = calls.ByCallee;
            this.rootSymbols = rootSymbols;
            this.options = options;
        }

        public CallTreeNode ProjectCallees(string symbolSelector)
        {
            var root = ResolveRoot(symbolSelector);
            return ProjectCallees(root);
        }

        public CallTreeDocument ProjectDocument(string symbolSelector)
        {
            var root = ResolveRoot(symbolSelector);
            var selectedRoot = CreateNode(root, CallTreeNodeKind.Source, Array.Empty<CallTreeNode>(), displayText: null);

            return new CallTreeDocument(
                selectedRoot,
                ProjectCallers(root),
                ProjectCallees(root));
        }

        public CallTreeDocument ProjectDocument(long symbolId)
        {
            var root = ResolveRoot(symbolId);
            var selectedRoot = CreateNode(root, CallTreeNodeKind.Source, Array.Empty<CallTreeNode>(), displayText: null);

            return new CallTreeDocument(
                selectedRoot,
                ProjectCallers(root),
                ProjectCallees(root));
        }

        public CallTreeNode ProjectMap(long projectId)
        {
            if (!projects.TryGetValue(projectId, out var project))
            {
                throw new DotTraceException($"SQLite cache project {projectId} does not exist in the selected snapshot.");
            }

            var inScopeSourceSymbols = symbols.Values
                .Where(symbol => symbol.OriginKind == SymbolOriginKind.Source && symbol.ProjectId == projectId)
                .OrderBy(symbol => symbol.QualifiedName, StringComparer.Ordinal)
                .ThenBy(symbol => symbol.SignatureText, StringComparer.Ordinal)
                .ThenBy(symbol => symbol.Id)
                .ToArray();
            var inScopeSourceIds = inScopeSourceSymbols.Select(symbol => symbol.Id).ToHashSet();
            var explicitRoots = rootSymbols
                .Where(root => root.ProjectId == projectId && inScopeSourceIds.Contains(root.SymbolId))
                .OrderBy(root => GetRootPriority(root.Kind))
                .ThenBy(root => symbols[root.SymbolId].QualifiedName, StringComparer.Ordinal)
                .ThenBy(root => symbols[root.SymbolId].SignatureText, StringComparer.Ordinal)
                .ThenBy(root => root.SymbolId)
                .ToArray();
            var explicitRootIds = explicitRoots.Select(root => root.SymbolId).ToHashSet();
            var expanded = new HashSet<long>();
            var groups = new List<CallTreeNode>();

            AddRootGroup(
                groups,
                "compiler-entry-points",
                "Compiler entry points",
                explicitRoots.Where(root => root.Kind == RootSymbolKind.CompilerEntryPoint).Select(root => root.SymbolId),
                projectId,
                expanded);
            AddRootGroup(
                groups,
                "aspnet-controller-actions",
                "ASP.NET controller actions",
                explicitRoots.Where(root => root.Kind == RootSymbolKind.AspNetControllerAction).Select(root => root.SymbolId),
                projectId,
                expanded);

            var graphRootIds = inScopeSourceSymbols
                .Where(symbol => !explicitRootIds.Contains(symbol.Id))
                .Where(symbol => !HasInScopeCaller(symbol.Id, projectId))
                .Select(symbol => symbol.Id)
                .ToArray();
            AddRootGroup(groups, "graph-roots", "Graph roots", graphRootIds, projectId, expanded);

            var remainingRootIds = inScopeSourceSymbols
                .Where(symbol => !expanded.Contains(symbol.Id))
                .Select(symbol => symbol.Id)
                .ToArray();
            AddRootGroup(groups, "remaining-source-islands", "Remaining source islands", remainingRootIds, projectId, expanded);

            return new CallTreeNode(
                $"project::{project.StableId}",
                $"Map: {project.Name}",
                CallTreeNodeKind.Group,
                null,
                groups);
        }

        private CachedSymbol ResolveRoot(string symbolSelector)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(symbolSelector);

            var selector = SymbolFormatting.ParseSelector(symbolSelector);
            var candidates = symbols.Values
                .Where(symbol => symbol.OriginKind == SymbolOriginKind.Source)
                .Where(symbol => selector.Signature is not null
                    ? string.Equals(symbol.NormalizedSignatureText, selector.Signature, StringComparison.Ordinal)
                    : string.Equals(symbol.NormalizedQualifiedName, selector.FullyQualifiedName, StringComparison.Ordinal))
                .ToArray();

            if (candidates.Length == 0)
            {
                throw new DotTraceException($"No method matched symbol '{symbolSelector}'.");
            }

            if (candidates.Length > 1)
            {
                var matches = string.Join(
                    Environment.NewLine,
                    candidates
                        .Select(symbol => $" - {symbol.SignatureText}")
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal));

                throw new DotTraceException(
                    $"The symbol '{symbolSelector}' is ambiguous. Use a fully-qualified signature. Matches:{Environment.NewLine}{matches}");
            }

            return candidates[0];
        }

        private CachedSymbol ResolveRoot(long symbolId)
        {
            if (!symbols.TryGetValue(symbolId, out var symbol))
            {
                throw new DotTraceException($"SQLite cache symbol {symbolId} does not exist in the selected snapshot.");
            }

            if (symbol.OriginKind != SymbolOriginKind.Source)
            {
                throw new DotTraceException($"SQLite cache symbol {symbolId} is external. Only source symbols can be rendered as tree roots.");
            }

            return symbol;
        }

        private CallTreeNode ProjectCallees(CachedSymbol root)
        {
            return BuildCalleeSourceNode(
                root.Id,
                depth: 0,
                callStack: new HashSet<long>(),
                displayText: null,
                expanded: new HashSet<long>(),
                scopeProjectId: null);
        }

        private CallTreeNode ProjectCallers(CachedSymbol root)
        {
            return BuildCallerSourceNode(
                root.Id,
                depth: 0,
                callStack: new HashSet<long>(),
                displayText: null,
                expanded: new HashSet<long>());
        }

        private void AddRootGroup(
            ICollection<CallTreeNode> groups,
            string id,
            string label,
            IEnumerable<long> rootIds,
            long projectId,
            ISet<long> expanded)
        {
            var children = rootIds
                .Distinct()
                .Select(rootId => BuildCalleeSourceNode(
                    rootId,
                    depth: 0,
                    callStack: new HashSet<long>(),
                    displayText: null,
                    expanded,
                    projectId))
                .ToArray();
            if (children.Length == 0)
            {
                return;
            }

            groups.Add(new CallTreeNode(
                id,
                label,
                CallTreeNodeKind.Group,
                null,
                children));
        }

        private bool HasInScopeCaller(long symbolId, long projectId)
        {
            return callsByCallee.TryGetValue(symbolId, out var incoming) &&
                incoming.Any(call =>
                    symbols.TryGetValue(call.CallerSymbolId, out var caller) &&
                    caller.ProjectId == projectId);
        }

        private static int GetRootPriority(RootSymbolKind kind)
        {
            return kind switch
            {
                RootSymbolKind.CompilerEntryPoint => 0,
                RootSymbolKind.AspNetControllerAction => 1,
                _ => 99
            };
        }

        private CallTreeNode BuildCalleeSourceNode(
            long symbolId,
            int depth,
            ISet<long> callStack,
            string? displayText,
            ISet<long> expanded,
            long? scopeProjectId)
        {
            var symbol = symbols[symbolId];

            if (callStack.Contains(symbolId))
            {
                return CreateNode(symbol, CallTreeNodeKind.Cycle, Array.Empty<CallTreeNode>(), displayText);
            }

            if (expanded.Contains(symbolId))
            {
                return CreateNode(symbol, CallTreeNodeKind.Repeated, Array.Empty<CallTreeNode>(), displayText);
            }

            if (options.MaxDepth is int maxDepth && depth >= maxDepth)
            {
                return CreateNode(symbol, CallTreeNodeKind.Truncated, Array.Empty<CallTreeNode>(), displayText);
            }

            expanded.Add(symbolId);
            var nextStack = new HashSet<long>(callStack) { symbolId };
            var children = callsByCaller.TryGetValue(symbolId, out var calls)
                ? calls.Select(call => BuildCalleeChildNode(call, depth + 1, nextStack, expanded, scopeProjectId)).ToArray()
                : Array.Empty<CallTreeNode>();

            return CreateNode(symbol, CallTreeNodeKind.Source, children, displayText);
        }

        private CallTreeNode BuildCalleeChildNode(
            CachedCall call,
            int depth,
            ISet<long> callStack,
            ISet<long> expanded,
            long? scopeProjectId)
        {
            if (call.CalleeSymbolId is null)
            {
                return new CallTreeNode(
                    $"unresolved::{call.CallText}::{call.Location?.FilePath}:{call.Location?.Line}:{call.Location?.Column}",
                    call.CallText,
                    CallTreeNodeKind.Unresolved,
                    call.Location,
                    Array.Empty<CallTreeNode>());
            }

            var symbol = symbols[call.CalleeSymbolId.Value];
            if (symbol.OriginKind == SymbolOriginKind.External)
            {
                return CreateNode(symbol, CallTreeNodeKind.External, Array.Empty<CallTreeNode>(), call.CallText);
            }

            if (scopeProjectId is not null && symbol.ProjectId != scopeProjectId)
            {
                return CreateNode(symbol, CallTreeNodeKind.Boundary, Array.Empty<CallTreeNode>(), call.CallText);
            }

            return BuildCalleeSourceNode(symbol.Id, depth, callStack, call.CallText, expanded, scopeProjectId);
        }

        private CallTreeNode BuildCallerSourceNode(
            long symbolId,
            int depth,
            ISet<long> callStack,
            string? displayText,
            ISet<long> expanded)
        {
            var symbol = symbols[symbolId];

            if (callStack.Contains(symbolId))
            {
                return CreateNode(symbol, CallTreeNodeKind.Cycle, Array.Empty<CallTreeNode>(), displayText);
            }

            if (expanded.Contains(symbolId))
            {
                return CreateNode(symbol, CallTreeNodeKind.Repeated, Array.Empty<CallTreeNode>(), displayText);
            }

            if (options.MaxDepth is int maxDepth && depth >= maxDepth)
            {
                return CreateNode(symbol, CallTreeNodeKind.Truncated, Array.Empty<CallTreeNode>(), displayText);
            }

            expanded.Add(symbolId);
            var nextStack = new HashSet<long>(callStack) { symbolId };
            var children = callsByCallee.TryGetValue(symbolId, out var calls)
                ? calls
                    .GroupBy(call => call.CallerSymbolId)
                    .Select(group => BuildCallerNode(group.First(), depth + 1, nextStack, expanded))
                    .ToArray()
                : Array.Empty<CallTreeNode>();

            return CreateNode(symbol, CallTreeNodeKind.Source, children, displayText);
        }

        private CallTreeNode BuildCallerNode(CachedCall call, int depth, ISet<long> callStack, ISet<long> expanded)
        {
            var symbol = symbols[call.CallerSymbolId];

            if (symbol.OriginKind == SymbolOriginKind.External)
            {
                return CreateNode(symbol, CallTreeNodeKind.External, Array.Empty<CallTreeNode>(), displayText: null);
            }

            return BuildCallerSourceNode(symbol.Id, depth, callStack, displayText: null, expanded);
        }

        private static CallTreeNode CreateNode(
            CachedSymbol symbol,
            CallTreeNodeKind kind,
            IReadOnlyList<CallTreeNode> children,
            string? displayText)
        {
            return new CallTreeNode(symbol.StableId, displayText ?? symbol.SignatureText, kind, symbol.Location, children);
        }
    }

    private sealed record CachedSymbol(
        long Id,
        long? ProjectId,
        string StableId,
        string QualifiedName,
        string SignatureText,
        string NormalizedQualifiedName,
        string NormalizedSignatureText,
        SymbolOriginKind OriginKind,
        SourceLocationInfo? Location);

    private sealed record CachedProject(
        long Id,
        string StableId,
        string Name,
        string AssemblyName,
        string FilePath);

    private sealed record CachedRootSymbol(
        long ProjectId,
        long SymbolId,
        RootSymbolKind Kind,
        string? MetadataJson);

    private sealed record CachedCalls(
        IReadOnlyDictionary<long, IReadOnlyList<CachedCall>> ByCaller,
        IReadOnlyDictionary<long, IReadOnlyList<CachedCall>> ByCallee);

    private sealed record CachedCall(
        long CallerSymbolId,
        long? CalleeSymbolId,
        string CallText,
        SourceLocationInfo? Location,
        int Ordinal);
}
