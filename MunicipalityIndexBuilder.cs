// MunicipalityIndexBuilder.cs
using System.Text;
using System.Text.Json;

namespace WebSnapshots;

public sealed class MunicipalityIndexBuilder
{
    private readonly Logger? _log;

    public MunicipalityIndexBuilder(Logger? log = null)
    {
        _log = log;
    }

    private sealed class ScrapeRunMeta
    {
        public string Municipality { get; set; } = "";
        public string Host { get; set; } = "";
        public string StartUrl { get; set; } = "";
        public string Status { get; set; } = "";
        public int PagesDone { get; set; }
        public string ScrapeFolder { get; set; } = ""; // folder name only, e.g. 2026-03-05_1745
        public DateTimeOffset GeneratedLocal { get; set; }
        public string EntryRel { get; set; } = "";     // e.g. "2026-03-05_1745/index.html"
        public string ViewerRel { get; set; } = "";    // e.g. "2026-03-14/viewer.htm"
    }

    public async Task BuildAsync(string runOutputDir, string municipalityFolderName)
    {
        var muniDir = Path.Combine(runOutputDir, municipalityFolderName);
        if (!Directory.Exists(muniDir)) return;

        var metas = new List<ScrapeRunMeta>();

        foreach (var sub in Directory.EnumerateDirectories(muniDir))
        {
            var scrapeFolder = Path.GetFileName(sub);
            if (string.IsNullOrWhiteSpace(scrapeFolder)) continue;

            var metaPath = Path.Combine(sub, "scrape.json");
            if (!File.Exists(metaPath)) continue;

            try
            {
                var json = await File.ReadAllTextAsync(metaPath, Encoding.UTF8);
                var meta = JsonSerializer.Deserialize<ScrapeRunMeta>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (meta == null) continue;

                // ensure folder-local rels
                meta.ScrapeFolder = scrapeFolder;
                if (string.IsNullOrWhiteSpace(meta.EntryRel))
                    meta.EntryRel = $"{scrapeFolder}/index.html";
                if (string.IsNullOrWhiteSpace(meta.ViewerRel))
                    meta.ViewerRel = $"{scrapeFolder}/viewer.htm";

                metas.Add(meta);
            }
            catch (Exception ex)
            {
                _log?.Warn($"Municipality index: failed reading {metaPath}: {ex.Message}");
            }
        }

        metas.Sort((a, b) => b.GeneratedLocal.CompareTo(a.GeneratedLocal));

        // Write index.json for this municipality (easy linking)
        var outJsonPath = Path.Combine(muniDir, "index.json");
        var jsonOut = JsonSerializer.Serialize(metas, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outJsonPath, jsonOut, Encoding.UTF8);

        // Write municipality index.html
        var sb = new StringBuilder();
        foreach (var m in metas)
        {
            var entry = E($"{m.ScrapeFolder}/index.html");
            var status = E(m.Status);
            var host = E(m.Host);
            var pages = m.PagesDone;
            var when = m.GeneratedLocal.ToString("yyyy-MM-dd HH:mm");

            sb.AppendLine($@"<li>
  <a href=""{entry}"">{E(when)}</a>
  <small class=""muted"">({host})</small>
  <span class=""badge"">{status}</span>
  <small class=""muted"">pages:{pages}</small>
</li>");
        }

        var html = $@"<!doctype html>
<html lang=""sv"">
<meta charset=""utf-8"">
<title>{E(municipalityFolderName)} - Web Snapshots</title>
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<style>
:root {{ --fg:#222; --muted:#666; --accent:#7a003c; --chip:#eef; --border:#ddd; }}
body{{font-family:system-ui,Segoe UI,Arial,sans-serif;margin:2rem;color:var(--fg);max-width:1100px}}
header h1{{margin:0 0 .25rem 0;font-size:1.8rem}}
header .sub{{color:var(--muted);font-size:.95rem}}
ul{{padding-left:1.2rem;margin-top:1rem}}
li{{margin:.55rem 0}}
.badge{{background:var(--chip);color:#334;padding:.1rem .4rem;border-radius:.4rem;font-size:.75rem;margin-left:.5rem}}
.muted{{color:var(--muted)}}
a{{color:var(--accent);text-decoration:none}}
a:hover{{text-decoration:underline}}
.code{{font-family:ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', 'Courier New', monospace; font-size:.9rem}}
</style>
<header>
  <div class=""sub""><a href=""../index.htm"">← Run index</a></div>
  <h1>{E(municipalityFolderName)}</h1>
  <div class=""sub"">Scrapes: {metas.Count}</div>
  <div class=""sub"">JSON: <span class=""code"">index.json</span></div>
</header>

<h2>Scrape runs</h2>
<ul>
{sb}
</ul>
</html>";

        var outHtmlPath = Path.Combine(muniDir, "index.html");
        await File.WriteAllTextAsync(outHtmlPath, html, Encoding.UTF8);

        // Backward compatible
        var outHtmPath = Path.Combine(muniDir, "index.htm");
        await File.WriteAllTextAsync(outHtmPath, html, Encoding.UTF8);
    }

    private static string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}