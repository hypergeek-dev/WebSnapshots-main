// SnapshotConfig.cs (FULL FILE)
using System;

namespace WebSnapshots;

public sealed class SnapshotConfig
{
    // Input/output
    public string InputFile { get; set; } = "sites.txt";

    // Base output folder chosen by user (GUI/CLI). We will always create a dated subfolder inside.
    public string OutputBaseDir { get; set; } = "output";

    // Effective output dir for this run (computed). If set, it overrides computed.
    public string OutputDir { get; set; } = "";

    // Dated runs
    public bool UseDatedOutput { get; set; } = true;

    // Crawl & capture limits
    public int MaxDepth { get; set; } = 3; // ✅ default now 3
    public int MaxPagesPerSite { get; set; } = 1500;
    public long MaxTotalBytes { get; set; } = 150L * 1024 * 1024 * 1024; // 150 GB

    // Browser/viewport
    public int ViewportWidth { get; set; } = 1920;
    public int ViewportHeight { get; set; } = 1080;

    // Timing
    public int ShotGotoTimeoutMs { get; set; } = 60_000;
    public int NavGotoTimeoutMs { get; set; } = 45_000;
    public int NetworkIdleMaxMs { get; set; } = 8_000;
    public int StabilizeMaxMs { get; set; } = 12_000;
    public int ScrollSettleMs { get; set; } = 350;
    public int DelayBetweenPagesMs { get; set; } = 0;

    // Tiling
    public int ScrollStepPx { get; set; } = 0;          // 0 => auto (viewportHeight - overlap)
    public int TileOverlapPx { get; set; } = 120;       // crop this overlap in viewer for tile>0
    public int MaxTilesPerPage { get; set; } = 5000;

    // Output encoding
    public int WebpQuality { get; set; } = 75;

    // Modes
    public bool LandingOnly { get; set; } = false;

    // Quick preview mode (fast “shape check” for the tree UI)
    // - Crawls up to MaxDepth, but caps breadth and time.
    // - Still builds a real hierarchy so you can judge the visuals.
    public bool QuickPreview { get; set; } = false;
    public int PreviewMaxPagesPerDepth { get; set; } = 25;
    public int PreviewMaxChildrenPerPage { get; set; } = 18;
    public int PreviewMaxTotalSeconds { get; set; } = 90;

    public bool Debug { get; set; } = false;

    // Misc
    public bool DropQueryStrings { get; set; } = true;
    public int ProgressEverySeconds { get; set; } = 20;

    public string? UserAgent { get; set; } = null;

    public void Validate()
    {
        if (MaxDepth < 0)
            throw new ArgumentException("MaxDepth cannot be negative");

        if (MaxPagesPerSite <= 0)
            throw new ArgumentException("MaxPagesPerSite must be positive");

        if (MaxTotalBytes <= 0)
            throw new ArgumentException("MaxTotalBytes must be positive");

        if (ViewportWidth < 100 || ViewportHeight < 100)
            throw new ArgumentException("Viewport dimensions must be at least 100x100");

        if (WebpQuality < 1 || WebpQuality > 100)
            throw new ArgumentException("WebP quality must be between 1 and 100");

        if (ShotGotoTimeoutMs <= 0 || NavGotoTimeoutMs <= 0)
            throw new ArgumentException("Timeout values must be positive");

        if (TileOverlapPx < 0)
            throw new ArgumentException("TileOverlapPx cannot be negative");

        if (MaxTilesPerPage <= 0)
            throw new ArgumentException("MaxTilesPerPage must be positive");

        if (ProgressEverySeconds <= 0)
            throw new ArgumentException("ProgressEverySeconds must be positive");

        if (QuickPreview)
        {
            if (PreviewMaxPagesPerDepth <= 0)
                throw new ArgumentException("PreviewMaxPagesPerDepth must be positive when QuickPreview is enabled");
            if (PreviewMaxChildrenPerPage <= 0)
                throw new ArgumentException("PreviewMaxChildrenPerPage must be positive when QuickPreview is enabled");
            if (PreviewMaxTotalSeconds <= 0)
                throw new ArgumentException("PreviewMaxTotalSeconds must be positive when QuickPreview is enabled");
        }
    }

    public static SnapshotConfig FromArgs(string[] args)
    {
        var cfg = new SnapshotConfig();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i]?.Trim() ?? "";
            if (a.Length == 0) continue;

            static string? Next(string[] arr, ref int idx)
            {
                if (idx + 1 >= arr.Length) return null;
                idx++;
                return arr[idx];
            }

            static int ParseInt(string? s, int fallback)
                => int.TryParse(s, out var v) ? v : fallback;

            static long ParseLong(string? s, long fallback)
                => long.TryParse(s, out var v) ? v : fallback;

