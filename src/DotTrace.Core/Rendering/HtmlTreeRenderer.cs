using System.Net;
using System.Text;
using DotTrace.Core.Analysis;

namespace DotTrace.Core.Rendering;

public sealed class HtmlTreeRenderer
{
    public string RenderDocument(CallTreeNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var selectedRoot = new CallTreeNode(
            root.Id,
            root.DisplayText,
            root.Kind,
            root.Location,
            Array.Empty<CallTreeNode>());
        var document = new CallTreeDocument(selectedRoot, selectedRoot, root);
        return RenderDocument(document, CallTreeView.Callees);
    }

    public string RenderMap(CallTreeNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var selectedRoot = new CallTreeNode(
            root.Id,
            root.DisplayText,
            root.Kind,
            root.Location,
            Array.Empty<CallTreeNode>());
        var document = new CallTreeDocument(selectedRoot, selectedRoot, root);
        return RenderDocument(document, CallTreeView.Callees, "DotTrace Codebase Map", "Project map");
    }

    public string RenderDocument(CallTreeDocument document, CallTreeView view = CallTreeView.Callees)
    {
        return RenderDocument(document, view, "DotTrace Call Tree", "Selected method");
    }

    private string RenderDocument(
        CallTreeDocument document,
        CallTreeView view,
        string title,
        string selectedLabel)
    {
        ArgumentNullException.ThrowIfNull(document);

        var sections = GetSections(document, view);
        var activeSectionId = view == CallTreeView.Both
            ? "full"
            : sections[0].Id;

        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("  <meta charset=\"utf-8\" />");
        builder.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        builder.AppendLine("  <title>DotTrace</title>");
        builder.AppendLine("  <style>");
        builder.AppendLine("    :root { --bg: #f5f7fa; --panel: #ffffff; --ink: #18202a; --muted: #647084; --border: #d8dee8; --branch: #6b7280; --group: #253244; --source: #0b5cad; --external: #b45f06; --boundary: #c05621; --cycle: #b4235a; --repeated: #6d4bc2; --truncated: #8a6400; --unresolved: #c22f24; }");
        builder.AppendLine("    * { box-sizing: border-box; }");
        builder.AppendLine("    body { margin: 0; font-family: \"Iosevka Web\", \"SFMono-Regular\", Consolas, monospace; color: var(--ink); background: var(--bg); min-height: 100vh; }");
        builder.AppendLine("    .page { max-width: 1280px; margin: 0 auto; padding: 20px; }");
        builder.AppendLine("    .header { display: flex; flex-wrap: wrap; gap: 16px; justify-content: space-between; align-items: flex-start; margin-bottom: 14px; }");
        builder.AppendLine("    h1 { margin: 0; font-size: 1.35rem; letter-spacing: 0; }");
        builder.AppendLine("    .selected-method { margin: 8px 0 0; color: var(--ink); max-width: min(100%, 112ch); overflow-wrap: anywhere; }");
        builder.AppendLine("    .selected-method span { color: var(--muted); }");
        builder.AppendLine("    .controls { display: flex; gap: 8px; }");
        builder.AppendLine("    button { border: 1px solid var(--border); border-radius: 6px; padding: 8px 12px; background: var(--panel); color: var(--ink); cursor: pointer; font: inherit; }");
        builder.AppendLine("    button:hover { border-color: #9aa7b8; }");
        builder.AppendLine("    .legend { display: flex; flex-wrap: wrap; gap: 12px; margin: 0 0 16px; padding: 0; list-style: none; color: var(--muted); }");
        builder.AppendLine("    .legend span { font-weight: 700; }");
        builder.AppendLine("    .tabs { display: flex; gap: 4px; margin-bottom: 8px; }");
        builder.AppendLine("    .tab { border-bottom-left-radius: 0; border-bottom-right-radius: 0; }");
        builder.AppendLine("    .tab.active { background: #253244; border-color: #253244; color: #ffffff; }");
        builder.AppendLine("    .shell { background: var(--panel); border: 1px solid var(--border); border-radius: 8px; overflow: hidden; }");
        builder.AppendLine("    .tab-panel[hidden] { display: none; }");
        builder.AppendLine("    .viewport { overflow: auto; padding: 18px 20px 24px; max-height: 78vh; }");
        builder.AppendLine("    .tree { transform: scale(var(--zoom, 1)); transform-origin: top left; width: max-content; min-width: 100%; }");
        builder.AppendLine("    .tree-line { white-space: pre; line-height: 1.55; }");
        builder.AppendLine("    .target-panel { overflow: auto; padding: 18px 20px 24px; max-height: 78vh; }");
        builder.AppendLine("    .target-line { font-weight: 700; }");
        builder.AppendLine("    .target-branch { color: var(--source); }");
        builder.AppendLine("    .kind-target { display: inline-block; color: #053f7f; background: #eaf4ff; border: 1px solid #90b8e8; border-radius: 4px; padding: 0 4px; }");
        builder.AppendLine("    .empty { color: var(--muted); }");
        builder.AppendLine("    .branch { color: var(--branch); }");
        builder.AppendLine("    .kind-group { color: var(--group); font-weight: 700; }");
        builder.AppendLine("    .kind-source { color: var(--source); }");
        builder.AppendLine("    .kind-external { color: var(--external); }");
        builder.AppendLine("    .kind-boundary { color: var(--boundary); }");
        builder.AppendLine("    .kind-cycle { color: var(--cycle); }");
        builder.AppendLine("    .kind-repeated { color: var(--repeated); }");
        builder.AppendLine("    .kind-truncated { color: var(--truncated); }");
        builder.AppendLine("    .kind-unresolved { color: var(--unresolved); }");
        builder.AppendLine("  </style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("  <div class=\"page\">");
        builder.AppendLine("    <header class=\"header\">");
        builder.AppendLine("      <div>");
        builder.Append("        <h1>");
        builder.Append(WebUtility.HtmlEncode(title));
        builder.AppendLine("</h1>");
        builder.Append("        <p class=\"selected-method\"><span>");
        builder.Append(WebUtility.HtmlEncode(selectedLabel));
        builder.Append("</span> ");
        builder.Append(WebUtility.HtmlEncode(document.SelectedRoot.DisplayText));
        builder.AppendLine("</p>");
        builder.AppendLine("      </div>");
        builder.AppendLine("      <div class=\"controls\">");
        builder.AppendLine("        <button type=\"button\" data-zoom-step=\"-0.1\">Zoom Out</button>");
        builder.AppendLine("        <button type=\"button\" data-zoom-reset=\"true\">Reset</button>");
        builder.AppendLine("        <button type=\"button\" data-zoom-step=\"0.1\">Zoom In</button>");
        builder.AppendLine("      </div>");
        builder.AppendLine("    </header>");
        builder.AppendLine("    <ul class=\"legend\">");
        builder.AppendLine("      <li><span class=\"kind-source\">source</span></li>");
        builder.AppendLine("      <li><span class=\"kind-external\">external</span></li>");
        builder.AppendLine("      <li><span class=\"kind-boundary\">boundary</span></li>");
        builder.AppendLine("      <li><span class=\"kind-cycle\">cycle</span></li>");
        builder.AppendLine("      <li><span class=\"kind-repeated\">seen</span></li>");
        builder.AppendLine("      <li><span class=\"kind-truncated\">max-depth</span></li>");
        builder.AppendLine("      <li><span class=\"kind-unresolved\">unresolved</span></li>");
        builder.AppendLine("    </ul>");
        builder.AppendLine("    <nav class=\"tabs\" role=\"tablist\" aria-label=\"Call tree views\">");
        foreach (var section in sections)
        {
            var isActive = section.Id == activeSectionId;
            builder.Append("      <button type=\"button\" class=\"tab");
            builder.Append(isActive ? " active" : string.Empty);
            builder.Append("\" role=\"tab\" id=\"tab-");
            builder.Append(section.Id);
            builder.Append("\" aria-controls=\"panel-");
            builder.Append(section.Id);
            builder.Append("\" aria-selected=\"");
            builder.Append(isActive ? "true" : "false");
            builder.Append("\" data-tab-target=\"");
            builder.Append(section.Id);
            builder.Append("\">");
            builder.Append(WebUtility.HtmlEncode(section.Label));
            builder.AppendLine("</button>");
        }

        builder.AppendLine("    </nav>");
        builder.AppendLine("    <div class=\"shell\">");
        foreach (var section in sections)
        {
            if (section.IsFullView)
            {
                AppendTargetPanel(builder, document, isActive: section.Id == activeSectionId);
            }
            else
            {
                AppendPanel(builder, section, isActive: section.Id == activeSectionId);
            }
        }

        builder.AppendLine("    </div>");
        builder.AppendLine("  </div>");
        builder.AppendLine("  <script>");
        builder.AppendLine("    const tabs = Array.from(document.querySelectorAll('[data-tab-target]'));");
        builder.AppendLine("    const panels = Array.from(document.querySelectorAll('[data-tab-panel]'));");
        builder.AppendLine("    const clamp = value => Math.min(2.5, Math.max(0.4, value));");
        builder.AppendLine("    const activePanel = () => panels.find(panel => !panel.hidden) || document.querySelector('.target-panel[data-zoom]');");
        builder.AppendLine("    const setZoom = (panel, value) => {");
        builder.AppendLine("      const zoom = clamp(value);");
        builder.AppendLine("      panel.dataset.zoom = zoom.toFixed(2);");
        builder.AppendLine("      panel.style.setProperty('--zoom', zoom.toFixed(2));");
        builder.AppendLine("    };");
        builder.AppendLine("    const activate = name => {");
        builder.AppendLine("      for (const tab of tabs) {");
        builder.AppendLine("        const selected = tab.dataset.tabTarget === name;");
        builder.AppendLine("        tab.classList.toggle('active', selected);");
        builder.AppendLine("        tab.setAttribute('aria-selected', String(selected));");
        builder.AppendLine("      }");
        builder.AppendLine("      for (const panel of panels) {");
        builder.AppendLine("        panel.hidden = panel.dataset.tabPanel !== name;");
        builder.AppendLine("      }");
        builder.AppendLine("    };");
        builder.AppendLine("    for (const tab of tabs) {");
        builder.AppendLine("      tab.addEventListener('click', () => activate(tab.dataset.tabTarget));");
        builder.AppendLine("    }");
        builder.AppendLine("    for (const button of document.querySelectorAll('[data-zoom-step]')) {");
        builder.AppendLine("      button.addEventListener('click', () => {");
        builder.AppendLine("        const panel = activePanel();");
        builder.AppendLine("        if (!panel) return;");
        builder.AppendLine("        setZoom(panel, Number(panel.dataset.zoom || '1') + Number(button.dataset.zoomStep));");
        builder.AppendLine("      });");
        builder.AppendLine("    }");
        builder.AppendLine("    document.querySelector('[data-zoom-reset]')?.addEventListener('click', () => {");
        builder.AppendLine("      const panel = activePanel();");
        builder.AppendLine("      if (panel) setZoom(panel, 1);");
        builder.AppendLine("    });");
        builder.AppendLine("  </script>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private static TreeSection[] GetSections(CallTreeDocument document, CallTreeView view)
    {
        return view switch
        {
            CallTreeView.Callers => [new TreeSection("callers", "Callers", document.CallersTree)],
            CallTreeView.Callees => [new TreeSection("callees", "Callees", document.CalleesTree)],
            CallTreeView.Both =>
            [
                new TreeSection("callers", "Callers", document.CallersTree),
                new TreeSection("callees", "Callees", document.CalleesTree),
                new TreeSection("full", "Full", document.SelectedRoot, IsFullView: true)
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, "Unsupported call tree view.")
        };
    }

    private static void AppendPanel(StringBuilder builder, TreeSection section, bool isActive)
    {
        builder.Append("      <section id=\"panel-");
        builder.Append(section.Id);
        builder.Append("\" class=\"tab-panel\" role=\"tabpanel\" aria-labelledby=\"tab-");
        builder.Append(section.Id);
        builder.Append("\" data-tab-panel=\"");
        builder.Append(section.Id);
        builder.Append("\" data-zoom=\"1\" style=\"--zoom: 1\"");
        if (!isActive)
        {
            builder.Append(" hidden");
        }

        builder.AppendLine(">");
        builder.AppendLine("        <div class=\"viewport\">");
        builder.AppendLine("          <div class=\"tree\">");

        if (section.Tree.Children.Count == 0)
        {
            builder.Append("            <div class=\"empty\">No ");
            builder.Append(WebUtility.HtmlEncode(section.Label.ToLowerInvariant()));
            builder.AppendLine(" found.</div>");
        }
        else
        {
            for (var i = 0; i < section.Tree.Children.Count; i++)
            {
                AppendNode(builder, prefix: string.Empty, section.Tree.Children[i], isLast: i == section.Tree.Children.Count - 1);
            }
        }

        builder.AppendLine("          </div>");
        builder.AppendLine("        </div>");
        builder.AppendLine("      </section>");
    }

    private static void AppendTargetPanel(StringBuilder builder, CallTreeDocument document, bool isActive)
    {
        builder.Append("      <section id=\"panel-full\" class=\"tab-panel target-panel\" role=\"tabpanel\" aria-labelledby=\"tab-full\" data-tab-panel=\"full\" data-zoom=\"1\" style=\"--zoom: 1\"");
        if (!isActive)
        {
            builder.Append(" hidden");
        }

        builder.AppendLine(">");
        builder.AppendLine("        <div class=\"tree\">");
        AppendFullTree(builder, document);
        builder.AppendLine("        </div>");
        builder.AppendLine("      </section>");
    }

    private static void AppendFullTree(StringBuilder builder, CallTreeDocument document)
    {
        var callerPaths = GetCallerPaths(document.CallersTree.Children);
        if (callerPaths.Count == 0)
        {
            AppendTargetWithCallees(builder, prefix: string.Empty, document);
            return;
        }

        for (var i = 0; i < callerPaths.Count; i++)
        {
            AppendCallerPath(builder, callerPaths[i], isLast: i == callerPaths.Count - 1, document);
        }
    }

    private static IReadOnlyList<IReadOnlyList<CallTreeNode>> GetCallerPaths(IReadOnlyList<CallTreeNode> callers)
    {
        var paths = new List<IReadOnlyList<CallTreeNode>>();
        foreach (var caller in callers)
        {
            AddCallerPaths(caller, new List<CallTreeNode>(), paths);
        }

        return paths;
    }

    private static void AddCallerPaths(
        CallTreeNode node,
        List<CallTreeNode> path,
        List<IReadOnlyList<CallTreeNode>> paths)
    {
        path.Add(node);
        if (node.Children.Count == 0)
        {
            paths.Add(path.AsEnumerable().Reverse().ToArray());
        }
        else
        {
            foreach (var child in node.Children)
            {
                AddCallerPaths(child, path, paths);
            }
        }

        path.RemoveAt(path.Count - 1);
    }

    private static void AppendCallerPath(
        StringBuilder builder,
        IReadOnlyList<CallTreeNode> path,
        bool isLast,
        CallTreeDocument document)
    {
        var prefix = string.Empty;
        for (var i = 0; i < path.Count; i++)
        {
            var isLastAtLevel = i == 0 ? isLast : true;
            WriteTreeLine(builder, prefix, isLastAtLevel ? "└── " : "├── ", path[i]);
            prefix += isLastAtLevel ? "    " : "│   ";
        }

        AppendTargetWithCallees(builder, prefix, document);
    }

    private static void AppendTargetWithCallees(StringBuilder builder, string prefix, CallTreeDocument document)
    {
        WriteTargetLine(builder, prefix, document.SelectedRoot);

        var targetChildPrefix = prefix + "    ";
        if (document.CalleesTree.Children.Count > 0)
        {
            for (var i = 0; i < document.CalleesTree.Children.Count; i++)
            {
                AppendNode(
                    builder,
                    targetChildPrefix,
                    document.CalleesTree.Children[i],
                    isLast: i == document.CalleesTree.Children.Count - 1);
            }
        }
        else
        {
            WriteTreeLine(builder, targetChildPrefix, branch: "└── ", label: "No callees found", labelClass: "empty");
        }
    }

    private static void AppendNode(StringBuilder builder, string prefix, CallTreeNode node, bool isLast)
    {
        var branch = isLast ? "└── " : "├── ";
        WriteTreeLine(builder, prefix, branch, node);

        var childPrefix = prefix + (isLast ? "    " : "│   ");
        for (var i = 0; i < node.Children.Count; i++)
        {
            AppendNode(builder, childPrefix, node.Children[i], i == node.Children.Count - 1);
        }
    }

    private readonly record struct TreeSection(string Id, string Label, CallTreeNode Tree, bool IsFullView = false);

    private static void WriteTreeLine(StringBuilder builder, string prefix, string branch, CallTreeNode node)
    {
        WriteTreeLine(builder, prefix, branch, FormatLabel(node), GetKindClass(node.Kind));
    }

    private static void WriteTreeLine(StringBuilder builder, string prefix, string branch, string label, string labelClass)
    {
        builder.Append("          <div class=\"tree-line\">");
        builder.Append("<span class=\"branch\">");
        builder.Append(WebUtility.HtmlEncode(prefix + branch));
        builder.Append("</span>");
        builder.Append("<span class=\"");
        builder.Append(labelClass);
        builder.Append("\">");
        builder.Append(WebUtility.HtmlEncode(label));
        builder.Append("</span>");
        builder.AppendLine("</div>");
    }

    private static void WriteTargetLine(StringBuilder builder, string prefix, CallTreeNode target)
    {
        builder.Append("          <div class=\"tree-line target-line\">");
        builder.Append("<span class=\"branch target-branch\">");
        builder.Append(WebUtility.HtmlEncode(prefix + "╞══ "));
        builder.Append("</span>");
        builder.Append("<span class=\"kind-target\">");
        builder.Append(WebUtility.HtmlEncode($"{FormatLabel(target)} [target]"));
        builder.Append("</span>");
        builder.AppendLine("</div>");
    }

    private static string FormatLabel(CallTreeNode node)
    {
        return node.Kind switch
        {
            CallTreeNodeKind.Group => node.DisplayText,
            CallTreeNodeKind.Source => node.DisplayText,
            CallTreeNodeKind.External => $"{node.DisplayText} [external]",
            CallTreeNodeKind.Boundary => $"{node.DisplayText} [boundary]",
            CallTreeNodeKind.Cycle => $"{node.DisplayText} [cycle]",
            CallTreeNodeKind.Repeated => $"{node.DisplayText} [seen]",
            CallTreeNodeKind.Truncated => $"{node.DisplayText} [max-depth]",
            CallTreeNodeKind.Unresolved => $"{node.DisplayText} [unresolved]",
            _ => node.DisplayText
        };
    }

    private static string GetKindClass(CallTreeNodeKind kind)
    {
        return kind switch
        {
            CallTreeNodeKind.Group => "kind-group",
            CallTreeNodeKind.Source => "kind-source",
            CallTreeNodeKind.External => "kind-external",
            CallTreeNodeKind.Boundary => "kind-boundary",
            CallTreeNodeKind.Cycle => "kind-cycle",
            CallTreeNodeKind.Repeated => "kind-repeated",
            CallTreeNodeKind.Truncated => "kind-truncated",
            CallTreeNodeKind.Unresolved => "kind-unresolved",
            _ => "kind-source"
        };
    }
}
