using DotTrace.Core.Analysis;
using DotTrace.Core.Rendering;
using Xunit;

namespace DotTrace.Tests;

public sealed class TextTreeRendererTests
{
    [Fact]
    public void Render_emits_unicode_tree_and_markers()
    {
        var tree = new CallTreeNode(
            "root",
            "Sample.EntryPoint.Run()",
            CallTreeNodeKind.Source,
            null,
            new[]
            {
                new CallTreeNode("child-1", "Sample.Worker.Step()", CallTreeNodeKind.Source, null, Array.Empty<CallTreeNode>()),
                new CallTreeNode("child-2", "System.Console.WriteLine(System.String)", CallTreeNodeKind.External, null, Array.Empty<CallTreeNode>())
            });

        var output = new TextTreeRenderer().Render(tree);

        Assert.Contains("Sample.EntryPoint.Run()", output, StringComparison.Ordinal);
        Assert.Contains("├── Sample.Worker.Step()", output, StringComparison.Ordinal);
        Assert.Contains("└── System.Console.WriteLine(System.String) [external]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_with_both_view_centers_target_between_callers_and_callees()
    {
        var selectedRoot = new CallTreeNode(
            "step",
            "Sample.Worker.Step()",
            CallTreeNodeKind.Source,
            null,
            Array.Empty<CallTreeNode>());
        var callersTree = selectedRoot with
        {
            Children =
            [
                new CallTreeNode(
                    "run",
                    "Sample.EntryPoint.Run()",
                    CallTreeNodeKind.Source,
                    null,
                    Array.Empty<CallTreeNode>())
            ]
        };
        var calleesTree = selectedRoot with
        {
            Children =
            [
                new CallTreeNode("loop", "Loop()", CallTreeNodeKind.Source, null, Array.Empty<CallTreeNode>())
            ]
        };
        var document = new CallTreeDocument(selectedRoot, callersTree, calleesTree);

        var output = new TextTreeRenderer().Render(document, CallTreeView.Both, new RenderOptions(UseColor: false));

        Assert.Contains("Callers", output, StringComparison.Ordinal);
        Assert.Contains("└── Sample.EntryPoint.Run()", output, StringComparison.Ordinal);
        Assert.Contains("Target", output, StringComparison.Ordinal);
        Assert.Contains("=> Sample.Worker.Step() [target]", output, StringComparison.Ordinal);
        Assert.Contains("Callees", output, StringComparison.Ordinal);
        Assert.Contains("└── Loop()", output, StringComparison.Ordinal);
        AssertOrdered(output, "Callers", "=> Sample.Worker.Step() [target]", "Callees");
        Assert.Equal(1, CountOccurrences(output, "Sample.Worker.Step()"));
    }

    private static void AssertOrdered(string value, string first, string second, string third)
    {
        var firstIndex = value.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = value.IndexOf(second, StringComparison.Ordinal);
        var thirdIndex = value.IndexOf(third, StringComparison.Ordinal);

        Assert.True(firstIndex >= 0);
        Assert.True(secondIndex > firstIndex);
        Assert.True(thirdIndex > secondIndex);
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
