using System.Text;

namespace WebSnapshots;

public sealed class SiteScrapeManifest
{
    public string Municipality { get; set; } = "";
    public string Host { get; set; } = "";
    public string StartUrl { get; set; } = "";
    public string Status { get; set; } = "PARTIAL";
    public string Reason { get; set; } = "";
    public string Note { get; set; } = "";
    public int PagesDone { get; set; }
    public int ScreenshotsDone { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset GeneratedLocal { get; set; }
    public string EntryRel { get; set; } = "";
    public string ViewerRel { get; set; } = "";
}

public static class SiteRunArtifacts
{
    public static async Task WriteAsync(string scrapeRootDir, SiteScrapeManifest meta)
    {
        meta.GeneratedLocal = DateTimeOffset.Now;

        var viewerExists = File.Exists(Path.Combine(scrapeRootDir, "viewer.htm"));
        if (!viewerExists)
            meta.ViewerRel = "";

        await AtomicWrite.WriteJsonAtomicAsync(Path.Combine(scrapeRootDir, "scrape.json"), meta);

        var html = BuildEntryHtml(meta, viewerExists);
        await AtomicWrite.WriteAllTextAtomicAsync(Path.Combine(scrapeRootDir, "index.html"), html, Encoding.UTF8);
        await AtomicWrite.WriteAllTextAtomicAsync(Path.Combine(scrapeRootDir, "index.htm"), html, Encoding.UTF8);
    }

    public static RunSiteItem ToRunSiteItem(SiteScrapeManifest meta)
        => new()
        {
            Host = meta.Host,
            DisplayName = meta.Municipality,
            ViewerRel = meta.ViewerRel,
            EntryRel = meta.EntryRel,
            Status = meta.Status,
            PagesDone = meta.PagesDone,
            ScreenshotsDone = meta.ScreenshotsDone,
            Reason = meta.Reason,
            Note = meta.Note,
            StartedAt = meta.StartedAt,
            FinishedAt = meta.FinishedAt
        };

    private static string BuildEntryHtml(SiteScrapeManifest meta, bool viewerExists)
    {
        static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        var complete = meta.Status.Equals("OK", StringComparison.OrdinalIgnoreCase);
        var summary = complete
            ? "Scrape completed successfully."
            : "This archive is incomplete. Review the status and reason before using it as a final archive.";
        var viewerBlock = viewerExists
            ? """<p><a href="viewer.htm#start">Open available viewer</a></p>"""
            : """<p class="warn">No viewer is available for this scrape.</p>""";
        var reasonBlock = string.IsNullOrWhiteSpace(meta.Reason)
            ? ""
            : $"""<p><strong>Reason:</strong> {E(meta.Reason)}</p>""";
        var noteBlock = string.IsNullOrWhiteSpace(meta.Note)
            ? ""
            : $"""<p><strong>Note:</strong> {E(meta.Note)}</p>""";

        return $@"<!doctype html>
<html lang=""sv"">
<meta charset=""utf-8"">
<title>{E(meta.Municipality)} - {E(meta.Host)}</title>
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<style>
:root {{ --fg:#222; --muted:#666; --accent:#7a003c; --chip:#eef; --border:#ddd; --warn:#8a1c1c; --warnbg:#fff2f2; }}
body{{font-family:system-ui,Segoe UI,Arial,sans-serif;margin:2rem;color:var(--fg);max-width:1100px}}
h1{{margin:0 0 .25rem 0;font-size:1.8rem}}
.sub{{color:var(--muted);font-size:.95rem;margin:.15rem 0}}
.badge{{background:var(--chip);color:#334;padding:.1rem .4rem;border-radius:.4rem;font-size:.75rem;margin-left:.5rem}}
.card{{border:1px solid var(--border);border-radius:12px;padding:1rem;margin-top:1rem}}
.incomplete{{border-color:#e0a0a0;background:var(--warnbg)}}
.warn{{color:var(--warn)}}
.code{{font-family:ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', 'Courier New', monospace;font-size:.9rem}}
a{{color:var(--accent);text-decoration:none}} a:hover{{text-decoration:underline}}
</style>
<div class=""sub""><a href=""../index.htm"">&larr; {E(meta.Municipality)} index</a> | <a href=""../../index.htm"">All runs</a></div>
<h1>{E(meta.Municipality)} <small class=""sub"">({E(meta.Host)})</small></h1>
<div class=""sub""><span class=""badge"">{E(meta.Status)}</span> pages:{meta.PagesDone} screenshots:{meta.ScreenshotsDone}</div>
<div class=""sub"">Start URL: <span class=""code"">{E(meta.StartUrl)}</span></div>
<div class=""card {(complete ? "" : "incomplete")}"">
  <h2 style=""margin-top:0"">{(complete ? "Archive ready" : "Incomplete archive")}</h2>
  <p>{E(summary)}</p>
  {reasonBlock}
  {noteBlock}
  {viewerBlock}
  <p class=""sub"">Metadata: <span class=""code"">scrape.json</span></p>
</div>
</html>";
    }
}
