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
        AppendTree(builder, root, renderOptions.UseColor);
        return builder.ToString();
    }

    public string Render(CallTreeDocument document, CallTreeView view, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        return view switch
        {
            CallTreeView.Callees => Render(document.CalleesTree, options),
            CallTreeView.Callers => RenderCallers(document, options),
            CallTreeView.Both => RenderBoth(document, options),
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, "Unsupported call tree view.")
        };
    }

    private static string RenderCallers(CallTreeDocument document, RenderOptions? options)
    {
        var renderOptions = options ?? new RenderOptions();
        var builder = new StringBuilder();
        builder.Append("Callers of ");
        builder.AppendLine(FormatLabel(document.SelectedRoot, renderOptions.UseColor));
        AppendTree(builder, document.CallersTree, renderOptions.UseColor);
        return builder.ToString();
    }

    private static string RenderBoth(CallTreeDocument document, RenderOptions? options)
    {
        var renderOptions = options ?? new RenderOptions();
        var builder = new StringBuilder();
        builder.AppendLine("Callers");
        AppendChildrenOrEmpty(builder, document.CallersTree, "callers", renderOptions.UseColor);
        builder.AppendLine();
        builder.AppendLine("Target");
        builder.AppendLine(FormatTargetLabel(document.SelectedRoot, renderOptions.UseColor));
        builder.AppendLine();
        builder.AppendLine("Callees");
        AppendChildrenOrEmpty(builder, document.CalleesTree, "callees", renderOptions.UseColor);
        return builder.ToString();
    }

    private static void AppendChildrenOrEmpty(StringBuilder builder, CallTreeNode root, string emptyLabel, bool useColor)
    {
        if (root.Children.Count == 0)
        {
            builder.Append("(no ");
            builder.Append(emptyLabel);
            builder.AppendLine(" found)");
            return;
        }

        for (var i = 0; i < root.Children.Count; i++)
        {
            AppendNode(builder, root.Children[i], prefix: string.Empty, isLast: i == root.Children.Count - 1, useColor);
        }
    }

    private static void AppendTree(StringBuilder builder, CallTreeNode root, bool useColor)
    {
        builder.AppendLine(FormatLabel(root, useColor));

        for (var i = 0; i < root.Children.Count; i++)
        {
            AppendNode(builder, root.Children[i], prefix: string.Empty, isLast: i == root.Children.Count - 1, useColor);
        }
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

    private static string FormatTargetLabel(CallTreeNode node, bool useColor)
    {
        var label = $"=> {FormatLabel(node, useColor: false)} [target]";
        return useColor ? WrapAnsi(label, "1;38;5;33") : label;
    }

    private static string WrapAnsi(string value, string colorCode)
    {
        return $"\u001b[{colorCode}m{value}\u001b[0m";
    }
}
