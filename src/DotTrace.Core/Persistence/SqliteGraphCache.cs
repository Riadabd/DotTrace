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

    public async Task<CallTreeNode> ProjectTreeAsync(
        string dbPath,
        string symbolSelector,
        AnalysisOptions? options = null,
        long? snapshotId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolSelector);

        var fullPath = Path.GetFullPath(dbPath);
        if (!File.Exists(fullPath))
        {
            throw new DotTraceException($"SQLite cache does not exist: {fullPath}");
        }

        await using var connection = CreateConnection(fullPath, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken);
        await SqliteSchema.ValidateAsync(connection, cancellationToken);

        var selectedSnapshotId = snapshotId ?? await GetActiveSnapshotIdAsync(connection, cancellationToken);
        await EnsureSnapshotExistsAsync(connection, selectedSnapshotId, cancellationToken);

        var symbols = await LoadSymbolsAsync(connection, selectedSnapshotId, cancellationToken);
        var calls = await LoadCallsAsync(connection, selectedSnapshotId, cancellationToken);
        var projector = new CallTreeProjector(symbols, calls, options ?? new AnalysisOptions());
        return projector.Project(symbolSelector);
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
            SELECT id, stable_id, qualified_name, signature_text, normalized_qualified_name, normalized_signature_text, origin_kind, file_path, line, column
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
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    FromDatabaseValue(reader.GetString(6)),
                    ReadLocation(reader, filePathOrdinal: 7, lineOrdinal: 8, columnOrdinal: 9)));
        }

        return symbols;
    }

    private static async Task<IReadOnlyDictionary<long, IReadOnlyList<CachedCall>>> LoadCallsAsync(
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
        }

        return callsByCaller.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<CachedCall>)pair.Value.OrderBy(call => call.Ordinal).ToArray());
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

    private static string ToDatabaseValue(SymbolOriginKind kind)
    {
        return kind switch
        {
            SymbolOriginKind.Source => "source",
            SymbolOriginKind.External => "external",
            _ => throw new DotTraceException($"Unsupported symbol origin kind '{kind}'.")
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

    private sealed class CallTreeProjector
    {
        private readonly IReadOnlyDictionary<long, CachedSymbol> symbols;
        private readonly IReadOnlyDictionary<long, IReadOnlyList<CachedCall>> callsByCaller;
        private readonly AnalysisOptions options;
        private readonly HashSet<long> expanded = new();

        public CallTreeProjector(
            IReadOnlyDictionary<long, CachedSymbol> symbols,
            IReadOnlyDictionary<long, IReadOnlyList<CachedCall>> callsByCaller,
            AnalysisOptions options)
        {
            this.symbols = symbols;
            this.callsByCaller = callsByCaller;
            this.options = options;
        }

        public CallTreeNode Project(string symbolSelector)
        {
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

            return BuildSourceNode(candidates[0].Id, depth: 0, callStack: new HashSet<long>());
        }

        private CallTreeNode BuildSourceNode(long symbolId, int depth, ISet<long> callStack)
        {
            var symbol = symbols[symbolId];

            if (callStack.Contains(symbolId))
            {
                return CreateNode(symbol, CallTreeNodeKind.Cycle, Array.Empty<CallTreeNode>());
            }

            if (expanded.Contains(symbolId))
            {
                return CreateNode(symbol, CallTreeNodeKind.Repeated, Array.Empty<CallTreeNode>());
            }

            if (options.MaxDepth is int maxDepth && depth >= maxDepth)
            {
                return CreateNode(symbol, CallTreeNodeKind.Truncated, Array.Empty<CallTreeNode>());
            }

            expanded.Add(symbolId);
            var nextStack = new HashSet<long>(callStack) { symbolId };
            var children = callsByCaller.TryGetValue(symbolId, out var calls)
                ? calls.Select(call => BuildChildNode(call, depth + 1, nextStack)).ToArray()
                : Array.Empty<CallTreeNode>();

            return CreateNode(symbol, CallTreeNodeKind.Source, children);
        }

        private CallTreeNode BuildChildNode(CachedCall call, int depth, ISet<long> callStack)
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
                return CreateNode(symbol, CallTreeNodeKind.External, Array.Empty<CallTreeNode>());
            }

            return BuildSourceNode(symbol.Id, depth, callStack);
        }

        private static CallTreeNode CreateNode(CachedSymbol symbol, CallTreeNodeKind kind, IReadOnlyList<CallTreeNode> children)
        {
            return new CallTreeNode(symbol.StableId, symbol.SignatureText, kind, symbol.Location, children);
        }
    }

    private sealed record CachedSymbol(
        long Id,
        string StableId,
        string QualifiedName,
        string SignatureText,
        string NormalizedQualifiedName,
        string NormalizedSignatureText,
        SymbolOriginKind OriginKind,
        SourceLocationInfo? Location);

    private sealed record CachedCall(
        long CallerSymbolId,
        long? CalleeSymbolId,
        string CallText,
        SourceLocationInfo? Location,
        int Ordinal);
}
