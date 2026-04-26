using DotTrace.Core.Analysis;
using DotTrace.Core.Persistence;
using Xunit;

namespace DotTrace.Tests;

public sealed class CallGraphSnapshotTests
{
    [Fact]
    public async Task Build_write_and_project_active_snapshot_preserves_current_tree_behavior()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var cache = new SqliteGraphCache();
        var dbPath = Path.Combine(fixture.RootPath, "graph.db");

        var build = await new CallGraphBuilder().BuildAsync(fixture.ProjectPath);
        var snapshotId = await cache.WriteSnapshotAsync(dbPath, build);
        var tree = await cache.ProjectTreeAsync(dbPath, "Sample.EntryPoint.Run()");

        Assert.True(snapshotId > 0);
        Assert.Equal(CallTreeNodeKind.Source, tree.Kind);
        Assert.Equal("Sample.EntryPoint.Run()", tree.DisplayText);
        Assert.Contains(tree.Children, child => child.DisplayText == "Sample.Worker.Worker()");
        Assert.Contains(tree.Children, child => child.DisplayText == "Sample.Worker.Step()");
        Assert.Contains(tree.Children, child => child.Kind == CallTreeNodeKind.External && child.DisplayText.StartsWith("System.Console.WriteLine(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectTreeAsync_detects_cycles_and_max_depth_from_snapshot()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var cache = new SqliteGraphCache();
        var dbPath = Path.Combine(fixture.RootPath, "graph.db");

        var build = await new CallGraphBuilder().BuildAsync(fixture.ProjectPath);
        await cache.WriteSnapshotAsync(dbPath, build);

        var cycleTree = await cache.ProjectTreeAsync(dbPath, "Sample.Worker.Step()");
        var loop = FindNode(cycleTree, "Sample.Worker.Loop()");
        Assert.NotNull(loop);
        Assert.Contains(loop!.Children, child => child.Kind == CallTreeNodeKind.Cycle && child.DisplayText == "Sample.Worker.Loop()");

        var truncatedTree = await cache.ProjectTreeAsync(dbPath, "Sample.EntryPoint.Run()", new AnalysisOptions(maxDepth: 1));
        Assert.Contains(truncatedTree.Children, child => child.DisplayText == "Sample.Worker.Step()" && child.Kind == CallTreeNodeKind.Truncated);
    }

    [Fact]
    public async Task BuildAsync_persists_local_function_accessor_and_unresolved_call_edges()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var cache = new SqliteGraphCache();
        var dbPath = Path.Combine(fixture.RootPath, "graph.db");

        var build = await new CallGraphBuilder().BuildAsync(fixture.ProjectPath);
        await cache.WriteSnapshotAsync(dbPath, build);

        var localTree = await cache.ProjectTreeAsync(dbPath, "Sample.Worker.WithLocal()");
        Assert.Contains(localTree.Children, child => child.Kind == CallTreeNodeKind.Source && child.DisplayText.Contains("Local", StringComparison.Ordinal));

        var accessorTree = await cache.ProjectTreeAsync(dbPath, "Sample.AccessorUser.UseAccessors()");
        Assert.Contains(accessorTree.Children, child => child.DisplayText == "Sample.AccessorUser.set_Value(System.Int32)");
        Assert.Contains(accessorTree.Children, child => child.DisplayText == "Sample.AccessorUser.get_Value()");
        Assert.Contains(accessorTree.Children, child => child.DisplayText == "Sample.AccessorUser.get_Item(System.Int32)");
        Assert.Contains(accessorTree.Children, child => child.DisplayText == "Sample.AccessorUser.set_Item(System.Int32, System.Int32)");

        var unresolvedTree = await cache.ProjectTreeAsync(dbPath, "Sample.EntryPoint.Unresolved()");
        Assert.Contains(unresolvedTree.Children, child => child.Kind == CallTreeNodeKind.Unresolved && child.DisplayText == "MissingCall()");
    }

    [Fact]
    public async Task WriteSnapshotAsync_retains_history_and_rolls_back_failed_snapshot()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var cache = new SqliteGraphCache();
        var dbPath = Path.Combine(fixture.RootPath, "graph.db");

        var build = await new CallGraphBuilder().BuildAsync(fixture.ProjectPath);
        var firstSnapshotId = await cache.WriteSnapshotAsync(dbPath, build);
        var secondSnapshotId = await cache.WriteSnapshotAsync(dbPath, build);

        var snapshots = await cache.ListSnapshotsAsync(dbPath);
        Assert.Equal([secondSnapshotId, firstSnapshotId], snapshots.Select(snapshot => snapshot.Id).ToArray());
        Assert.True(snapshots.Single(snapshot => snapshot.Id == secondSnapshotId).IsActive);
        Assert.False(snapshots.Single(snapshot => snapshot.Id == firstSnapshotId).IsActive);

        var historicalTree = await cache.ProjectTreeAsync(dbPath, "Sample.EntryPoint.Run()", snapshotId: firstSnapshotId);
        Assert.Equal("Sample.EntryPoint.Run()", historicalTree.DisplayText);

        var brokenBuild = build with
        {
            Calls = [.. build.Calls, new CallGraphCall("missing-caller", null, "Missing()", null, 0)]
        };
        await Assert.ThrowsAsync<KeyNotFoundException>(() => cache.WriteSnapshotAsync(dbPath, brokenBuild));

        var snapshotsAfterFailure = await cache.ListSnapshotsAsync(dbPath);
        Assert.Equal([secondSnapshotId, firstSnapshotId], snapshotsAfterFailure.Select(snapshot => snapshot.Id).ToArray());
        Assert.True(snapshotsAfterFailure.Single(snapshot => snapshot.Id == secondSnapshotId).IsActive);
    }

    [Fact]
    public async Task ProjectTreeAsync_reports_ambiguous_roots_within_snapshot()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var cache = new SqliteGraphCache();
        var dbPath = Path.Combine(fixture.RootPath, "graph.db");

        var build = await new CallGraphBuilder().BuildAsync(fixture.ProjectPath);
        await cache.WriteSnapshotAsync(dbPath, build);

        var exception = await Assert.ThrowsAsync<DotTraceException>(() => cache.ProjectTreeAsync(dbPath, "Sample.Overloads.Execute"));

        Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sample.Overloads.Execute()", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Sample.Overloads.Execute(System.String)", exception.Message, StringComparison.Ordinal);
    }

    private static CallTreeNode? FindNode(CallTreeNode root, string displayText)
    {
        if (root.DisplayText == displayText)
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var match = FindNode(child, displayText);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

}
