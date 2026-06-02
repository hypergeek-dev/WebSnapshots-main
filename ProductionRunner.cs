using System.Globalization;

namespace WebSnapshots;

public static class ProductionRunner
{
    public static async Task RunAsync(
        SnapshotConfig cfg,
        List<string> urls,
        Action<string> uiLog,
        CancellationToken ct,
        PauseController pause)
    {
        ct.ThrowIfCancellationRequested();
        cfg.OutputBaseDir = Path.GetFullPath(Path.IsPathRooted(cfg.OutputBaseDir)
            ? cfg.OutputBaseDir
            : Path.Combine(Directory.GetCurrentDirectory(), cfg.OutputBaseDir));
        Directory.CreateDirectory(cfg.OutputBaseDir);
        cfg.OutputDir = cfg.OutputBaseDir;

        var startedAt = DateTimeOffset.Now;
        var runId = startedAt.ToString("yyyy-MM-dd_HHmmss");
        var runDir = Path.Combine(cfg.OutputDir, "_runs", runId);
        Directory.CreateDirectory(runDir);

        using var log = new Logger(Path.Combine(runDir, "run.log"));
        var manifestStore = new RunManifestStore(runDir, new RunManifest
        {
            RunId = runId,
            RunFolderName = runId,
            OutputDir = cfg.OutputDir,
            OutputRoot = cfg.OutputBaseDir,
            Status = "PARTIAL",
            StartedAt = startedAt,
            GeneratedLocal = startedAt,
            SitesPlanned = urls.Count,
            Settings = BuildRunSettings(cfg)
        });
        await manifestStore.InitializeAsync();

        var governor = new StorageGovernor(cfg.MaxTotalBytes);
        await using var runner = new PlaywrightRunner(cfg, log);

        void LogLine(string s)
        {
            try { uiLog(s); } catch { }
            try { log.Info(s); } catch { }
        }

        LogLine($"[OUT]  {cfg.OutputDir}");
        LogLine($"[SITE] Count={urls.Count} MaxDepth={cfg.MaxDepth}");
        if (cfg.LandingOnly) LogLine("[MODE] Landing-only enabled");
        LogLine("");

        try
        {
            foreach (var startUrlRaw in urls)
            {
                ct.ThrowIfCancellationRequested();
                pause.WaitIfPaused(ct);
                await ProcessSiteAsync(Utils.EnsureScheme(startUrlRaw), cfg, runner, governor, runDir, manifestStore, ct, pause, uiLog);
            }

            await BuildIndexesAsync(cfg, runDir, runId, manifestStore.SnapshotResults(), log);
            var status = manifestStore.Manifest.Results.All(x => x.Status == "OK") ? "OK" : "PARTIAL";
            await manifestStore.CompleteAsync(status);
            await GlobalIndexBuilder.BuildAsync(cfg.OutputBaseDir);
            LogLine("[DONE] Run completed.");
        }
        catch (OperationCanceledException)
        {
            await manifestStore.CompleteAsync("CANCELLED");
            await TryBuildIndexesAsync(cfg, runDir, runId, manifestStore.SnapshotResults(), log);
            await TryBuildGlobalIndexAsync(cfg.OutputBaseDir, log);
            throw;
        }
        catch
        {
            await manifestStore.CompleteAsync("PARTIAL");
            await TryBuildIndexesAsync(cfg, runDir, runId, manifestStore.SnapshotResults(), log);
            await TryBuildGlobalIndexAsync(cfg.OutputBaseDir, log);
            throw;
        }
    }

