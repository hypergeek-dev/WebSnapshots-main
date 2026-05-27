// AppRunner.cs
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WebSnapshots;

public static class AppRunner
{
    public static async Task RunAsync(
        SnapshotConfig cfg,
        List<string> urls,
        Action<string> uiLog,
        CancellationToken ct,
        PauseController pause)
    {
        ct.ThrowIfCancellationRequested();

        // Resolve base output
        cfg.OutputBaseDir = Path.GetFullPath(
            Path.IsPathRooted(cfg.OutputBaseDir)
                ? cfg.OutputBaseDir
                : Path.Combine(Directory.GetCurrentDirectory(), cfg.OutputBaseDir)
        );
        Directory.CreateDirectory(cfg.OutputBaseDir);

        // OutputDir is just the base — municipality folders sit directly inside it
        cfg.OutputDir = cfg.OutputBaseDir;

        // Run ID for logs only
        var runId = DateTimeOffset.Now.ToString("yyyy-MM-dd_HHmmss");
        var runDir = Path.Combine(cfg.OutputDir, "_runs", runId);
        Directory.CreateDirectory(runDir);

        using var log = new Logger(Path.Combine(runDir, "run.log"));
        var governor = new StorageGovernor(cfg.MaxTotalBytes);

        void LogLine(string s)
        {
            try { uiLog(s); } catch { }
            try { log.Info(s); } catch { }
        }

        log.Event("RUN_GUI",
            ("runId", runId),
            ("outputBaseDir", cfg.OutputBaseDir),
            ("outputDir", cfg.OutputDir),
            ("sites", urls.Count),
            ("maxDepth", cfg.MaxDepth));

        LogLine($"[OUT]  {cfg.OutputDir}");
        LogLine($"[SITE] Count={urls.Count} MaxDepth={cfg.MaxDepth}");
        LogLine("");

        var results = new System.Collections.Concurrent.ConcurrentBag<(string Host, string DisplayName, string ViewerRel, string Status, int PagesDone)>();

        await using var runner = new PlaywrightRunner(cfg, log);

        foreach (var startUrlRaw in urls)
        {
            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);

            var startUrl = Utils.EnsureScheme(startUrlRaw);

            await ProcessSiteAsync(startUrl, cfg, runner, governor, runDir, log, results, ct, pause, uiLog);
        }

        ct.ThrowIfCancellationRequested();
        pause.WaitIfPaused(ct);

        // Top index (written into the run's _runs/{runId}/ folder)
        using (log.Scope("BUILD_TOP_INDEX"))
        {
            var indexBuilder = new TopIndexBuilder(cfg);
            await indexBuilder.BuildAsync(runDir, runId, results.ToList());
        }

        // Build per-municipality indexes (index.html + index.json)
        using (log.Scope("BUILD_MUNICIPALITY_INDEXES"))
        {
            var muniBuilder = new MunicipalityIndexBuilder(log);
            var muniFolders = results
                .Select(r => r.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var muni in muniFolders)
            {
                await muniBuilder.BuildAsync(cfg.OutputDir, muni);
            }
        }

        // Run manifest
        var manifest = new RunManifest
        {
            RunId = runId,
            RunFolderName = runId,
            OutputDir = cfg.OutputDir,
            GeneratedLocal = DateTimeOffset.Now,
            Sites = results.Count,
            Results = results.Select(r => new RunSiteItem
            {
                Host = r.Host,
                DisplayName = r.DisplayName,
                ViewerRel = r.ViewerRel,
                Status = r.Status,
                PagesDone = r.PagesDone
            }).ToList()
        };
        await Utils.WriteJsonAsync(Path.Combine(runDir, "run.json"), manifest);

        // Base/global index (unchanged behavior)
        await GlobalIndexBuilder.BuildAsync(cfg.OutputBaseDir);