            switch (a.ToLowerInvariant())
            {
                case "--input":
                case "--sites":
                case "--sites-file":
                    cfg.InputFile = Next(args, ref i) ?? cfg.InputFile;
                    break;

                case "--output":
                case "--out":
                    // GUI/CLI passes base output folder here
                    cfg.OutputBaseDir = Next(args, ref i) ?? cfg.OutputBaseDir;
                    break;

                case "--no-dated-output":
                    cfg.UseDatedOutput = false;
                    break;

                case "--max-depth":
                    cfg.MaxDepth = ParseInt(Next(args, ref i), cfg.MaxDepth);
                    break;

                case "--max-pages":
                case "--max-pages-per-site":
                    cfg.MaxPagesPerSite = ParseInt(Next(args, ref i), cfg.MaxPagesPerSite);
                    break;

                case "--cap-bytes":
                    cfg.MaxTotalBytes = ParseLong(Next(args, ref i), cfg.MaxTotalBytes);
                    break;

                case "--cap-gb":
                    {
                        var gb = ParseInt(Next(args, ref i), (int)(cfg.MaxTotalBytes / (1024L * 1024 * 1024)));
                        cfg.MaxTotalBytes = gb * 1024L * 1024 * 1024;
                    }
                    break;

                case "--viewport":
                    {
                        var v = Next(args, ref i);
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            var parts = v.Split('x', 'X');
                            if (parts.Length == 2)
                            {
                                cfg.ViewportWidth = ParseInt(parts[0], cfg.ViewportWidth);
                                cfg.ViewportHeight = ParseInt(parts[1], cfg.ViewportHeight);
                            }
                        }
                    }
                    break;

                case "--webp-quality":
                    cfg.WebpQuality = ParseInt(Next(args, ref i), cfg.WebpQuality);
                    break;

                case "--landing-only":
                case "--landingpage-only":
                    cfg.LandingOnly = true;
                    break;

                case "--quick-preview":
                case "--preview":
                case "--test-preview":
                    cfg.QuickPreview = true;
                    break;

                case "--preview-pages-per-depth":
                    cfg.PreviewMaxPagesPerDepth = ParseInt(Next(args, ref i), cfg.PreviewMaxPagesPerDepth);
                    break;

                case "--preview-children-per-page":
                    cfg.PreviewMaxChildrenPerPage = ParseInt(Next(args, ref i), cfg.PreviewMaxChildrenPerPage);
                    break;

                case "--preview-seconds":
                    cfg.PreviewMaxTotalSeconds = ParseInt(Next(args, ref i), cfg.PreviewMaxTotalSeconds);
                    break;

                case "--debug":
                    cfg.Debug = true;
                    break;

                case "--drop-query":
                case "--drop-query-strings":
                    cfg.DropQueryStrings = true;
                    break;

                case "--keep-query":
                case "--keep-query-strings":
                    cfg.DropQueryStrings = false;
                    break;

                case "--nav-timeout-ms":
                    cfg.NavGotoTimeoutMs = ParseInt(Next(args, ref i), cfg.NavGotoTimeoutMs);
                    break;

                case "--shot-timeout-ms":
                    cfg.ShotGotoTimeoutMs = ParseInt(Next(args, ref i), cfg.ShotGotoTimeoutMs);
                    break;

                case "--network-idle-ms":
                    cfg.NetworkIdleMaxMs = ParseInt(Next(args, ref i), cfg.NetworkIdleMaxMs);
                    break;

                case "--stabilize-ms":
                    cfg.StabilizeMaxMs = ParseInt(Next(args, ref i), cfg.StabilizeMaxMs);
                    break;

                case "--scroll-step":
                    cfg.ScrollStepPx = ParseInt(Next(args, ref i), cfg.ScrollStepPx);
                    break;

                case "--scroll-settle-ms":
                    cfg.ScrollSettleMs = ParseInt(Next(args, ref i), cfg.ScrollSettleMs);
                    break;

                case "--tile-overlap":
                    cfg.TileOverlapPx = ParseInt(Next(args, ref i), cfg.TileOverlapPx);
                    break;

                case "--max-tiles":
                    cfg.MaxTilesPerPage = ParseInt(Next(args, ref i), cfg.MaxTilesPerPage);
                    break;

                case "--delay-ms":
                    cfg.DelayBetweenPagesMs = ParseInt(Next(args, ref i), cfg.DelayBetweenPagesMs);
                    break;

                case "--progress-every":
                case "--progress-every-seconds":
                    cfg.ProgressEverySeconds = ParseInt(Next(args, ref i), cfg.ProgressEverySeconds);
                    break;

                case "--user-agent":
                    cfg.UserAgent = Next(args, ref i);
                    break;
            }
        }

        cfg.Validate();
        return cfg;
    }
}