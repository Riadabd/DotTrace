using System.Text.Json;
using System.Text.Json.Serialization;
using DotTrace.Core.Analysis;
using DotTrace.Core.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed record BrowserServerOptions(
    string DbPath,
    long? InitialSnapshotId,
    int? Port,
    int? InitialMaxDepth);

internal sealed class RunningBrowserServer : IAsyncDisposable
{
    private readonly WebApplication app;

    public RunningBrowserServer(WebApplication app, string url)
    {
        this.app = app;
        Url = url;
    }

    public string Url { get; }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        return app.WaitForShutdownAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

internal static class BrowserServer
{
    public static async Task<int> RunAsync(
        BrowserServerOptions options,
        TextWriter log,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var server = await StartAsync(options, log, cancellationToken);
            await server.WaitForShutdownAsync(cancellationToken);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (DotTraceException exception)
        {
            log.WriteLine(exception.Message);
            return 1;
        }
    }

    public static async Task<RunningBrowserServer> StartAsync(
        BrowserServerOptions options,
        TextWriter? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DbPath);

        var dbPath = Path.GetFullPath(options.DbPath);
        await ValidateCacheAsync(dbPath, options.InitialSnapshotId, cancellationToken);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port ?? 0}");
        builder.Services.ConfigureHttpJsonOptions(jsonOptions =>
        {
            jsonOptions.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        var app = builder.Build();
        MapRoutes(app, dbPath, options);

        await app.StartAsync(cancellationToken);
        var url = ResolveUrl(app);
        if (log is not null)
        {
            await log.WriteLineAsync($"DotTrace browser UI: {url}");
        }

        return new RunningBrowserServer(app, url);
    }

    private static void MapRoutes(WebApplication app, string dbPath, BrowserServerOptions options)
    {
        var cache = new SqliteGraphCache();

        app.MapGet("/", () => Results.Content(BrowserUi.Html, "text/html; charset=utf-8"));

        app.MapGet("/api/config", () => Results.Ok(new
        {
            dbPath,
            initialSnapshotId = options.InitialSnapshotId,
            initialMaxDepth = options.InitialMaxDepth
        }));

        app.MapGet("/api/snapshots", async (CancellationToken cancellationToken) =>
        {
            return await HandleAsync(async () => Results.Ok(await cache.ListSnapshotsAsync(dbPath, cancellationToken)));
        });

        app.MapGet("/api/symbols", async (
            long? snapshot,
            string? query,
            int? page,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            return await HandleAsync(async () =>
            {
                var symbols = await cache.SearchSymbolsAsync(
                    dbPath,
                    query,
                    snapshot ?? options.InitialSnapshotId,
                    page ?? 1,
                    pageSize ?? 50,
                    cancellationToken);

                var formatter = await CreatePathFormatterAsync(
                    cache,
                    dbPath,
                    snapshot ?? options.InitialSnapshotId,
                    cancellationToken);

                return Results.Ok(symbols.Select(symbol => BrowserSymbolInfo.From(symbol, formatter)).ToArray());
            });
        });

        app.MapGet("/api/symbols/{id:long}", async (
            long id,
            long? snapshot,
            CancellationToken cancellationToken) =>
        {
            return await HandleAsync(async () =>
            {
                var symbol = await cache.GetSymbolAsync(
                    dbPath,
                    id,
                    snapshot ?? options.InitialSnapshotId,
                    cancellationToken);

                var formatter = await CreatePathFormatterAsync(
                    cache,
                    dbPath,
                    snapshot ?? options.InitialSnapshotId,
                    cancellationToken);

                return Results.Ok(BrowserSymbolInfo.From(symbol, formatter));
            });
        });

        app.MapGet("/api/projects", async (
            long? snapshot,
            CancellationToken cancellationToken) =>
        {
            return await HandleAsync(async () =>
            {
                var projects = await cache.ListProjectsAsync(
                    dbPath,
                    snapshot ?? options.InitialSnapshotId,
                    cancellationToken);

                var formatter = await CreatePathFormatterAsync(
                    cache,
                    dbPath,
                    snapshot ?? options.InitialSnapshotId,
                    cancellationToken);

                return Results.Ok(projects.Select(project => BrowserProjectInfo.From(project, formatter)).ToArray());
            });
        });

        app.MapGet("/api/tree", async (
            long symbolId,
            long? snapshot,
            string? view,
            int? maxDepth,
            CancellationToken cancellationToken) =>
        {
            return await HandleAsync(async () =>
            {
                var selectedView = ParseView(view);
                if (maxDepth is <= 0)
                {
                    throw new DotTraceException("maxDepth must be a positive integer.");
                }

                var document = await cache.ProjectDocumentBySymbolIdAsync(
                    dbPath,
                    symbolId,
                    new AnalysisOptions(maxDepth ?? options.InitialMaxDepth),
                    snapshot ?? options.InitialSnapshotId,
                    cancellationToken);

                var formatter = await CreatePathFormatterAsync(
                    cache,
                    dbPath,
                    snapshot ?? options.InitialSnapshotId,
                    cancellationToken);

                return Results.Ok(new BrowserTreeResponse(
                    selectedView,
                    BrowserCallTreeDocument.From(document, formatter)));
            });
        });

        app.MapGet("/api/map", async (
            long projectId,
            long? snapshot,
            int? maxDepth,
            CancellationToken cancellationToken) =>
        {
            return await HandleAsync(async () =>
            {
                if (projectId <= 0)
                {
                    throw new DotTraceException("projectId must be a positive integer.");
                }

                if (maxDepth is <= 0)
                {
                    throw new DotTraceException("maxDepth must be a positive integer.");
                }

                var map = await cache.ProjectMapAsync(
                    dbPath,
                    projectId,
                    new AnalysisOptions(maxDepth ?? options.InitialMaxDepth),
                    snapshot ?? options.InitialSnapshotId,
                    cancellationToken);

                var formatter = await CreatePathFormatterAsync(
                    cache,
                    dbPath,
                    snapshot ?? options.InitialSnapshotId,
                    cancellationToken);

                return Results.Ok(new BrowserMapResponse(BrowserCallTreeNode.From(map, formatter)));
            });
        });
    }

    private static async Task<BrowserPathFormatter> CreatePathFormatterAsync(
        SqliteGraphCache cache,
        string dbPath,
        long? snapshotId,
        CancellationToken cancellationToken)
    {
        var snapshots = await cache.ListSnapshotsAsync(dbPath, cancellationToken);
        var snapshot = snapshotId is null
            ? snapshots.FirstOrDefault(candidate => candidate.IsActive)
            : snapshots.FirstOrDefault(candidate => candidate.Id == snapshotId.Value);

        if (snapshot is null)
        {
            throw snapshotId is null
                ? new DotTraceException("SQLite cache does not have an active snapshot. Run 'cache build' first.")
                : new DotTraceException($"SQLite cache snapshot {snapshotId.Value} does not exist.");
        }

        return BrowserPathFormatter.FromInputPath(snapshot.InputPath);
    }

    private static async Task ValidateCacheAsync(
        string dbPath,
        long? initialSnapshotId,
        CancellationToken cancellationToken)
    {
        var snapshots = await new SqliteGraphCache().ListSnapshotsAsync(dbPath, cancellationToken);
        if (initialSnapshotId is null)
        {
            return;
        }

        if (!snapshots.Any(snapshot => snapshot.Id == initialSnapshotId.Value))
        {
            throw new DotTraceException($"SQLite cache snapshot {initialSnapshotId.Value} does not exist.");
        }
    }

    private static async Task<IResult> HandleAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (DotTraceException exception)
        {
            return Results.Problem(
                title: "DotTrace cache error",
                detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static CallTreeView ParseView(string? value)
    {
        return (value ?? "callees").ToLowerInvariant() switch
        {
            "callees" => CallTreeView.Callees,
            "callers" => CallTreeView.Callers,
            "both" => CallTreeView.Both,
            _ => throw new DotTraceException($"Unknown view '{value}'. Supported values: callees, callers, both.")
        };
    }

    private static string ResolveUrl(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        return addresses?.FirstOrDefault() ?? app.Urls.First();
    }

    private sealed record BrowserTreeResponse(CallTreeView View, BrowserCallTreeDocument Document);

    private sealed record BrowserMapResponse(BrowserCallTreeNode Map);

    private sealed record BrowserSymbolInfo(
        long Id,
        long SnapshotId,
        string QualifiedName,
        string SignatureText,
        SymbolOriginKind OriginKind,
        string? ProjectName,
        string? ProjectAssemblyName,
        string? ProjectFilePath,
        string? ProjectDisplayPath,
        BrowserLocation? Location,
        long DirectCallerCount,
        long DirectCalleeCount)
    {
        public static BrowserSymbolInfo From(CallGraphSymbolInfo symbol, BrowserPathFormatter formatter)
        {
            return new BrowserSymbolInfo(
                symbol.Id,
                symbol.SnapshotId,
                symbol.QualifiedName,
                symbol.SignatureText,
                symbol.OriginKind,
                symbol.ProjectName,
                symbol.ProjectAssemblyName,
                symbol.ProjectFilePath,
                symbol.ProjectFilePath is null ? null : formatter.FormatPath(symbol.ProjectFilePath),
                BrowserLocation.From(symbol.Location, formatter),
                symbol.DirectCallerCount,
                symbol.DirectCalleeCount);
        }
    }

    private sealed record BrowserProjectInfo(
        long Id,
        long SnapshotId,
        string Name,
        string AssemblyName,
        string FilePath,
        string DisplayPath,
        long SourceSymbolCount,
        long RootSymbolCount,
        long DirectCallCount)
    {
        public static BrowserProjectInfo From(CallGraphProjectInfo project, BrowserPathFormatter formatter)
        {
            return new BrowserProjectInfo(
                project.Id,
                project.SnapshotId,
                project.Name,
                project.AssemblyName,
                project.FilePath,
                formatter.FormatPath(project.FilePath),
                project.SourceSymbolCount,
                project.RootSymbolCount,
                project.DirectCallCount);
        }
    }

    private sealed record BrowserCallTreeDocument(
        BrowserCallTreeNode SelectedRoot,
        BrowserCallTreeNode CallersTree,
        BrowserCallTreeNode CalleesTree)
    {
        public static BrowserCallTreeDocument From(CallTreeDocument document, BrowserPathFormatter formatter)
        {
            return new BrowserCallTreeDocument(
                BrowserCallTreeNode.From(document.SelectedRoot, formatter),
                BrowserCallTreeNode.From(document.CallersTree, formatter),
                BrowserCallTreeNode.From(document.CalleesTree, formatter));
        }
    }

    private sealed record BrowserCallTreeNode(
        string Id,
        string DisplayText,
        CallTreeNodeKind Kind,
        BrowserLocation? Location,
        IReadOnlyList<BrowserCallTreeNode> Children)
    {
        public static BrowserCallTreeNode From(CallTreeNode node, BrowserPathFormatter formatter)
        {
            return new BrowserCallTreeNode(
                node.Id,
                node.DisplayText,
                node.Kind,
                BrowserLocation.From(node.Location, formatter),
                node.Children.Select(child => From(child, formatter)).ToArray());
        }
    }

    private sealed record BrowserLocation(string FilePath, string DisplayPath, int Line, int Column)
    {
        public static BrowserLocation? From(SourceLocationInfo? location, BrowserPathFormatter formatter)
        {
            return location is null
                ? null
                : new BrowserLocation(
                    location.FilePath,
                    formatter.FormatPath(location.FilePath),
                    location.Line,
                    location.Column);
        }
    }

    private sealed class BrowserPathFormatter
    {
        private readonly string sourceRootPath;

        private BrowserPathFormatter(string sourceRootPath)
        {
            this.sourceRootPath = sourceRootPath;
        }

        public static BrowserPathFormatter FromInputPath(string inputPath)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            var sourceRootPath = Path.GetDirectoryName(fullInputPath);
            if (string.IsNullOrEmpty(sourceRootPath))
            {
                sourceRootPath = Path.GetPathRoot(fullInputPath) ?? fullInputPath;
            }

            return new BrowserPathFormatter(sourceRootPath);
        }

        public string FormatPath(string filePath)
        {
            try
            {
                var fullPath = Path.GetFullPath(filePath);
                var relativePath = Path.GetRelativePath(sourceRootPath, fullPath);
                if (Path.IsPathFullyQualified(relativePath))
                {
                    return filePath;
                }

                return NormalizePath(relativePath);
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is NotSupportedException ||
                exception is PathTooLongException)
            {
                return filePath;
            }
        }

        private static string NormalizePath(string path)
        {
            return path
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }
    }
}
