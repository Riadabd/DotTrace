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
}