    private static async Task ProcessSiteAsync(
        string startUrl,
        SnapshotConfig cfg,
        PlaywrightRunner runner,
        StorageGovernor governor,
        string runDir,
        RunManifestStore manifestStore,
        CancellationToken ct,
        PauseController pause,
        Action<string> uiLog)
    {
        ct.ThrowIfCancellationRequested();
        pause.WaitIfPaused(ct);

        var host = Uri.TryCreate(startUrl, UriKind.Absolute, out var startUri)
            && !string.IsNullOrWhiteSpace(startUri.Host)
                ? startUri.Host
                : "invalid-url";
        var municipality = Utils.HostToMunicipality(host);
        var scrapeFolderName = DateTimeOffset.Now.ToString("yyMMdd", CultureInfo.InvariantCulture);
        var scrapeRootDir = Path.Combine(cfg.OutputDir, municipality, scrapeFolderName);
        if (Directory.Exists(scrapeRootDir))
        {
            var n = 2;
            while (Directory.Exists(scrapeRootDir + $"_{n}")) n++;
            scrapeFolderName += $"_{n}";
            scrapeRootDir = Path.Combine(cfg.OutputDir, municipality, scrapeFolderName);
        }
        Directory.CreateDirectory(scrapeRootDir);

        using var siteLog = new Logger(Path.Combine(runDir, $"{host}.log"));
        var meta = new SiteScrapeManifest
        {
            Municipality = municipality,
            Host = host,
            StartUrl = startUrl,
            Status = "PARTIAL",
            Note = "Scrape is in progress. This archive is incomplete until status becomes OK.",
            StartedAt = DateTimeOffset.Now,
            EntryRel = $"{municipality}/{scrapeFolderName}/index.html"
        };
        await SiteRunArtifacts.WriteAsync(scrapeRootDir, meta);
        await manifestStore.UpsertSiteAsync(SiteRunArtifacts.ToRunSiteItem(meta));
        uiLog($"[SITE] START {startUrl}");

        try
        {
            NavIndex nav;
            using (siteLog.Scope("CRAWL", ("startUrl", startUrl)))
            {
                nav = await new NavCrawler(cfg, runner, siteLog).CrawlAsync(startUrl, governor, ct, pause);
                await AtomicWrite.WriteJsonAtomicAsync(Path.Combine(scrapeRootDir, "nav.json"), nav);
            }
            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);
            using (siteLog.Scope("SNAPSHOT_ALL", ("host", host)))
            {
                meta.PagesDone = await new Snapshotter(cfg, runner, siteLog).CaptureAllAsync(scrapeRootDir, nav.Flat, governor, ct, pause);
            }
            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);
            using (siteLog.Scope("BUILD_VIEWER", ("host", host)))
            {
                await new SiteViewerBuilder(cfg, siteLog).BuildAsync(scrapeRootDir, host, startUrl, "viewer.htm");
            }
            meta.Status = "OK";
            meta.Note = "";
        }
        catch (OperationCanceledException)
        {
            meta.Status = "CANCELLED";
            meta.Reason = "The scrape was cancelled by the operator.";
            meta.Note = "Archive is incomplete because the run was cancelled.";
            uiLog($"[SITE] STOP {host} (cancelled)");
            throw;
        }
        catch (StorageCapReachedException ex)
        {
            meta.Status = "CAP_REACHED";
            meta.Reason = ex.Message;
            meta.Note = "Archive is incomplete because the configured storage cap was reached.";
            siteLog.Error("Storage cap reached: " + ex.Message);
            await TryBuildViewerAsync(scrapeRootDir, host, startUrl, cfg, siteLog);
        }
        catch (Exception ex)
        {
            meta.Status = "ERROR";
            meta.Reason = ex.Message;
            meta.Note = "Archive is incomplete because the site scrape failed.";
            siteLog.Error("Unhandled exception: " + ex);
            await TryBuildViewerAsync(scrapeRootDir, host, startUrl, cfg, siteLog);
        }
        finally
        {
            meta.PagesDone = CountActualPages(scrapeRootDir, meta.PagesDone);
            meta.ScreenshotsDone = CountScreenshots(scrapeRootDir);
            meta.FinishedAt = DateTimeOffset.Now;
            meta.ViewerRel = File.Exists(Path.Combine(scrapeRootDir, "viewer.htm"))
                ? $"{municipality}/{scrapeFolderName}/viewer.htm"
                : "";
            await SiteRunArtifacts.WriteAsync(scrapeRootDir, meta);
            await manifestStore.UpsertSiteAsync(SiteRunArtifacts.ToRunSiteItem(meta));
            uiLog($"[SITE] DONE  {host} status={meta.Status} pagesCaptured={meta.PagesDone}");
            uiLog("");
        }
    }

    private static async Task BuildIndexesAsync(SnapshotConfig cfg, string runDir, string runId, List<RunSiteItem> results, Logger log)
    {
        var tuples = results.Select(x => (x.Host, x.DisplayName, LinkFor(x), x.Status, x.PagesDone)).ToList();
        await new TopIndexBuilder(cfg).BuildAsync(runDir, runId, tuples);
        var municipalityIndex = new MunicipalityIndexBuilder(log);
        foreach (var muni in results.Select(x => x.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase))
            await municipalityIndex.BuildAsync(cfg.OutputDir, muni);
    }

    private static async Task TryBuildIndexesAsync(SnapshotConfig cfg, string runDir, string runId, List<RunSiteItem> results, Logger log)
    {
        try { await BuildIndexesAsync(cfg, runDir, runId, results, log); }
        catch (Exception ex) { log.Warn("Partial-run index rebuild failed: " + ex.Message); }
    }

    private static async Task TryBuildGlobalIndexAsync(string outputDir, Logger log)
    {
        try { await GlobalIndexBuilder.BuildAsync(outputDir); }
        catch (Exception ex) { log.Warn("Global index rebuild failed: " + ex.Message); }
    }

    private static async Task TryBuildViewerAsync(string siteDir, string host, string startUrl, SnapshotConfig cfg, Logger log)
    {
        if (!File.Exists(Path.Combine(siteDir, "nav.json"))) return;
        try { await new SiteViewerBuilder(cfg, log).BuildAsync(siteDir, host, startUrl, "viewer.htm"); }
        catch (Exception ex) { log.Warn("Partial viewer build failed: " + ex.Message); }
    }

    private static int CountActualPages(string siteDir, int current)
    {
        try
        {
            var dir = Path.Combine(siteDir, "pages");
            if (!Directory.Exists(dir)) return current;
            return Math.Max(current, Directory.GetFiles(dir, "*.htm").Count(x => !x.EndsWith(".text.htm", StringComparison.OrdinalIgnoreCase)));
        }
        catch { return current; }
    }

    private static int CountScreenshots(string siteDir)
    {
        try
        {
            var dir = Path.Combine(siteDir, "shots");
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.webp").Length : 0;
        }
        catch { return 0; }
    }

    private static string LinkFor(RunSiteItem item)
    {
        if (item.Status == "OK" && !string.IsNullOrWhiteSpace(item.ViewerRel))
            return item.ViewerRel;
        return !string.IsNullOrWhiteSpace(item.EntryRel) ? item.EntryRel : item.ViewerRel;
    }

    private static Dictionary<string, object?> BuildRunSettings(SnapshotConfig cfg) => new()
    {
        ["maxDepth"] = cfg.MaxDepth,
        ["maxPagesPerSite"] = cfg.MaxPagesPerSite,
        ["maxTotalBytes"] = cfg.MaxTotalBytes,
        ["landingOnly"] = cfg.LandingOnly,
        ["quickPreview"] = cfg.QuickPreview,
        ["viewport"] = $"{cfg.ViewportWidth}x{cfg.ViewportHeight}",
        ["webpQuality"] = cfg.WebpQuality,
        ["dropQueryStrings"] = cfg.DropQueryStrings,
        ["delayBetweenPagesMs"] = cfg.DelayBetweenPagesMs
    };
}
