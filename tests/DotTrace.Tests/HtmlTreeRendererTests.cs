using DotTrace.Core.Analysis;
using DotTrace.Core.Rendering;
using Xunit;

namespace DotTrace.Tests;

public sealed class HtmlTreeRendererTests
{
    [Fact]
    public void RenderDocument_emits_zoom_controls_and_kind_classes()
    {
        var tree = new CallTreeNode(
            "root",
            "Sample.EntryPoint.Run()",
            CallTreeNodeKind.Source,
            null,
            new[]
            {
                new CallTreeNode("child", "Sample.Worker.Loop()", CallTreeNodeKind.Cycle, null, Array.Empty<CallTreeNode>())
            });

        var html = new HtmlTreeRenderer().RenderDocument(tree);

        Assert.Contains("data-zoom-step", html, StringComparison.Ordinal);
        Assert.Contains("kind-cycle", html, StringComparison.Ordinal);
        Assert.Contains("Sample.Worker.Loop() [cycle]", html, StringComparison.Ordinal);
        Assert.Contains("DotTrace Call Tree", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_with_both_view_emits_tabs_and_directional_sections()
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

        var html = new HtmlTreeRenderer().RenderDocument(document, CallTreeView.Both);

        Assert.Contains("id=\"tab-callers\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"tab-callees\" aria-controls=\"panel-callees\" aria-selected=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"panel-callers\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"panel-callees\"", html, StringComparison.Ordinal);
        Assert.Contains("data-tab-panel=\"callers\" data-zoom=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("data-tab-panel=\"callees\" data-zoom=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("Sample.EntryPoint.Run()", html, StringComparison.Ordinal);
        Assert.DoesNotContain("(calls Step()", html, StringComparison.Ordinal);
        Assert.Contains("Loop()", html, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(html, "Sample.Worker.Step()"));
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
