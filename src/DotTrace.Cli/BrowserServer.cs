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

                return Results.Ok(symbols);
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

                return Results.Ok(symbol);
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

                return Results.Ok(new BrowserTreeResponse(selectedView, document));
            });
        });
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

    private sealed record BrowserTreeResponse(CallTreeView View, CallTreeDocument Document);
}
