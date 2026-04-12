using System.Text;
using DotTrace.Core.Analysis;

namespace DotTrace.Core.Rendering;

public sealed class TextTreeRenderer
{
    public string Render(CallTreeNode root, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        var renderOptions = options ?? new RenderOptions();
        var builder = new StringBuilder();
        builder.AppendLine(FormatLabel(root, renderOptions.UseColor));

        for (var i = 0; i < root.Children.Count; i++)
        {
            AppendNode(builder, root.Children[i], prefix: string.Empty, isLast: i == root.Children.Count - 1, renderOptions.UseColor);
        }

        return builder.ToString();
    }

    private static void AppendNode(StringBuilder builder, CallTreeNode node, string prefix, bool isLast, bool useColor)
    {
        var branch = isLast ? "└── " : "├── ";
        builder.Append(prefix);
        builder.Append(FormatBranch(branch, useColor));
        builder.AppendLine(FormatLabel(node, useColor));

        var childPrefix = prefix + (isLast ? "    " : "│   ");
        for (var i = 0; i < node.Children.Count; i++)
        {
            AppendNode(builder, node.Children[i], childPrefix, i == node.Children.Count - 1, useColor);
        }
    }

    private static string FormatBranch(string branch, bool useColor)
    {
        return useColor ? WrapAnsi(branch, "38;5;244") : branch;
    }

    private static string FormatLabel(CallTreeNode node, bool useColor)
    {
        var label = node.Kind switch
        {
            CallTreeNodeKind.Source => node.DisplayText,
            CallTreeNodeKind.External => $"{node.DisplayText} [external]",
            CallTreeNodeKind.Cycle => $"{node.DisplayText} [cycle]",
            CallTreeNodeKind.Repeated => $"{node.DisplayText} [seen]",
            CallTreeNodeKind.Truncated => $"{node.DisplayText} [max-depth]",
            CallTreeNodeKind.Unresolved => $"{node.DisplayText} [unresolved]",
            _ => node.DisplayText
        };

        if (!useColor)
        {
            return label;
        }

        return node.Kind switch
        {
            CallTreeNodeKind.Source => WrapAnsi(label, "38;5;33"),
            CallTreeNodeKind.External => WrapAnsi(label, "38;5;214"),
            CallTreeNodeKind.Cycle => WrapAnsi(label, "38;5;198"),
            CallTreeNodeKind.Repeated => WrapAnsi(label, "38;5;141"),
            CallTreeNodeKind.Truncated => WrapAnsi(label, "38;5;220"),
            CallTreeNodeKind.Unresolved => WrapAnsi(label, "38;5;203"),
            _ => label
        };
    }

    private static string WrapAnsi(string value, string colorCode)
    {
        return $"\u001b[{colorCode}m{value}\u001b[0m";
    }
}

