using System.Globalization;
using System.Text.Json;
using WebSnapshots.Analysis;
using WebSnapshots.Telemetry;

namespace WebSnapshots.Diagnostic;

/// <summary>
/// Runs the scraper against a single CMS target using a named diagnostic profile.
/// Deterministic: fixed depth/pages/children, stable folder naming, structured telemetry.
///
/// Does NOT auto-improve or loop. Produces evidence for human + AI analysis.
/// </summary>
public sealed class DiagnosticRunner
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    public static async Task RunAsync(
        string targetId,
        string outputDir,
        string? profileName = null,
        int? maxDepthOverride = null,
        int? maxPagesOverride = null,
        string? configPath = null,
        CancellationToken ct = default)
    {
        // ── Load target and profile ──────────────────────────────────────────
        var targetConfig = await CmsTargetConfig.LoadAsync(configPath);
        var profileConfig = await DiagnosticProfileConfig.LoadAsync(configPath);

        var target = targetConfig.FindById(targetId);
        if (target == null)
        {
            Console.Error.WriteLine($"[DIAGNOSTIC] Target '{targetId}' not found in cms-targets.json.");
            Console.Error.WriteLine("[DIAGNOSTIC] Available targets:");
            foreach (var t in targetConfig.Targets)
                Console.Error.WriteLine($"  {t.Id,-40} {t.Name,-30} ({t.Cms})");
            return;
        }

        var profile = profileConfig.GetOrDefault(profileName);

        // Command-line overrides take precedence over profile defaults
        var depth = maxDepthOverride ?? profile.MaxDepth;
        var pages = maxPagesOverride ?? profile.MaxPages;

        Console.WriteLine($"[DIAGNOSTIC] Target:  {target.Id} — {target.Name}");
        Console.WriteLine($"[DIAGNOSTIC] Profile: {profile.Name} — {profile.Description}");
        Console.WriteLine($"[DIAGNOSTIC] CMS (expected): {target.Cms}");
        Console.WriteLine($"[DIAGNOSTIC] URL: {target.Url}");
        Console.WriteLine($"[DIAGNOSTIC] MaxDepth={depth} MaxPages={pages} MaxChildren={profile.MaxChildrenPerPage} MaxPerDepth={profile.MaxPagesPerDepth}");
        Console.WriteLine($"[DIAGNOSTIC] CaptureSnapshots={profile.CaptureSnapshots} ExtractText={profile.ExtractText} BuildViewer={profile.BuildViewer}");
        Console.WriteLine();

        var runId = DateTimeOffset.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var siteDir = Path.GetFullPath(Path.Combine(outputDir, target.Id, runId));
        Directory.CreateDirectory(siteDir);

        var host = new Uri(target.Url).Host;
        var municipality = Utils.HostToMunicipality(host);

        // Written first so the folder is labelled before any crash or cancellation.
        await File.WriteAllTextAsync(
            Path.Combine(siteDir, "DIAGNOSTIC_ONLY.txt"),
            string.Join(Environment.NewLine,
                "DIAGNOSTIC RUN — NOT A PRODUCTION ARCHIVE",
                "==========================================",
                "",
                "This folder was created by the WebSnapshots diagnostic pipeline.",
                "It is NOT an archive-grade production scrape.",
                "",
                $"Target:    {target.Id} — {target.Name}",
                $"Profile:   {profile.Name}",
                $"Snapshots: {(profile.CaptureSnapshots ? "YES" : "NO (text-only mode)")}",
                $"Run ID:    {runId}",
                "",
                $"Production scrapes live under output/{municipality}/[date]/.",
                ""),
            System.Text.Encoding.UTF8);

        Console.WriteLine($"[DIAGNOSTIC] Output: {siteDir}");
        Console.WriteLine();

        // ── Scrape metadata ──────────────────────────────────────────────────
        var meta = new
        {
            runId,
            targetId = target.Id,
            targetName = target.Name,
            expectedCms = target.Cms,
            startUrl = target.Url,
            host,
            municipality,
            diagnosticProfile = profile.Name,
            maxDepth = depth,
            maxPages = pages,
            maxChildrenPerPage = profile.MaxChildrenPerPage,
            maxPagesPerDepth = profile.MaxPagesPerDepth,
            captureSnapshots = profile.CaptureSnapshots,
            extractText = profile.ExtractText,
            buildViewer = profile.BuildViewer,
            saturationWindowSize = profile.SaturationWindowSize,
            startedUtc = DateTime.UtcNow.ToString("o"),
            mode = "diagnostic",
            deterministic = true
        };

        await File.WriteAllTextAsync(
            Path.Combine(siteDir, "scrape-meta.json"),
            JsonSerializer.Serialize(meta, _jsonOpts),
            System.Text.Encoding.UTF8);

        // ── Telemetry writer ─────────────────────────────────────────────────
        TelemetryWriter? telemetry = null;
        if (profile.Telemetry)
        {
            telemetry = new TelemetryWriter(
                filePath: Path.Combine(siteDir, "telemetry.jsonl"),
                runId: runId,
                targetId: target.Id,
                municipality: municipality,
                host: host);

            telemetry.Emit(TelemetryPhase.Run, "diagnostic_start", TelemetrySeverity.Info,
                target.Url, new Dictionary<string, object?>
                {
                    ["targetId"] = target.Id,
                    ["expectedCms"] = target.Cms,
                    ["profile"] = profile.Name,
                    ["maxDepth"] = depth,
                    ["maxPages"] = pages
                });
        }

        // ── Build SnapshotConfig ─────────────────────────────────────────────
        var cfg = BuildConfig(target, siteDir, depth, pages);
        using var log = new Logger(Path.Combine(siteDir, "diagnostic.log"));

        log.Event("DIAGNOSTIC_START",
            ("targetId", target.Id),
            ("profile", profile.Name),
            ("expectedCms", target.Cms),
            ("startUrl", target.Url),
            ("maxDepth", depth),
            ("maxPages", pages),
            ("runId", runId));

        var governor = new StorageGovernor(cfg.MaxTotalBytes);
        await using var pw = new PlaywrightRunner(cfg, log);
        var pause = PauseController.Noop;

        // ── Phase 1: Crawl ───────────────────────────────────────────────────
        NavIndex? nav = null;
        var crawlFailed = false;

        try
        {
            var crawlOpt = BuildCrawlOptions(profile, depth, pages, cfg, target.Cms);
            var crawler = new NavCrawler(cfg, pw, log, telemetry);

            using (log.Scope("CRAWL", ("startUrl", target.Url)))
            {
                nav = await crawler.CrawlAsync(target.Url, governor, ct, pause, crawlOpt);
            }

            await Utils.WriteJsonAsync(Path.Combine(siteDir, "nav.json"), nav);
            log.Event("CRAWL_DONE",
                ("flatCount", nav.Flat.Count),
                ("nodesCount", nav.Nodes.Count),
                ("navGroups", nav.NavGroups.Count));

            Console.WriteLine($"[DIAGNOSTIC] Crawl done: {nav.Flat.Count} pages, {nav.NavGroups.Count} nav groups");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[DIAGNOSTIC] Crawl cancelled.");
            telemetry?.Emit(TelemetryPhase.Crawl, "crawl_cancelled", TelemetrySeverity.Warning);
            telemetry?.Dispose();
            return;
        }
        catch (Exception ex)
        {
            crawlFailed = true;
            log.Error($"Crawl failed: {ex}");
            telemetry?.Emit(TelemetryPhase.Crawl, "crawl_failed", TelemetrySeverity.Critical,
                target.Url, new Dictionary<string, object?> { ["error"] = ex.Message });
            Console.Error.WriteLine($"[DIAGNOSTIC] Crawl failed: {ex.Message}");
        }

        // ── Phase 2: Snapshot (conditional on profile) ───────────────────────
        int pagesCaptured = 0;

        if (nav != null && nav.Flat.Count > 0)
        {
            bool skipSnapshots = !profile.CaptureSnapshots;
            bool skipText = !profile.ExtractText;

            // Only run Snapshotter if we need screenshots OR text
            if (!skipText || !skipSnapshots)
            {
                try
                {
                    var snap = new Snapshotter(cfg, pw, log, telemetry, skipScreenshots: skipSnapshots);
                    using (log.Scope("SNAPSHOT_ALL"))
                    {
                        pagesCaptured = await snap.CaptureAllAsync(siteDir, nav.Flat, governor, ct, pause);
                    }

                    var mode = skipSnapshots ? "text-only" : "full";
                    log.Event("SNAPSHOT_DONE", ("pagesCaptured", pagesCaptured), ("mode", mode));
                    Console.WriteLine($"[DIAGNOSTIC] Capture done ({mode}): {pagesCaptured} pages");
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("[DIAGNOSTIC] Snapshot cancelled.");
                }
                catch (Exception ex)
                {
                    log.Error($"Snapshot phase failed: {ex.Message}");
                    Console.Error.WriteLine($"[DIAGNOSTIC] Snapshot phase failed: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("[DIAGNOSTIC] Snapshot phase skipped (profile: smoke).");
            }
        }

        // ── Phase 3: Viewer (conditional on profile) ─────────────────────────
        if (nav != null && profile.BuildViewer)
        {
            try
            {
                var viewer = new SiteViewerBuilder(cfg, log);
                await viewer.BuildAsync(siteDir, host, target.Url);
                telemetry?.Emit(TelemetryPhase.ViewerBuild, "viewer_built", TelemetrySeverity.Info,
                    null, new Dictionary<string, object?>
                    {
                        ["navNodes"] = nav.Nodes.Count,
                        ["visibleGroups"] = nav.VisibleGroups.Count
                    });
            }
            catch (Exception ex)
            {
                log.Warn($"Viewer build failed: {ex.Message}");
            }
        }

        telemetry?.Emit(TelemetryPhase.Run, "diagnostic_complete", TelemetrySeverity.Info,
            null, new Dictionary<string, object?>
            {
                ["flatPageCount"] = nav?.Flat.Count ?? 0,
                ["pagesCaptured"] = pagesCaptured,
                ["profile"] = profile.Name,
                ["crawlFailed"] = crawlFailed
            });

        // Close telemetry before quality analysis so the report builder can read
        // the complete JSONL stream on Windows.
        telemetry?.Dispose();
        telemetry = null;

        // ── Phase 4: Quality report ──────────────────────────────────────────
        if (profile.QualityReport)
        {
            Console.WriteLine("[DIAGNOSTIC] Building quality report...");
            try
            {
                var configDict = new Dictionary<string, object?>
                {
                    ["profile"] = profile.Name,
                    ["maxDepth"] = depth,
                    ["maxPages"] = pages,
                    ["maxChildrenPerPage"] = profile.MaxChildrenPerPage,
                    ["maxPagesPerDepth"] = profile.MaxPagesPerDepth,
                    ["captureSnapshots"] = profile.CaptureSnapshots,
                    ["extractText"] = profile.ExtractText
                };

                var qr = await QualityReportBuilder.BuildAsync(
                    siteDir: siteDir,
                    runId: runId,
                    targetId: target.Id,
                    municipality: municipality,
                    host: host,
                    startUrl: target.Url,
                    expectedCms: target.Cms,
                    config: configDict,
                    diagnosticProfile: profile.Name,
                    saturationWindowSize: profile.SaturationWindowSize);

                var satStatus = qr.Saturation.Enabled
                    ? (qr.Saturation.Saturated ? "SATURATED" : $"not saturated ({qr.Saturation.NewPatternsInLastWindow} new patterns in last window)")
                    : "no fingerprint data";

                Console.WriteLine($"[DIAGNOSTIC] Quality: {qr.Warnings.Count} warnings, {qr.SuspectedFailureModes.Count} failure modes, saturation={satStatus}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DIAGNOSTIC] Quality report failed: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"[DIAGNOSTIC] Done. Output: {siteDir}");
        Console.WriteLine($"[DIAGNOSTIC] Profile: {profile.Name}");
        Console.WriteLine($"[DIAGNOSTIC] Flat pages crawled: {nav?.Flat.Count ?? 0}");
        Console.WriteLine($"[DIAGNOSTIC] Pages captured: {pagesCaptured}");
        Console.WriteLine();
        PrintNextSteps(profile.Name, target.Id, siteDir);

        if (crawlFailed || nav == null || nav.Flat.Count == 0)
        {
            Console.Error.WriteLine("[DIAGNOSTIC] Run failed basic success criteria; reports were written for inspection.");
            Environment.ExitCode = 2;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static NavCrawler.Options BuildCrawlOptions(
        DiagnosticProfile profile, int depth, int pages,
        SnapshotConfig cfg, string expectedCms)
    {
        var useQuickPreview = profile.MaxChildrenPerPage > 0 || profile.MaxPagesPerDepth > 0;

        return new NavCrawler.Options
        {
            MaxDepth = depth,
            MaxPagesPerSite = pages,
            DropQueryStrings = cfg.DropQueryStrings,
            ProgressEverySeconds = cfg.ProgressEverySeconds,
            ExpectedCms = expectedCms,

            // Use existing QuickPreview infrastructure for per-depth/per-children limits
            QuickPreview = useQuickPreview,
            PreviewMaxChildrenPerPage = profile.MaxChildrenPerPage,
            PreviewMaxPagesPerDepth = profile.MaxPagesPerDepth,
            PreviewMaxTotalSeconds = 0 // no time cap in diagnostic mode
        };
    }

    private static SnapshotConfig BuildConfig(CmsTarget target, string siteDir, int depth, int pages)
    {
        var cfg = new SnapshotConfig
        {
            MaxDepth = depth,
            MaxPagesPerSite = pages,
            DropQueryStrings = true,
            Debug = false,
            LandingOnly = false,
            QuickPreview = false
        };

        cfg.OutputBaseDir = Path.GetDirectoryName(Path.GetDirectoryName(siteDir)) ?? siteDir;
        cfg.OutputDir = cfg.OutputBaseDir;

        return cfg;
    }

    private static void PrintNextSteps(string profileName, string targetId, string siteDir)
    {
        Console.WriteLine("Next steps:");

        switch (profileName)
        {
            case "smoke":
                Console.WriteLine($"  dotnet run -- diagnostic --target {targetId} --profile cms-diagnostic");
                break;

            case "cms-diagnostic":
                Console.WriteLine($"  dotnet run -- diagnostic --target {targetId} --profile nav-diagnostic");
                break;

            case "nav-diagnostic":
                Console.WriteLine($"  dotnet run -- review-pack --current {siteDir}");
                Console.WriteLine($"  # Or compare two nav-diagnostic runs:");
                Console.WriteLine($"  dotnet run -- compare --previous <baseline-folder> --current {siteDir}");
                break;

            case "snapshot-diagnostic":
                Console.WriteLine($"  dotnet run -- review-pack --current {siteDir}");
                break;

            case "full-validation":
                Console.WriteLine($"  dotnet run -- compare --previous <prev-folder> --current {siteDir}");
                Console.WriteLine($"  dotnet run -- acceptance --comparison {siteDir}\\comparison-report.json");
                break;
        }
    }
}
