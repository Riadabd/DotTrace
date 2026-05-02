using DotTrace.Core.Analysis;
using DotTrace.Core.Persistence;
using Xunit;

namespace DotTrace.Tests;

public sealed class CallGraphSnapshotTests
{
    [Fact]
    public async Task Build_write_and_project_active_snapshot_uses_call_site_display_for_children()
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
        Assert.Contains(tree.Children, child => child.DisplayText == "new Worker()");
        Assert.Contains(tree.Children, child => child.DisplayText == "Step()");
        Assert.Contains(tree.Children, child => child.Kind == CallTreeNodeKind.External && child.DisplayText == "WriteLine(\"done\": System.String)");
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
        var loop = FindNode(cycleTree, "Loop()");
        Assert.NotNull(loop);
        Assert.Contains(loop!.Children, child => child.Kind == CallTreeNodeKind.Cycle && child.DisplayText == "Loop()");

        var truncatedTree = await cache.ProjectTreeAsync(dbPath, "Sample.EntryPoint.Run()", new AnalysisOptions(maxDepth: 1));
        Assert.Contains(truncatedTree.Children, child => child.DisplayText == "Step()" && child.Kind == CallTreeNodeKind.Truncated);
    }

    [Fact]
    public async Task ProjectDocumentAsync_projects_direct_and_recursive_callers()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var cache = new SqliteGraphCache();
        var dbPath = Path.Combine(fixture.RootPath, "graph.db");

        var build = await new CallGraphBuilder().BuildAsync(fixture.ProjectPath);
        await cache.WriteSnapshotAsync(dbPath, build);

        var stepDocument = await cache.ProjectDocumentAsync(dbPath, "Sample.Worker.Step()");

        Assert.Equal("Sample.Worker.Step()", stepDocument.SelectedRoot.DisplayText);
        var directCaller = Assert.Single(stepDocument.CallersTree.Children);
        Assert.Equal("Sample.EntryPoint.Run()", directCaller.DisplayText);
        Assert.NotEqual("Step()", directCaller.DisplayText);
        Assert.NotNull(directCaller.Location);
        Assert.EndsWith("EntryPoint.cs", directCaller.Location!.FilePath, StringComparison.Ordinal);

        var loopDocument = await cache.ProjectDocumentAsync(dbPath, "Sample.Worker.Loop()");
        var stepCaller = loopDocument.CallersTree.Children.SingleOrDefault(
            child => child.DisplayText == "Sample.Worker.Step()");

        Assert.NotNull(stepCaller);
        Assert.Contains(
            stepCaller!.Children,
            child => child.DisplayText == "Sample.EntryPoint.Run()");
    }

    [Fact]
    public async Task ProjectDocumentAsync_detects_cycle_repeated_and_max_depth_for_callers()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var cache = new SqliteGraphCache();
        var dbPath = Path.Combine(fixture.RootPath, "graph.db");

        var build = await new CallGraphBuilder().BuildAsync(fixture.ProjectPath);
        await cache.WriteSnapshotAsync(dbPath, build);

        var loopDocument = await cache.ProjectDocumentAsync(dbPath, "Sample.Worker.Loop()");
        Assert.Contains(
            loopDocument.CallersTree.Children,
            child => child.Kind == CallTreeNodeKind.Cycle
                && child.DisplayText == "Sample.Worker.Loop()");

        var repeatedDocument = await cache.ProjectDocumentAsync(dbPath, "Sample.RepeatedCallers.Leaf()");
        var rootCallers = repeatedDocument.CallersTree.Children
            .SelectMany(child => child.Children)
            .Where(child => child.DisplayText == "Sample.RepeatedCallers.Root()")
            .ToArray();

        Assert.Contains(rootCallers, child => child.Kind == CallTreeNodeKind.Source);
        Assert.Contains(rootCallers, child => child.Kind == CallTreeNodeKind.Repeated);

        var truncatedDocument = await cache.ProjectDocumentAsync(
            dbPath,
            "Sample.Worker.Step()",
            new AnalysisOptions(maxDepth: 1));
        var truncatedCaller = Assert.Single(truncatedDocument.CallersTree.Children);
        Assert.Equal(CallTreeNodeKind.Truncated, truncatedCaller.Kind);
        Assert.Equal("Sample.EntryPoint.Run()", truncatedCaller.DisplayText);
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
        Assert.Contains(accessorTree.Children, child => child.DisplayText == "Value");
        Assert.Contains(accessorTree.Children, child => child.DisplayText == "this[0]");
        Assert.Contains(accessorTree.Children, child => child.DisplayText == "this[1]");

        var unresolvedTree = await cache.ProjectTreeAsync(dbPath, "Sample.EntryPoint.Unresolved()");
        Assert.Contains(unresolvedTree.Children, child => child.Kind == CallTreeNodeKind.Unresolved && child.DisplayText == "MissingCall()");
    }

    [Fact]
    public async Task ProjectTreeAsync_displays_call_site_argument_labels_for_resolved_calls()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var cache = new SqliteGraphCache();
        var dbPath = Path.Combine(fixture.RootPath, "graph.db");

        var build = await new CallGraphBuilder().BuildAsync(fixture.ProjectPath);
        await cache.WriteSnapshotAsync(dbPath, build);

        var tree = await cache.ProjectTreeAsync(
            dbPath,
            "Sample.ArgumentSource.Run(System.String, System.Threading.CancellationToken)");

        Assert.Equal("Sample.ArgumentSource.Run(System.String, System.Threading.CancellationToken)", tree.DisplayText);
        Assert.Contains(tree.Children, child => child.DisplayText == "Target(str: System.String, ct: System.Threading.CancellationToken)");
        Assert.Contains(tree.Children, child => child.DisplayText == "Target(\"done\": System.String, CancellationToken.None: System.Threading.CancellationToken)");
        Assert.Contains(tree.Children, child => child.DisplayText == "Named(cancellationToken: ct: System.Threading.CancellationToken, value: str: System.String)");
        Assert.Contains(tree.Children, child => child.DisplayText == "RefOut(ref number: System.Int32, out var written: System.Int32)");
        Assert.Contains(tree.Children, child => child.DisplayText == "In(in number: System.Int32)");
        Assert.Contains(tree.Children, child => child.DisplayText == "Optional(str: System.String)");
        Assert.Contains(tree.Children, child => child.DisplayText == "new Constructed(str: System.String)");
        Assert.Contains(tree.Children, child => child.DisplayText == "new(str: System.String)");

        var constructorTree = await cache.ProjectTreeAsync(dbPath, "Sample.DerivedConstructed.DerivedConstructed(System.String)");
        Assert.Contains(constructorTree.Children, child => child.DisplayText == "base(value: System.String)");
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
