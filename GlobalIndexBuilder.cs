
using System.Text;
using System.Text.Json;

namespace WebSnapshots;

public static class GlobalIndexBuilder
{
    public static async Task BuildAsync(string outputBaseDir)
    {
        outputBaseDir = Path.GetFullPath(outputBaseDir);

        var manifests = new List<RunManifest>();

        var runsDir = Path.Combine(outputBaseDir, "_runs");
        if (Directory.Exists(runsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(runsDir))
            {
                var mf = Path.Combine(dir, "run.json");
                if (!File.Exists(mf)) continue;

                try
                {
                    var json = await File.ReadAllTextAsync(mf, Encoding.UTF8);
                    var m = JsonSerializer.Deserialize<RunManifest>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (m != null) manifests.Add(m);
                }
                catch
                {
                    // ignore broken manifest
                }
            }
        }

        manifests.Sort((a, b) => string.CompareOrdinal(b.RunFolderName, a.RunFolderName)); // newest-ish by folder name

        var sb = new StringBuilder();

        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"sv\">");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine("<title>Web Snapshots</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(":root{--fg:#222;--muted:#666;--accent:#7a003c;--chip:#eef;--border:#ddd;}");
        sb.AppendLine("body{font-family:system-ui,Segoe UI,Arial,sans-serif;margin:2rem;color:var(--fg);max-width:1200px}");
        sb.AppendLine("header h1{margin:0 0 .25rem 0;font-size:1.8rem}");
        sb.AppendLine("header .sub{color:var(--muted);font-size:.95rem}");
        sb.AppendLine("a{color:var(--accent);text-decoration:none}");
        sb.AppendLine("a:hover{text-decoration:underline}");
        sb.AppendLine(".run{border:1px solid var(--border);border-radius:12px;padding:12px 14px;margin:14px 0;background:#fff}");
        sb.AppendLine(".runTitle{display:flex;gap:10px;align-items:baseline;flex-wrap:wrap}");
        sb.AppendLine(".pill{background:var(--chip);padding:.15rem .5rem;border-radius:.5rem;font-size:.75rem;color:#334}");
        sb.AppendLine("ul{padding-left:1.2rem;margin:.75rem 0 0 0}");
        sb.AppendLine("li{margin:.35rem 0}");
        sb.AppendLine(".muted{color:var(--muted)}");
        sb.AppendLine("</style>");
        sb.AppendLine("<header>");
        sb.AppendLine("  <h1>Web Snapshots</h1>");
        sb.AppendLine("  <div class=\"sub\">All dated runs in this output folder.</div>");
        sb.AppendLine("</header>");

        if (manifests.Count == 0)
        {
            sb.AppendLine("<p class=\"muted\">No runs found (no run.json files yet).</p>");
        }
        else
        {
            foreach (var m in manifests)
            {
                var runFolder = m.RunFolderName;
                var runIndexHref = $"_runs/{E(runFolder)}/index.htm";
                sb.AppendLine("<div class=\"run\">");
                sb.AppendLine("  <div class=\"runTitle\">");
                sb.Append("    <h2 style=\"margin:0\"><a href=\"").Append(runIndexHref).Append("\">").Append(E(runFolder)).AppendLine("</a></h2>");
                sb.Append("    <span class=\"pill\">sites: ").Append(m.Sites).AppendLine("</span>");
                sb.Append("    <span class=\"pill\">generated: ").Append(E(m.GeneratedLocal.ToString("yyyy-MM-dd HH:mm"))).AppendLine("</span>");
                sb.AppendLine("  </div>");

                if (m.Results != null && m.Results.Count > 0)
                {
                    sb.AppendLine("  <ul>");
                    foreach (var it in m.Results.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
                    {
                        var viewerHref = E(it.ViewerRel);
                        if (!viewerHref.Contains('#')) viewerHref += "#start";

                        sb.Append("    <li><a href=\"").Append(viewerHref).Append("\">").Append(E(it.DisplayName)).Append("</a>");
                        sb.Append(" <small class=\"muted\">(").Append(E(it.Host)).Append(")</small>");
                        sb.Append(" <span class=\"pill\">").Append(E(it.Status)).Append("</span>");
                        sb.Append(" <small class=\"muted\">pages:").Append(it.PagesDone).AppendLine("</small></li>");
                    }
                    sb.AppendLine("  </ul>");
                }
                else
                {
                    sb.AppendLine("  <div class=\"muted\">No site results recorded.</div>");
                }

                sb.AppendLine("</div>");
            }
        }

        var outPath = Path.Combine(outputBaseDir, "index.htm");
        Directory.CreateDirectory(outputBaseDir);
        await File.WriteAllTextAsync(outPath, sb.ToString(), Encoding.UTF8);
    }

    private static string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}
