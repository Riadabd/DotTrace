using System.Net;
using System.Text;
using DotTrace.Core.Analysis;

namespace DotTrace.Core.Rendering;

public sealed class HtmlTreeRenderer
{
    public string RenderDocument(CallTreeNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("  <meta charset=\"utf-8\" />");
        builder.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        builder.AppendLine("  <title>DotTrace</title>");
        builder.AppendLine("  <style>");
        builder.AppendLine("    :root { --bg-start: #f8f2df; --bg-end: #e7dfcb; --panel: rgba(255,255,255,0.72); --ink: #1f2833; --muted: #657182; --branch: #8b8479; --source: #1b5fa7; --external: #c46f0a; --cycle: #b32157; --repeated: #7b4bb7; --truncated: #8f6a00; --unresolved: #b42318; --zoom: 1; }");
        builder.AppendLine("    * { box-sizing: border-box; }");
        builder.AppendLine("    body { margin: 0; font-family: \"Iosevka Web\", \"SFMono-Regular\", Consolas, monospace; color: var(--ink); background: radial-gradient(circle at top left, var(--bg-start), var(--bg-end)); min-height: 100vh; }");
        builder.AppendLine("    .page { max-width: 1200px; margin: 0 auto; padding: 24px; }");
        builder.AppendLine("    .hero { display: flex; flex-wrap: wrap; gap: 16px; justify-content: space-between; align-items: center; margin-bottom: 16px; }");
        builder.AppendLine("    h1 { margin: 0; font-size: 1.4rem; letter-spacing: 0.04em; text-transform: uppercase; }");
        builder.AppendLine("    .subtitle { margin: 6px 0 0; color: var(--muted); max-width: 70ch; }");
        builder.AppendLine("    .controls { display: flex; gap: 8px; }");
        builder.AppendLine("    button { border: 0; border-radius: 999px; padding: 10px 14px; background: #2f5d62; color: #f7f3e8; cursor: pointer; font: inherit; }");
        builder.AppendLine("    button:hover { background: #244a4e; }");
        builder.AppendLine("    .legend { display: flex; flex-wrap: wrap; gap: 12px; margin: 0 0 16px; padding: 0; list-style: none; color: var(--muted); }");
        builder.AppendLine("    .legend span { font-weight: 700; }");
        builder.AppendLine("    .shell { background: var(--panel); border: 1px solid rgba(61, 73, 82, 0.12); border-radius: 20px; box-shadow: 0 24px 50px rgba(41, 47, 54, 0.12); overflow: hidden; }");
        builder.AppendLine("    .viewport { overflow: auto; padding: 18px 20px 24px; max-height: 78vh; backdrop-filter: blur(12px); }");
        builder.AppendLine("    .tree { transform: scale(var(--zoom)); transform-origin: top left; width: max-content; min-width: 100%; }");
        builder.AppendLine("    .tree-line { white-space: pre; line-height: 1.55; }");
        builder.AppendLine("    .branch { color: var(--branch); }");
        builder.AppendLine("    .kind-source { color: var(--source); }");
        builder.AppendLine("    .kind-external { color: var(--external); }");
        builder.AppendLine("    .kind-cycle { color: var(--cycle); }");
        builder.AppendLine("    .kind-repeated { color: var(--repeated); }");
        builder.AppendLine("    .kind-truncated { color: var(--truncated); }");
        builder.AppendLine("    .kind-unresolved { color: var(--unresolved); }");
        builder.AppendLine("  </style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("  <div class=\"page\">");
        builder.AppendLine("    <div class=\"hero\">");
        builder.AppendLine("      <div>");
        builder.AppendLine("        <h1>DotTrace Call Tree</h1>");
        builder.AppendLine("        <p class=\"subtitle\">Unicode-first rendering for large static call trees. Scroll freely, or use the zoom controls for denser branches.</p>");
        builder.AppendLine("      </div>");
        builder.AppendLine("      <div class=\"controls\">");
        builder.AppendLine("        <button type=\"button\" data-zoom-step=\"-0.1\">Zoom Out</button>");
        builder.AppendLine("        <button type=\"button\" data-zoom-reset=\"true\">Reset</button>");
        builder.AppendLine("        <button type=\"button\" data-zoom-step=\"0.1\">Zoom In</button>");
        builder.AppendLine("      </div>");
        builder.AppendLine("    </div>");
        builder.AppendLine("    <ul class=\"legend\">");
        builder.AppendLine("      <li><span class=\"kind-source\">source</span></li>");
        builder.AppendLine("      <li><span class=\"kind-external\">external</span></li>");
        builder.AppendLine("      <li><span class=\"kind-cycle\">cycle</span></li>");
        builder.AppendLine("      <li><span class=\"kind-repeated\">seen</span></li>");
        builder.AppendLine("      <li><span class=\"kind-truncated\">max-depth</span></li>");
        builder.AppendLine("      <li><span class=\"kind-unresolved\">unresolved</span></li>");
        builder.AppendLine("    </ul>");
        builder.AppendLine("    <div class=\"shell\">");
        builder.AppendLine("      <div class=\"viewport\">");
        builder.AppendLine("        <div class=\"tree\">");

        AppendLine(builder, prefix: string.Empty, root);

        builder.AppendLine("        </div>");
        builder.AppendLine("      </div>");
        builder.AppendLine("    </div>");
        builder.AppendLine("  </div>");
        builder.AppendLine("  <script>");
        builder.AppendLine("    const root = document.documentElement;");
        builder.AppendLine("    let zoom = 1;");
        builder.AppendLine("    const clamp = value => Math.min(2.5, Math.max(0.4, value));");
        builder.AppendLine("    for (const button of document.querySelectorAll('[data-zoom-step]')) {");
        builder.AppendLine("      button.addEventListener('click', () => {");
        builder.AppendLine("        zoom = clamp(zoom + Number(button.dataset.zoomStep));");
        builder.AppendLine("        root.style.setProperty('--zoom', zoom.toFixed(2));");
        builder.AppendLine("      });");
        builder.AppendLine("    }");
        builder.AppendLine("    document.querySelector('[data-zoom-reset]')?.addEventListener('click', () => {");
        builder.AppendLine("      zoom = 1;");
        builder.AppendLine("      root.style.setProperty('--zoom', '1');");
        builder.AppendLine("    });");
        builder.AppendLine("  </script>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private static void AppendLine(StringBuilder builder, string prefix, CallTreeNode node)
    {
        WriteTreeLine(builder, prefix, string.Empty, node);

        for (var i = 0; i < node.Children.Count; i++)
        {
            AppendNode(builder, prefix: string.Empty, node.Children[i], isLast: i == node.Children.Count - 1);
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

    private static void WriteTreeLine(StringBuilder builder, string prefix, string branch, CallTreeNode node)
    {
        builder.Append("          <div class=\"tree-line\">");
        builder.Append("<span class=\"branch\">");
        builder.Append(WebUtility.HtmlEncode(prefix + branch));
        builder.Append("</span>");
        builder.Append("<span class=\"");
        builder.Append(GetKindClass(node.Kind));
        builder.Append("\">");
        builder.Append(WebUtility.HtmlEncode(FormatLabel(node)));
        builder.Append("</span>");
        builder.AppendLine("</div>");
    }

    private static string FormatLabel(CallTreeNode node)
    {
        return node.Kind switch
        {
            CallTreeNodeKind.Source => node.DisplayText,
            CallTreeNodeKind.External => $"{node.DisplayText} [external]",
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
            CallTreeNodeKind.Source => "kind-source",
            CallTreeNodeKind.External => "kind-external",
            CallTreeNodeKind.Cycle => "kind-cycle",
            CallTreeNodeKind.Repeated => "kind-repeated",
            CallTreeNodeKind.Truncated => "kind-truncated",
            CallTreeNodeKind.Unresolved => "kind-unresolved",
            _ => "kind-source"
        };
    }
}