        LogLine("[DONE] Run completed.");
    }

    private sealed class ScrapeRunMeta
    {
        public string Municipality { get; set; } = "";
        public string Host { get; set; } = "";
        public string StartUrl { get; set; } = "";
        public string Status { get; set; } = "";
        public int PagesDone { get; set; }
        public DateTimeOffset GeneratedLocal { get; set; }
        public string EntryRel { get; set; } = "";
        public string ViewerRel { get; set; } = "";
    }

    private static async Task ProcessSiteAsync(
        string startUrl,
        SnapshotConfig cfg,
        PlaywrightRunner runner,
        StorageGovernor governor,
        string runDir,
        Logger log,
        System.Collections.Concurrent.ConcurrentBag<(string Host, string DisplayName, string ViewerRel, string Status, int PagesDone)> results,
        CancellationToken ct,
        PauseController pause,
        Action<string> uiLog)
    {
        ct.ThrowIfCancellationRequested();
        pause.WaitIfPaused(ct);

        var host = new Uri(startUrl).Host;
        var municipality = Utils.HostToMunicipality(host);

        // Scrape folder: {OutputDir}/{Municipality}/{YYMMDDHHmm}
        var scrapeFolderName = DateTimeOffset.Now.ToString("yyMMdd", CultureInfo.InvariantCulture);
        var scrapeRootDir = Path.Combine(cfg.OutputDir, municipality, scrapeFolderName);

        // Deduplicate if folder already exists (rare but possible)
        if (Directory.Exists(scrapeRootDir))
        {
            var n = 2;
            while (Directory.Exists(scrapeRootDir + $"_{n}")) n++;
            scrapeFolderName = scrapeFolderName + $"_{n}";
            scrapeRootDir = Path.Combine(cfg.OutputDir, municipality, scrapeFolderName);
        }

        Directory.CreateDirectory(scrapeRootDir);

        // Content goes directly in the scrape folder — no sites/{host}/ nesting
        var siteDir = scrapeRootDir;

        using var siteLog = new Logger(Path.Combine(runDir, $"{host}.log"));
        siteLog.Event("SITE_START",
            ("startUrl", startUrl),
            ("host", host),
            ("municipality", municipality),
            ("scrapeRootDir", scrapeRootDir),
            ("siteDir", siteDir));

        uiLog($"[SITE] START {startUrl}");
        var status = "OK";
        var pagesDone = 0;

        try
        {
            NavIndex nav;

            // 1) Crawl
            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);
            using (siteLog.Scope("CRAWL", ("startUrl", startUrl)))
            {
                var navCrawler = new NavCrawler(cfg, runner, siteLog);

                nav = await navCrawler.CrawlAsync(startUrl, governor, ct, pause);

                var navPath = Path.Combine(siteDir, "nav.json");
                await Utils.WriteJsonAsync(navPath, nav);

                siteLog.Event("CRAWL_DONE", ("flatCount", nav.Flat.Count), ("nodesCount", nav.Nodes.Count));
            }

            // 2) Snapshot
            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);
            using (siteLog.Scope("SNAPSHOT_ALL", ("host", host)))
            {
                var snap = new Snapshotter(cfg, runner, siteLog);

                pagesDone = await snap.CaptureAllAsync(siteDir, nav.Flat, governor, ct, pause);
                siteLog.Event("SNAPSHOT_DONE", ("pagesCaptured", pagesDone));
            }

            // 3) Viewer (inside siteDir)
            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);
            using (siteLog.Scope("BUILD_VIEWER", ("host", host)))
            {
                var viewer = new SiteViewerBuilder(cfg, siteLog);
                await viewer.BuildAsync(siteDir, host, startUrl, "viewer.htm");
            }

            // 4) Entry index.html for this scrape root
            var viewerRel = "viewer.htm";

            var entryHtml = BuildScrapeEntryHtml(
                municipality,
                host,
                startUrl,
                status,
                pagesDone,
                viewerRel);

            await File.WriteAllTextAsync(Path.Combine(scrapeRootDir, "index.html"), entryHtml, Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(scrapeRootDir, "index.htm"), entryHtml, Encoding.UTF8);

            // 5) scrape.json
            var meta = new ScrapeRunMeta
            {
                Municipality = municipality,
                Host = host,
                StartUrl = startUrl,
                Status = status,
                PagesDone = pagesDone,
                GeneratedLocal = DateTimeOffset.Now,
                EntryRel = $"{municipality}/{scrapeFolderName}/index.html",
                ViewerRel = $"{municipality}/{scrapeFolderName}/viewer.htm"
            };

            var metaJson = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(scrapeRootDir, "scrape.json"), metaJson, Encoding.UTF8);

            // Add result item pointing to municipality/date/index.html
            results.Add((host, municipality, $"{municipality}/{scrapeFolderName}/index.html", "OK", pagesDone));
        }
        catch (OperationCanceledException)
        {
            status = "CANCELLED";
            results.Add((host, municipality, $"{municipality}/{scrapeFolderName}/index.html", status, pagesDone));
            uiLog($"[SITE] STOP {host} (cancelled)");
            throw;
        }
        catch (StorageCapReachedException ex)
        {
            status = "CAP_REACHED";
            siteLog.Error("Storage cap reached: " + ex.Message);
            uiLog($"[SITE] STOP {host} (storage cap reached)");
            pagesDone = CountActualShots(siteDir, pagesDone);
            await TryBuildViewerAsync(siteDir, host, startUrl, pagesDone, cfg, siteLog);
            results.Add((host, municipality, $"{municipality}/{scrapeFolderName}/index.html", status, pagesDone));
        }
        catch (Exception ex)
        {
            status = "ERROR";
            siteLog.Error("Unhandled exception: " + ex);
            uiLog($"[SITE] ERROR {host}: {ex.Message}");
            pagesDone = CountActualShots(siteDir, pagesDone);
            await TryBuildViewerAsync(siteDir, host, startUrl, pagesDone, cfg, siteLog);
            results.Add((host, municipality, $"{municipality}/{scrapeFolderName}/index.html", status, pagesDone));
        }
        finally
        {
            siteLog.Event("SITE_END", ("host", host), ("status", status), ("pagesDone", pagesDone));
            uiLog($"[SITE] DONE  {host} status={status} pagesCaptured={pagesDone}");
            uiLog("");
        }
    }

    private static int CountActualShots(string siteDir, int currentPagesDone)
    {
        try
        {
            var shotsDir = Path.Combine(siteDir, "shots");
            if (!Directory.Exists(shotsDir)) return currentPagesDone;
            var count = Directory.GetFiles(shotsDir, "*.webp", SearchOption.TopDirectoryOnly).Length;
            return count > currentPagesDone ? count : currentPagesDone;
        }
        catch { return currentPagesDone; }
    }

    private static async Task TryBuildViewerAsync(string siteDir, string host, string startUrl, int pagesDone, SnapshotConfig cfg, Logger siteLog)
    {
        if (pagesDone <= 0) return;
        try
        {
            var viewer = new SiteViewerBuilder(cfg, siteLog);
            await viewer.BuildAsync(siteDir, host, startUrl, "viewer.htm");
        }
        catch { }
    }

    private static string BuildScrapeEntryHtml(string municipality, string host, string startUrl, string status, int pagesDone, string viewerRel)
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        // viewerRel is relative to scrape root
        var viewerLink = viewerRel.Contains('#') ? viewerRel : viewerRel + "#start";

        return $@"<!doctype html>
