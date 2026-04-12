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
}
