// Program.cs
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WebSnapshots;

public static class Program
{
    private const int MAX_PARALLEL_SITES = 3;

    [STAThread]
    public static async Task Main(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("WEB SNAPSHOTS (Playwright-only)");
        Console.WriteLine($"Arguments: {string.Join(" ", args)}");
        Console.WriteLine("========================================");
        Console.WriteLine();

        // GUI mode
        if (args.Length == 0 || args[0].Equals("gui", StringComparison.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return;
        }

        // Serve mode
        if (args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
        {
            await RunServeAsync(args);
            return;
        }

        // CLI scrape mode
        await RunCliAsync(args);
    }

    private static async Task RunServeAsync(string[] args)
    {
        var port = 8080;
        if (args.Length > 1 && int.TryParse(args[1], out var p)) port = p;

        var dir = "output";
        if (args.Length > 2) dir = args[2];

        await SimpleWebServer.StartAsync(dir, port);
    }

    private static async Task RunCliAsync(string[] args)
    {
        var cfg = SnapshotConfig.FromArgs(args);

        var workDir = Directory.GetCurrentDirectory();
        var binDir = AppContext.BaseDirectory;

        var sitesPathWork = Path.Combine(workDir, cfg.InputFile);
        var sitesPathBin = Path.Combine(binDir, cfg.InputFile);
        var sitesPath = ResolveSitesPath(sitesPathWork, sitesPathBin);

        EnsureSitesFileExistsOrCreateExample(sitesPathWork, sitesPathBin);

        if (!File.Exists(sitesPathWork) && !File.Exists(sitesPathBin))
        {
            Console.WriteLine($"Created example input file: {sitesPathWork}");
            Console.WriteLine("Add URLs and run again.");
            return;
        }

        var tempLogPath = Path.Combine(
            Path.GetFullPath(
                Path.IsPathRooted(cfg.OutputBaseDir)
                    ? cfg.OutputBaseDir
                    : Path.Combine(Directory.GetCurrentDirectory(), cfg.OutputBaseDir)
            ),
            "_cli_temp.log");

        Directory.CreateDirectory(Path.GetDirectoryName(tempLogPath)!);

        using var tempLog = new Logger(tempLogPath);
        var urls = LoadUrlsFromFile(sitesPath, tempLog);

        if (urls.Count == 0)
        {
            Console.WriteLine("No valid URLs found in input file.");
            return;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("[CANCEL] Ctrl+C received. Cancelling...");
        };

        var pause = new PauseController();

        await RunAsync(
            cfg,
            urls,
            s => Console.WriteLine(s),
            cts.Token,
            pause);

        Console.WriteLine("To view locally:");
        Console.WriteLine($"  dotnet run -- serve 8080 {cfg.OutputBaseDir}");
        Console.WriteLine("  Open: http://localhost:8080/");
    }

    public static async Task RunAsync(
        SnapshotConfig cfg,
        List<string> urls,
        Action<string> uiLog,
        CancellationToken ct,
        PauseController pause)
    {
        ct.ThrowIfCancellationRequested();

        cfg.OutputBaseDir = Path.GetFullPath(
            Path.IsPathRooted(cfg.OutputBaseDir)
                ? cfg.OutputBaseDir
                : Path.Combine(Directory.GetCurrentDirectory(), cfg.OutputBaseDir)
        );
        Directory.CreateDirectory(cfg.OutputBaseDir);

        // OutputDir is just the base — municipality folders sit directly inside it
        cfg.OutputDir = cfg.OutputBaseDir;

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

        log.Event("RUN",
            ("runId", runId),
            ("outputBaseDir", cfg.OutputBaseDir),
            ("outputDir", cfg.OutputDir),
            ("sites", urls.Count),
            ("maxDepth", cfg.MaxDepth),
            ("maxPagesPerSite", cfg.MaxPagesPerSite),
            ("viewport", $"{cfg.ViewportWidth}x{cfg.ViewportHeight}"),
            ("webpQuality", cfg.WebpQuality),
            ("landingOnly", cfg.LandingOnly),
            ("debug", cfg.Debug),
            ("progressEverySeconds", cfg.ProgressEverySeconds));

        LogLine($"[OUT]  {cfg.OutputDir}");
        LogLine($"[SITE] Count={urls.Count} MaxDepth={cfg.MaxDepth}");
        if (cfg.LandingOnly) LogLine("[MODE] Landing-only enabled");
        if (cfg.Debug) LogLine("[MODE] Debug enabled");
        LogLine("");

        var results = new ConcurrentBag<(string Host, string DisplayName, string ViewerRel, string Status, int PagesDone)>();

        await using var runner = new PlaywrightRunner(cfg, log);

        foreach (var startUrlRaw in urls)
        {
            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);

            var startUrl = Utils.EnsureScheme(startUrlRaw);

            await ProcessSiteAsync(
                startUrl,
                cfg,
                runner,
                governor,
                runDir,
                log,
                results,
                ct,
                pause,
                uiLog);
        }

        ct.ThrowIfCancellationRequested();
        pause.WaitIfPaused(ct);

        using (log.Scope("BUILD_TOP_INDEX"))
        {
            var indexBuilder = new TopIndexBuilder(cfg);
            await indexBuilder.BuildAsync(cfg.OutputDir, runId, results.ToList());
        }

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

        await Utils.WriteJsonAsync(Path.Combine(cfg.OutputDir, "run.json"), manifest);

        await GlobalIndexBuilder.BuildAsync(cfg.OutputBaseDir);

        LogLine("[DONE] Run completed.");
    }

    private static async Task ProcessSiteAsync(
        string startUrl,
        SnapshotConfig cfg,
        PlaywrightRunner runner,
        StorageGovernor governor,
        string runDir,
        Logger log,
        ConcurrentBag<(string Host, string DisplayName, string ViewerRel, string Status, int PagesDone)> results,
        CancellationToken ct,
        PauseController pause,
        Action<string> uiLog)
    {
        ct.ThrowIfCancellationRequested();
        pause.WaitIfPaused(ct);

        var host = new Uri(startUrl).Host;
        var municipality = Utils.HostToMunicipality(host);

        // Scrape folder: {OutputDir}/{Municipality}/{yyMMddHHmm}
        var scrapeFolderName = DateTimeOffset.Now.ToString("yyMMddHHmm", CultureInfo.InvariantCulture);
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

            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);
            using (siteLog.Scope("SNAPSHOT_ALL", ("host", host)))
            {
                var snap = new Snapshotter(cfg, runner, siteLog);
                pagesDone = await snap.CaptureAllAsync(siteDir, nav.Flat, governor, ct, pause);
                siteLog.Event("SNAPSHOT_DONE", ("pagesCaptured", pagesDone));
            }

            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);
            using (siteLog.Scope("BUILD_VIEWER", ("host", host)))
            {
                var viewer = new SiteViewerBuilder(cfg, siteLog);
                await viewer.BuildAsync(siteDir, host, startUrl);
            }

