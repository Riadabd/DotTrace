using System.Text.Json;
using DotTrace.Core.Analysis;
using DotTrace.Core.Persistence;
using Xunit;

namespace DotTrace.Tests;

public sealed class BrowserServerTests
{
    [Fact]
    public async Task Browser_server_serves_ui_and_read_only_cache_apis()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var dbPath = Path.Combine(fixture.RootPath, "graph.db");
        var cache = new SqliteGraphCache();
        var build = await new CallGraphBuilder().BuildAsync(fixture.ProjectPath);
        await cache.WriteSnapshotAsync(dbPath, build);

        await using var server = await BrowserServer.StartAsync(
            new BrowserServerOptions(dbPath, InitialSnapshotId: null, Port: null, InitialMaxDepth: 3));
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };

        var html = await client.GetStringAsync("/");
        Assert.Contains("DotTrace Explorer", html, StringComparison.Ordinal);
        Assert.Contains("symbolSearch", html, StringComparison.Ordinal);
        Assert.Contains("projectSelect", html, StringComparison.Ordinal);
        Assert.Contains("renderMap", html, StringComparison.Ordinal);

        using var snapshotsDocument = await GetJsonAsync(client, "/api/snapshots");
        var snapshots = snapshotsDocument.RootElement;
        Assert.Equal(JsonValueKind.Array, snapshots.ValueKind);
        Assert.True(snapshots.GetArrayLength() > 0);
        var snapshotId = snapshots[0].GetProperty("id").GetInt64();

        using var symbolsDocument = await GetJsonAsync(
            client,
            $"/api/symbols?snapshot={snapshotId}&query=Worker.Step&pageSize=10");
        var symbols = symbolsDocument.RootElement;
        var symbol = Assert.Single(symbols.EnumerateArray());
        Assert.Equal("Sample.Worker.Step()", symbol.GetProperty("signatureText").GetString());
        var symbolId = symbol.GetProperty("id").GetInt64();

        using var detailDocument = await GetJsonAsync(client, $"/api/symbols/{symbolId}?snapshot={snapshotId}");
        Assert.Equal("Sample.Worker.Step()", detailDocument.RootElement.GetProperty("signatureText").GetString());

        using var projectsDocument = await GetJsonAsync(client, $"/api/projects?snapshot={snapshotId}");
        var project = Assert.Single(projectsDocument.RootElement.EnumerateArray());
        var projectId = project.GetProperty("id").GetInt64();
        Assert.Equal("Sample.App", project.GetProperty("name").GetString());

        using var mapDocument = await GetJsonAsync(
            client,
            $"/api/map?snapshot={snapshotId}&projectId={projectId}&maxDepth=3");
        Assert.Equal("Map: Sample.App", mapDocument.RootElement.GetProperty("map").GetProperty("displayText").GetString());

        using var treeDocument = await GetJsonAsync(
            client,
            $"/api/tree?snapshot={snapshotId}&symbolId={symbolId}&view=both&maxDepth=3");
        Assert.Equal("both", treeDocument.RootElement.GetProperty("view").GetString());
        var document = treeDocument.RootElement.GetProperty("document");
        Assert.Equal(
            "Sample.Worker.Step()",
            document.GetProperty("selectedRoot").GetProperty("displayText").GetString());
        Assert.Equal(
            "Sample.EntryPoint.Run()",
            document.GetProperty("callersTree").GetProperty("children")[0].GetProperty("displayText").GetString());
        Assert.Equal(
            "Loop()",
            document.GetProperty("calleesTree").GetProperty("children")[0].GetProperty("displayText").GetString());
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string requestUri)
    {
        await using var stream = await client.GetStreamAsync(requestUri);
        return await JsonDocument.ParseAsync(stream);
    }
}
