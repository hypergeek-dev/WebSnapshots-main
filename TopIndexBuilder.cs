
using System.Text;

namespace WebSnapshots;

public sealed class TopIndexBuilder
{
    private readonly SnapshotConfig _cfg;
    public TopIndexBuilder(SnapshotConfig cfg) { _cfg = cfg; }

    public async Task BuildAsync(
        string outputDir,
        string runFolderName,
        List<(string Host, string DisplayName, string ViewerRel, string Status, int PagesDone)> items)
    {
        var sb = new StringBuilder();

        foreach (var it in items.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var name = E(it.DisplayName);
            var host = E(it.Host);
            var status = E(it.Status);

            var link = "../../" + E(it.ViewerRel);
            if (!link.Contains('#')) link += "#start";

            sb.AppendLine($@"<li><a href=""{link}"">{name}</a> <small class=""muted"">({host})</small> <span class=""badge"">{status}</span> <small class=""muted"">pages:{it.PagesDone}</small></li>");
        }

        var now = DateTime.Now;

        var html = $@"<!doctype html>
<html lang=""sv"">
<meta charset=""utf-8"">
<title>Web Snapshots - {E(runFolderName)}</title>
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<style>
:root {{ --fg:#222; --muted:#666; --accent:#7a003c; --chip:#eef; --border:#ddd; }}
body{{font-family:system-ui,Segoe UI,Arial,sans-serif;margin:2rem;color:var(--fg);max-width:1100px}}
header h1{{margin:0 0 .25rem 0;font-size:1.8rem}}
header .sub{{color:var(--muted);font-size:.95rem}}
ul{{padding-left:1.2rem;margin-top:1rem}}
li{{margin:.45rem 0}}
.badge{{background:var(--chip);color:#334;padding:.1rem .4rem;border-radius:.4rem;font-size:.75rem;margin-left:.5rem}}
.muted{{color:var(--muted)}}
a{{color:var(--accent);text-decoration:none}}
a:hover{{text-decoration:underline}}
</style>
<header>
  <div class=""sub""><a href=""../../index.htm"">← All runs</a></div>
  <h1>Web Snapshots</h1>
  <div class=""sub"">Run: {E(runFolderName)}</div>
  <div class=""sub"">Generated: {now:yyyy-MM-dd HH:mm}</div>
</header>
<h2>Sites</h2>
<ul>
{sb}
</ul>";

        var indexPath = Path.Combine(outputDir, "index.htm");
        await File.WriteAllTextAsync(indexPath, html, Encoding.UTF8);
    }

    private static string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}