            results.Add((host, municipality, $"{municipality}/{scrapeFolderName}/index.htm", "OK", pagesDone));
        }
        catch (OperationCanceledException)
        {
            status = "CANCELLED";
            results.Add((host, municipality, $"{municipality}/{scrapeFolderName}/index.htm", status, pagesDone));
            uiLog($"[SITE] STOP {host} (cancelled)");
            throw;
        }
        catch (StorageCapReachedException ex)
        {
            status = "CAP_REACHED";
            siteLog.Error("Storage cap reached: " + ex.Message);
            uiLog($"[SITE] STOP {host} (storage cap reached)");
            results.Add((host, municipality, $"{municipality}/{scrapeFolderName}/index.htm", status, pagesDone));
        }
        catch (Exception ex)
        {
            status = "ERROR";
            siteLog.Error("Unhandled exception: " + ex);
            uiLog($"[SITE] ERROR {host}: {ex.Message}");
            results.Add((host, municipality, $"{municipality}/{scrapeFolderName}/index.htm", status, pagesDone));
        }
        finally
        {
            siteLog.Event("SITE_END", ("host", host), ("status", status), ("pagesDone", pagesDone));
            uiLog($"[SITE] DONE  {host} status={status} pagesCaptured={pagesDone}");
            uiLog("");
        }
    }

    private static string ResolveSitesPath(string sitesPathWork, string sitesPathBin)
        => File.Exists(sitesPathWork) ? sitesPathWork : sitesPathBin;

    private static void EnsureSitesFileExistsOrCreateExample(string sitesPathWork, string sitesPathBin)
    {
        if (File.Exists(sitesPathWork) || File.Exists(sitesPathBin))
            return;

        File.WriteAllText(
            sitesPathWork,
            "# One URL per line\n" +
            "https://www.astorp.se\n",
            Encoding.UTF8);
    }

    private static List<string> LoadUrlsFromFile(string sitesPath, Logger log)
    {
        return File.ReadAllLines(sitesPath, Encoding.UTF8)
            .Select(s => (s ?? "").Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Where(s => !s.StartsWith("#"))
            .Select(u =>
            {
                try
                {
                    return Utils.EnsureScheme(u);
                }
                catch (Exception ex)
                {
                    log.Warn($"Invalid URL: {u} - {ex.Message}");
                    return null;
                }
            })
            .Where(u => u != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Optional parallel runner if you want it later
    private static async Task ProcessSitesParallelAsync(
        List<string> urls,
        SnapshotConfig cfg,
        PlaywrightRunner runner,
        StorageGovernor governor,
        string runDir,
        Logger log,
        ConcurrentBag<(string Host, string DisplayName, string ViewerRel, string Status, int PagesDone)> results,
        CancellationToken ct,
        PauseController pause,
        Action<string> uiLog)
    {
        using var gate = new SemaphoreSlim(MAX_PARALLEL_SITES, MAX_PARALLEL_SITES);

        var tasks = urls.Select(async startUrl =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await ProcessSiteAsync(startUrl, cfg, runner, governor, runDir, log, results, ct, pause, uiLog);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }
}