<html lang=""sv"">
<meta charset=""utf-8"">
<title>{E(municipality)} - {E(host)}</title>
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<style>
:root {{ --fg:#222; --muted:#666; --accent:#7a003c; --chip:#eef; --border:#ddd; }}
body{{font-family:system-ui,Segoe UI,Arial,sans-serif;margin:2rem;color:var(--fg);max-width:1100px}}
h1{{margin:0 0 .25rem 0;font-size:1.8rem}}
.sub{{color:var(--muted);font-size:.95rem;margin:.15rem 0}}
.badge{{background:var(--chip);color:#334;padding:.1rem .4rem;border-radius:.4rem;font-size:.75rem;margin-left:.5rem}}
a{{color:var(--accent);text-decoration:none}}
a:hover{{text-decoration:underline}}
.card{{border:1px solid var(--border);border-radius:12px;padding:1rem;margin-top:1rem}}
.code{{font-family:ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', 'Courier New', monospace; font-size:.9rem}}
</style>

<div class=""sub""><a href=""../index.html"">← {E(municipality)} index</a> | <a href=""../../index.html"">Run index</a></div>
<h1>{E(municipality)} <small class=""sub"">({E(host)})</small></h1>
<div class=""sub""><span class=""badge"">{E(status)}</span> <span class=""sub"">pages:{pagesDone}</span></div>
<div class=""sub"">Start URL: <span class=""code"">{E(startUrl)}</span></div>

<div class=""card"">
  <h2 style=""margin-top:0"">Open scrape</h2>
  <p><a href=""{E(viewerLink)}"">Open viewer</a></p>
  <p class=""sub"">This folder is self-contained. To replace a bad scrape, replace this date folder.</p>
  <p class=""sub"">Metadata: <span class=""code"">scrape.json</span></p>
</div>
</html>";
    }
}