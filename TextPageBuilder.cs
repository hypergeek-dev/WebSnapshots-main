// TextPageBuilder.cs
using System.Text;

namespace WebSnapshots;

public static class TextPageBuilder
{
    public static string Build(TextContent content, string visualPageHref)
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        var sb = new StringBuilder();

        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"sv\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("  <title>").Append(E(content.Title)).AppendLine(" - Text Only</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    :root { --bg:#fafaf8; --text:#1a1a1a; --subtle:#666; --border:#ddd; --code-bg:#f5f5f5; --hover:#f0f0f0; }");
        sb.AppendLine("    * { box-sizing: border-box; }");
        sb.AppendLine("    body { margin:0; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif; background: var(--bg); color: var(--text); line-height: 1.6; }");
        sb.AppendLine("    .toolbar { position: sticky; top: 0; background: #fff; border-bottom: 2px solid var(--border); padding: 0.8rem 1.5rem; display: flex; justify-content: space-between; align-items: center; z-index: 100; box-shadow: 0 1px 3px rgba(0,0,0,0.05); }");
        sb.AppendLine("    .toolbar .title { font-weight: 600; font-size: 0.95rem; color: var(--text); }");
        sb.AppendLine("    .toolbar .actions { display: flex; gap: 0.5rem; }");
        sb.AppendLine("    .btn { padding: 0.5rem 1rem; border: 1px solid var(--border); background: #fff; color: var(--text); border-radius: 6px; cursor: pointer; font-size: 0.875rem; text-decoration: none; display: inline-flex; align-items: center; gap: 0.4rem; transition: background 0.15s; }");
        sb.AppendLine("    .btn:hover { background: var(--hover); }");
        sb.AppendLine("    .btn-primary { background: var(--text); color: #fff; border-color: var(--text); }");
        sb.AppendLine("    .btn-primary:hover { background: #333; border-color: #333; }");
        sb.AppendLine("    .container { max-width: 900px; margin: 0 auto; padding: 2rem 1.5rem; }");
        sb.AppendLine("    .meta { margin-bottom: 2rem; padding-bottom: 1.5rem; border-bottom: 1px solid var(--border); }");
        sb.AppendLine("    .meta .page-title { font-size: 1.8rem; font-weight: 700; margin: 0 0 0.5rem 0; color: var(--text); }");
        sb.AppendLine("    .meta .description { color: var(--subtle); font-size: 1rem; margin: 0.5rem 0; }");
        sb.AppendLine("    .meta .url { font-size: 0.875rem; color: var(--subtle); word-break: break-all; }");
        sb.AppendLine("    .meta .url a { color: inherit; }");
        sb.AppendLine("    .section { margin: 2.5rem 0; position: relative; }");
        sb.AppendLine("    .section-header { display: flex; justify-content: space-between; align-items: baseline; margin-bottom: 1rem; }");
        sb.AppendLine("    .section-heading { margin: 0; font-weight: 600; color: var(--text); }");
        sb.AppendLine("    .section-heading.h1 { font-size: 1.75rem; }");
        sb.AppendLine("    .section-heading.h2 { font-size: 1.5rem; }");
        sb.AppendLine("    .section-heading.h3 { font-size: 1.25rem; }");
        sb.AppendLine("    .section-heading.h4 { font-size: 1.1rem; }");
        sb.AppendLine("    .section-heading.h5 { font-size: 1rem; }");
        sb.AppendLine("    .section-heading.h6 { font-size: 0.95rem; }");
        sb.AppendLine("    .copy-section { opacity: 0.6; font-size: 0.75rem; padding: 0.25rem 0.5rem; border: 1px solid var(--border); background: #fff; border-radius: 4px; cursor: pointer; transition: opacity 0.15s; }");
        sb.AppendLine("    .copy-section:hover { opacity: 1; background: var(--hover); }");
        sb.AppendLine("    .copy-section.copied { opacity: 1; background: #e8f5e9; border-color: #4caf50; color: #2e7d32; }");
        sb.AppendLine("    .content-block { margin: 1rem 0; }");
        sb.AppendLine("    .paragraph { margin: 0.75rem 0; font-size: 1rem; color: var(--text); }");
        sb.AppendLine("    .quote { margin: 1rem 0; padding: 1rem 1.5rem; border-left: 4px solid var(--border); background: var(--code-bg); font-style: italic; color: var(--subtle); }");
        sb.AppendLine("    .code { margin: 1rem 0; padding: 1rem; background: var(--code-bg); border: 1px solid var(--border); border-radius: 4px; font-family: 'Courier New', monospace; font-size: 0.875rem; overflow-x: auto; white-space: pre-wrap; word-break: break-all; }");
        sb.AppendLine("    .list { margin: 1rem 0; padding-left: 2rem; }");
        sb.AppendLine("    .list li { margin: 0.4rem 0; }");
        sb.AppendLine("    .links-section { margin-top: 3rem; padding-top: 2rem; border-top: 2px solid var(--border); }");
        sb.AppendLine("    .links-section h2 { font-size: 1.3rem; margin-bottom: 1rem; }");
        sb.AppendLine("    .link-item { margin: 0.5rem 0; font-size: 0.9rem; }");
        sb.AppendLine("    .link-item a { color: #1976d2; text-decoration: none; }");
        sb.AppendLine("    .link-item a:hover { text-decoration: underline; }");
        sb.AppendLine("    .link-item .link-url { color: var(--subtle); margin-left: 0.5rem; font-size: 0.8rem; word-break: break-all; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // Toolbar
        sb.AppendLine("  <div class=\"toolbar\">");
        sb.AppendLine("    <div class=\"title\">📄 Text Only View</div>");
        sb.AppendLine("    <div class=\"actions\">");
        sb.Append("      <a href=\"").Append(E(visualPageHref)).AppendLine("\" class=\"btn btn-primary\">🖼️ Visual View</a>");
        sb.AppendLine("      <button class=\"btn\" onclick=\"window.print()\">🖨️ Print</button>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");

        sb.AppendLine("  <div class=\"container\">");

        // Meta section
        sb.AppendLine("    <div class=\"meta\">");
        sb.Append("      <h1 class=\"page-title\">").Append(E(content.Title)).AppendLine("</h1>");
        
        if (!string.IsNullOrWhiteSpace(content.Description))
        {
            sb.Append("      <div class=\"description\">").Append(E(content.Description)).AppendLine("</div>");
        }
        
        sb.Append("      <div class=\"url\"><a href=\"").Append(E(content.Url)).Append("\" target=\"_blank\" rel=\"noreferrer\">")
          .Append(E(content.Url)).AppendLine("</a></div>");
        sb.AppendLine("    </div>");

        // Content sections
        int sectionId = 0;
        foreach (var section in content.Sections)
        {
            var secId = $"sec_{sectionId++}";
            sb.AppendLine("    <div class=\"section\">");
            sb.AppendLine("      <div class=\"section-header\">");
            sb.Append("        <").Append(section.Level).Append(" class=\"section-heading ").Append(section.Level).Append("\">")
              .Append(E(section.Text)).Append("</").Append(section.Level).AppendLine(">");
            sb.Append("        <button class=\"copy-section\" data-section=\"").Append(secId).AppendLine("\" onclick=\"copySection(this)\">📋 Copy</button>");
            sb.AppendLine("      </div>");

            sb.Append("      <div id=\"").Append(secId).AppendLine("\" class=\"section-content\">");
            
            foreach (var block in section.Content)
            {
                switch (block.Type)
                {
                    case "paragraph":
                        sb.Append("        <div class=\"paragraph\">").Append(E(block.Text)).AppendLine("</div>");
                        break;
                    
                    case "quote":
                        sb.Append("        <blockquote class=\"quote\">").Append(E(block.Text)).AppendLine("</blockquote>");
                        break;
                    
                    case "code":
                        sb.Append("        <pre class=\"code\">").Append(E(block.Text)).AppendLine("</pre>");
                        break;
                    
                    case "list":
                        var tag = block.Ordered ? "ol" : "ul";
                        sb.Append("        <").Append(tag).AppendLine(" class=\"list\">");
                        foreach (var item in block.Items)
                        {
                            sb.Append("          <li>").Append(E(item)).AppendLine("</li>");
                        }
                        sb.Append("        </").Append(tag).AppendLine(">");
                        break;
                }
            }
            
            sb.AppendLine("      </div>");
            sb.AppendLine("    </div>");
        }

        // Links section
        if (content.Links.Count > 0)
        {
            sb.AppendLine("    <div class=\"links-section\">");
            sb.AppendLine("      <h2>Links from this page</h2>");
            
            foreach (var link in content.Links.Take(50)) // Limit to 50 links
            {
                sb.AppendLine("      <div class=\"link-item\">");
                sb.Append("        <a href=\"").Append(E(link.Href)).Append("\" target=\"_blank\" rel=\"noreferrer\">")
                  .Append(E(link.Text)).AppendLine("</a>");
                sb.Append("        <span class=\"link-url\">").Append(E(link.Href)).AppendLine("</span>");
                sb.AppendLine("      </div>");
            }
            
            if (content.Links.Count > 50)
            {
                sb.Append("      <div class=\"link-item\"><em>... and ").Append(content.Links.Count - 50).AppendLine(" more links</em></div>");
            }
            
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("  </div>");

        // JavaScript for copy functionality
        sb.AppendLine("  <script>");
        sb.AppendLine("    function copySection(btn) {");
        sb.AppendLine("      const sectionId = btn.getAttribute('data-section');");
        sb.AppendLine("      const section = document.getElementById(sectionId);");
        sb.AppendLine("      if (!section) return;");
        sb.AppendLine("      ");
        sb.AppendLine("      const text = section.innerText || section.textContent;");
        sb.AppendLine("      ");
        sb.AppendLine("      navigator.clipboard.writeText(text).then(() => {");
        sb.AppendLine("        const originalText = btn.textContent;");
        sb.AppendLine("        btn.textContent = '✓ Copied!';");
        sb.AppendLine("        btn.classList.add('copied');");
        sb.AppendLine("        ");
        sb.AppendLine("        setTimeout(() => {");
        sb.AppendLine("          btn.textContent = originalText;");
        sb.AppendLine("          btn.classList.remove('copied');");
        sb.AppendLine("        }, 2000);");
        sb.AppendLine("      }).catch(err => {");
        sb.AppendLine("        console.error('Copy failed:', err);");
        sb.AppendLine("        alert('Copy failed. Please select and copy manually.');");
        sb.AppendLine("      });");
        sb.AppendLine("    }");
        sb.AppendLine("  </script>");

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }
}