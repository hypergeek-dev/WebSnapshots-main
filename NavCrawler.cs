using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using WebSnapshots.Diagnostic;
using WebSnapshots.Telemetry;

namespace WebSnapshots;

public sealed class NavCrawler
{
    private readonly SnapshotConfig _cfg;
    private readonly PlaywrightRunner _pw;
    private readonly Logger _log;
    private readonly TelemetryWriter? _telemetry;

    public sealed class Options
    {
        public int MaxDepth { get; set; } = 3;
        public int MaxPagesPerSite { get; set; } = 1500;

        // Expected CMS for telemetry mismatch detection. Null = not known.
        public string? ExpectedCms { get; set; }
        public bool DropQueryStrings { get; set; } = true;
        public int ProgressEverySeconds { get; set; } = 20;

        public int MaxStartNavLinks { get; set; } = 500;
        public int MaxStartVisibleLinksPerGroup { get; set; } = 40;

        public WaitUntilState NavWaitUntil { get; set; } = WaitUntilState.DOMContentLoaded;
        public int FastNetworkIdleMs { get; set; } = 1200;
        public bool EnableFallbackGoto { get; set; } = true;
        public int MicroSettleDelayMs { get; set; } = 100;
        public int WaitForAnyAnchorMs { get; set; } = 1200;

        public bool QuickPreview { get; set; } = false;
        public int PreviewMaxPagesPerDepth { get; set; } = 0;
        public int PreviewMaxChildrenPerPage { get; set; } = 0;
        public int PreviewMaxTotalSeconds { get; set; } = 0;

        public int MaxInnerNavLinks { get; set; } = 250;
        public int MaxFallbackSectionLinks { get; set; } = 120;
    }

    private sealed record QItem(string Url, int Depth, string ParentUrl, bool StructuralBranch, string Text = "");

    private sealed class RootCandidateClassification
    {
        public List<HomepageSection> Accepted { get; } = new();
        public HashSet<string> AcceptedUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RejectedUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int CandidateCount { get; set; }
    }

    public NavCrawler(SnapshotConfig cfg, PlaywrightRunner pw, Logger log, TelemetryWriter? telemetry = null)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _pw = pw ?? throw new ArgumentNullException(nameof(pw));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _telemetry = telemetry;
    }

    public async Task<NavIndex> CrawlAsync(
        string startUrl,
        StorageGovernor governor,
        CancellationToken ct,
        PauseController? pause = null,
        Options? opt = null)
    {
        opt ??= new Options
        {
            MaxDepth = _cfg.MaxDepth,
            MaxPagesPerSite = _cfg.MaxPagesPerSite,
            DropQueryStrings = _cfg.DropQueryStrings,
            ProgressEverySeconds = _cfg.ProgressEverySeconds,
            QuickPreview = _cfg.QuickPreview,
            PreviewMaxPagesPerDepth = _cfg.QuickPreview ? _cfg.PreviewMaxPagesPerDepth : 0,
            PreviewMaxChildrenPerPage = _cfg.QuickPreview ? _cfg.PreviewMaxChildrenPerPage : 0,
            PreviewMaxTotalSeconds = _cfg.QuickPreview ? _cfg.PreviewMaxTotalSeconds : 0
        };

        if (string.IsNullOrWhiteSpace(startUrl))
            throw new ArgumentException("startUrl is required", nameof(startUrl));

        var startAbs = Utils.NormalizeUrl(Utils.EnsureScheme(startUrl), opt.DropQueryStrings);
        var host = new Uri(startAbs).Host;

        _log.Event("NAV_INIT",
            ("startUrl", startAbs),
            ("host", host),
            ("maxDepth", opt.MaxDepth),
            ("maxPagesPerSite", opt.MaxPagesPerSite),
            ("dropQueryStrings", opt.DropQueryStrings),
            ("progressEverySeconds", opt.ProgressEverySeconds),
            ("maxStartNavLinks", opt.MaxStartNavLinks),
            ("maxStartVisibleLinksPerGroup", opt.MaxStartVisibleLinksPerGroup),
            ("maxInnerNavLinks", opt.MaxInnerNavLinks),
            ("maxFallbackSectionLinks", opt.MaxFallbackSectionLinks),
            ("navWaitUntil", opt.NavWaitUntil.ToString()),
            ("fastNetworkIdleMs", opt.FastNetworkIdleMs),
            ("waitForAnyAnchorMs", opt.WaitForAnyAnchorMs),
            ("microSettleDelayMs", opt.MicroSettleDelayMs),
            ("enableFallbackGoto", opt.EnableFallbackGoto),
            ("quickPreview", opt.QuickPreview),
            ("previewMaxPagesPerDepth", opt.PreviewMaxPagesPerDepth),
            ("previewMaxChildrenPerPage", opt.PreviewMaxChildrenPerPage),
            ("previewMaxTotalSeconds", opt.PreviewMaxTotalSeconds));

        await using var ctx = await _pw.NewContextAsync();
        var page = await ctx.NewPageAsync();

        var navGroups = new List<NavGroup>();
        var visibleGroups = new List<VisibleLinkGroup>();
        var rootClassification = new RootCandidateClassification();
        var homepageSections = rootClassification.Accepted;
        CmsDetectionResult cms = new() { Kind = CmsKind.Unknown, Confidence = "low" };
        CmsAwareNavExtractor? cmsExtractor = null;

        try
        {
            await SafeGotoAsync(page, startAbs, ct, pause, opt);

            cms = await CmsDetector.DetectAsync(page);
            cmsExtractor = new CmsAwareNavExtractor(cms, _log, startAbs);

            _log.Event("NAV_CMS_DETECTED",
                ("host", host),
                ("cms", cms.Kind.ToString()),
                ("confidence", cms.Confidence),
                ("signals", string.Join(" | ", cms.Signals ?? new List<string>())));

            var cmsMismatch = !string.IsNullOrWhiteSpace(opt.ExpectedCms) &&
                !string.Equals(cms.Kind.ToString(), opt.ExpectedCms, StringComparison.OrdinalIgnoreCase);

            _telemetry?.Emit(TelemetryPhase.CmsDetection, "cms_detected", TelemetrySeverity.Info,
                startAbs, new Dictionary<string, object?>
                {
                    ["kind"] = cms.Kind.ToString(),
                    ["confidence"] = cms.Confidence,
                    ["signals"] = string.Join(", ", cms.Signals ?? new List<string>()),
                    ["expectedCms"] = opt.ExpectedCms ?? "",
                    ["mismatch"] = cmsMismatch
                });

            navGroups = await cmsExtractor.ExtractStartPageNavGroupsAsync(
                page,
                startAbs,
                host,
                opt.DropQueryStrings,
                opt.MaxStartNavLinks);

            visibleGroups = await cmsExtractor.ExtractVisibleGroupsAsync(
                page,
                startAbs,
                host,
                opt.DropQueryStrings,
                opt.MaxStartVisibleLinksPerGroup);

            // Phase 1: extract municipality-authored homepage card/tile sections.
            // These are the canonical IA anchors used for viewer hierarchy and reparenting.
            rootClassification = await ExtractHomepageSectionsAsync(
                page, startAbs, host, opt.DropQueryStrings, _log);
            homepageSections = rootClassification.Accepted;
        }
        catch (Exception ex)
        {
            _log.Warn($"NAV_GROUPS_FAILED {ex.GetType().Name}: {ex.Message}");
        }

        if (navGroups.Count == 0)
        {
            try
            {
                var localStartLinks = await LocalNavExtractor.ExtractAsync(
                    page,
                    startAbs,
                    host,
                    opt.DropQueryStrings,
                    opt.MaxStartNavLinks);

                if (localStartLinks.Count > 0)
                {
                    navGroups.Add(new NavGroup
                    {
                        Id = "startpage_local_fallback",
                        Rank = 0,
                        LinkCount = localStartLinks.Count,
                        Flat = localStartLinks.Select(x => new NavItem
                        {
                            Url = x.Url,
                            Title = x.Text,
                            Depth = 1,
                            ParentUrl = startAbs
                        }).ToList()
                    });

                    _log.Event("NAV_START_FALLBACK_LOCAL",
                        ("host", host),
                        ("count", localStartLinks.Count),
                        ("structural", localStartLinks.Count(x => x.IsStructural)));
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"NAV_START_FALLBACK_LOCAL_WARN host={host} err={ex.Message}");
            }
        }

        if (visibleGroups.Count == 0)
        {
            try
            {
                visibleGroups = await LocalNavExtractor.ExtractVisibleGroupsAsync(
                    page,
                    startAbs,
                    host,
                    opt.DropQueryStrings,
                    opt.MaxStartVisibleLinksPerGroup);

                if (visibleGroups.Count > 0)
                {
                    _log.Event("NAV_VISIBLE_FALLBACK_LOCAL",
                        ("host", host),
                        ("groups", visibleGroups.Count),
                        ("links", visibleGroups.Sum(x => x.Flat?.Count ?? 0)));
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"NAV_VISIBLE_FALLBACK_LOCAL_WARN host={host} err={ex.Message}");
            }
        }

        var primaryNavUrls = new List<string>();
        var primaryNavSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var protectedPrimaryRootCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (navGroups.Count > 0)
        {
            foreach (var g in navGroups.OrderBy(x => x.Rank).ThenByDescending(x => x.LinkCount))
            {
                _log.Event("NAV_GROUP_CANDIDATE",
                    ("id", g.Id),
                    ("rank", g.Rank),
                    ("linkCount", g.LinkCount));
            }

            var primary = navGroups
                .OrderBy(g => g.Rank)
                .ThenByDescending(g => g.LinkCount)
                .FirstOrDefault() ?? navGroups[0];

            _log.Event("NAV_PRIMARY_GROUP_SELECTED",
                ("id", primary.Id),
                ("rank", primary.Rank),
                ("linkCount", primary.LinkCount));

            foreach (var it in primary.Flat)
            {
                var u = Utils.NormalizeUrl(it.Url, opt.DropQueryStrings);
                if (string.IsNullOrWhiteSpace(u)) continue;
                if (u.Equals(startAbs, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var uu = new Uri(u);
                    if (!SameSiteHost(uu.Host, host))
                        continue;

                    if (!string.Equals(uu.Host, host, StringComparison.OrdinalIgnoreCase))
                        u = new UriBuilder(u) { Host = host }.Uri.ToString().TrimEnd('/');
                }
                catch
                {
                    continue;
                }

                if (IsProtectedPrimaryRootCandidate(u, it.Title, startAbs))
                    protectedPrimaryRootCandidates.Add(u);

                if (primaryNavSet.Add(u))
                    primaryNavUrls.Add(u);
            }
        }

        var visibleSeedUrls = new List<string>();
        var visibleSeedSet  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var displayOnlyCount = 0;

        foreach (var g in visibleGroups)
        {
            foreach (var it in g.Flat)
            {
                // Display-only links (articles, external cards) are shown in the
                // viewer but must NOT become structural crawl-expansion roots.
                if (it.IsDisplayOnly)
                {
                    displayOnlyCount++;
                    continue;
                }

                var u = Utils.NormalizeUrl(it.Url, opt.DropQueryStrings);
                if (string.IsNullOrWhiteSpace(u)) continue;
                if (u.Equals(startAbs, StringComparison.OrdinalIgnoreCase)) continue;
                if (primaryNavSet.Contains(u)) continue;

                try
                {
                    var uu = new Uri(u);
                    if (!SameSiteHost(uu.Host, host))
                        continue;

                    if (!string.Equals(uu.Host, host, StringComparison.OrdinalIgnoreCase))
                        u = new UriBuilder(u) { Host = host }.Uri.ToString().TrimEnd('/');
                }
                catch
                {
                    continue;
                }

                if (visibleSeedSet.Add(u))
                    visibleSeedUrls.Add(u);
            }
        }

        if (displayOnlyCount > 0)
            _log.Event("VISIBLE_DISPLAY_ONLY_SKIPPED",
                ("host",  host),
                ("count", displayOnlyCount));

        _log.Event("NAV_GROUPS_EXTRACTED",
            ("host", host),
            ("groups", navGroups.Count),
            ("primaryCount", primaryNavUrls.Count),
            ("visibleGroups", visibleGroups.Count),
            ("visibleSeedUrls", visibleSeedUrls.Count),
            ("displayOnlyLinks", displayOnlyCount));

        {
            var primaryGroup = navGroups.OrderBy(g => g.Rank).ThenByDescending(g => g.LinkCount).FirstOrDefault();
            _telemetry?.Emit(TelemetryPhase.NavStartExtraction, "nav_groups_found", TelemetrySeverity.Info,
                startAbs, new Dictionary<string, object?>
                {
                    ["navGroupCount"]       = navGroups.Count,
                    ["visibleGroupCount"]   = visibleGroups.Count,
                    ["primaryGroupId"]      = primaryGroup?.Id ?? "",
                    ["primaryLinkCount"]    = primaryNavUrls.Count,
                    ["visibleSeedCount"]    = visibleSeedUrls.Count,
                    ["displayOnlyLinks"]    = displayOnlyCount,
                    ["groupIds"]            = string.Join(", ", navGroups.Select(g => g.Id ?? ""))
                });
        }

        // ── Structural promotion fallback ────────────────────────────────────
        // When the primary nav group extracted zero usable links but visible groups
        // contain municipal-section-shaped URLs, promote those into the structural
        // primary list so they end up in treeFlat (not just allFlat).
        if (primaryNavUrls.Count == 0 && visibleSeedUrls.Count > 0)
        {
            var promoted = new List<string>();
            var rejected  = new List<string>();
            foreach (var u in visibleSeedUrls)
                (IsLikelyMunicipalSection(u) ? promoted : rejected).Add(u);

            if (promoted.Count > 0)
            {
                foreach (var u in promoted)
                {
                    protectedPrimaryRootCandidates.Add(u);
                    if (primaryNavSet.Add(u))
                        primaryNavUrls.Add(u);
                }

                var reason = $"primary nav had zero usable links; promoted {promoted.Count} visible municipal section links";

                _telemetry?.Emit(TelemetryPhase.NavStartExtraction, "visible_links_promoted_to_structural",
                    TelemetrySeverity.Info, startAbs, new Dictionary<string, object?>
                    {
                        ["reason"]             = reason,
                        ["visibleGroupId"]     = visibleGroups.FirstOrDefault()?.Id ?? "",
                        ["promotedCount"]      = promoted.Count,
                        ["rejectedCount"]      = rejected.Count,
                        ["samplePromotedUrls"] = string.Join(", ", promoted.Take(5)),
                        ["sampleRejectedUrls"] = string.Join(", ", rejected.Take(5))
                    });

                _log.Event("NAV_VISIBLE_PROMOTED",
                    ("host", host),
                    ("promoted", promoted.Count),
                    ("rejected", rejected.Count));
            }
        }

        // Snapshot the real primary/header roots before homepage anchors are
        // added as crawl supplements. Homepage cards must not gain the same
        // protection as primary navigation roots.
        var protectedPrimaryRootSet = protectedPrimaryRootCandidates
            .Select(CanonicalUrlKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _log.Event("NAV_PRIMARY_ROOT_PROTECTION",
            ("protectedRootCount", protectedPrimaryRootSet.Count),
            ("primaryCount", primaryNavUrls.Count));

        if (rootClassification.AcceptedUrls.Count > 0)
        {
            var acceptedRootPromoted = 0;

            foreach (var u in rootClassification.AcceptedUrls)
            {
                if (string.IsNullOrWhiteSpace(u)) continue;
                if (u.Equals(startAbs, StringComparison.OrdinalIgnoreCase)) continue;
                if (primaryNavSet.Add(u))
                {
                    primaryNavUrls.Add(u);
                    acceptedRootPromoted++;
                }
            }

            if (acceptedRootPromoted > 0)
            {
                _log.Event("ACCEPTED_ROOT_CANDIDATES_PROMOTED",
                    ("host", host),
                    ("count", acceptedRootPromoted));

                _telemetry?.Emit(TelemetryPhase.NavStartExtraction, "accepted_root_candidates_promoted",
                    TelemetrySeverity.Info, startAbs, new Dictionary<string, object?>
                    {
                        ["count"] = acceptedRootPromoted
                });
            }
        }

        var allFlat = new List<NavItem>(capacity: Math.Min(opt.MaxPagesPerSite, 4096));
        var treeFlat = new List<NavItem>(capacity: Math.Min(opt.MaxPagesPerSite, 4096));

        var allFlatByUrl = new Dictionary<string, NavItem>(StringComparer.OrdinalIgnoreCase);
        var allFlatEdges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var treeFlatEdges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var bestParentByUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bestStructuralByUrl = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var q = new Queue<QItem>();

        q.Enqueue(new QItem(startAbs, 0, "", true));
        discovered.Add(startAbs);
        bestParentByUrl[startAbs] = "";
        bestStructuralByUrl[startAbs] = true;

        foreach (var u in primaryNavUrls)
        {
            if (discovered.Add(u))
            {
                q.Enqueue(new QItem(u, 1, startAbs, true));
                bestParentByUrl[u] = startAbs;
                bestStructuralByUrl[u] = true;
            }
        }

        foreach (var u in visibleSeedUrls)
        {
            if (discovered.Add(u))
            {
                q.Enqueue(new QItem(u, 1, startAbs, false));
                bestParentByUrl[u] = startAbs;
                bestStructuralByUrl[u] = false;
            }
        }

        if (primaryNavUrls.Count == 0 && visibleSeedUrls.Count == 0)
        {
            try
            {
                var rawStartLinks = await LocalNavExtractor.ExtractAsync(
                    page,
                    startAbs,
                    host,
                    opt.DropQueryStrings,
                    opt.MaxStartNavLinks);

                var fallbackSeedCount = 0;

                foreach (var link in rawStartLinks
                    .OrderByDescending(x => x.IsStructural)
                    .ThenByDescending(x => x.Confidence))
                {
                    var u = NormalizeInternalUrl(link.Url, startAbs, host, opt.DropQueryStrings);
                    if (string.IsNullOrWhiteSpace(u)) continue;
                    if (u.Equals(startAbs, StringComparison.OrdinalIgnoreCase)) continue;

                    if (discovered.Add(u))
                    {
                        q.Enqueue(new QItem(u, 1, startAbs, link.IsStructural, link.Text ?? ""));
                        bestParentByUrl[u] = startAbs;
                        bestStructuralByUrl[u] = link.IsStructural;
                        fallbackSeedCount++;
                    }
                }

                _log.Event("NAV_SEEDED_FALLBACK",
                    ("host", host),
                    ("count", fallbackSeedCount));
            }
            catch (Exception ex)
            {
                _log.Warn($"NAV_SEEDED_FALLBACK_WARN host={host} err={ex.Message}");
            }
        }
        else
        {
            _log.Event("NAV_SEEDED",
                ("host", host),
                ("primary", primaryNavUrls.Count),
                ("visible", visibleSeedUrls.Count));
        }

        // Seed additional URLs from sitemap.xml / sitemap_index.xml / robots.txt.
        // This is especially effective for WordPress and other CMS platforms that
        // publish a sitemap. URLs already in the queue are deduplicated via `discovered`.
        if (!opt.QuickPreview)
        {
            try
            {
                var sitemapUrls = await SitemapFetcher.FetchUrlsAsync(startAbs, _log);
                var sitemapAdded = 0;
                foreach (var su in sitemapUrls)
                {
                    if (allFlat.Count + q.Count >= opt.MaxPagesPerSite * 2) break;

                    var u = NormalizeInternalUrl(su, startAbs, host, opt.DropQueryStrings);
                    if (string.IsNullOrWhiteSpace(u)) continue;
                    if (u.Equals(startAbs, StringComparison.OrdinalIgnoreCase)) continue;

                    if (discovered.Add(u))
                    {
                        q.Enqueue(new QItem(u, 1, startAbs, false));
                        bestParentByUrl[u] = startAbs;
                        bestStructuralByUrl[u] = false;
                        sitemapAdded++;
                    }
                }
                if (sitemapAdded > 0)
                    _log.Event("SITEMAP_SEEDED", ("host", host), ("added", sitemapAdded));
            }
            catch (Exception ex)
            {
                _log.Warn($"SITEMAP_SEED_WARN host={host} err={ex.Message}");
            }
        }

        var depthVisited = new Dictionary<int, int>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastProgress = DateTimeOffset.Now;
        var maxPagesReachedBeforeDedupe = false;

        while (q.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            pause?.WaitIfPaused(ct);

            if (opt.QuickPreview && opt.PreviewMaxTotalSeconds > 0 && sw.Elapsed.TotalSeconds > opt.PreviewMaxTotalSeconds)
            {
                _log.Warn($"NAV_PREVIEW_TIMECAP reached {opt.PreviewMaxTotalSeconds}s visited={allFlat.Count} queue={q.Count}");
                break;
            }

            if (allFlat.Count >= opt.MaxPagesPerSite)
            {
                maxPagesReachedBeforeDedupe = true;
                _log.Warn($"NAV_MAXPAGES reached {opt.MaxPagesPerSite}");
                break;
            }

            governor?.CheckCapSmart(_cfg.OutputDir);

            var item = q.Dequeue();

            if (visited.Contains(item.Url))
                continue;

            if (opt.QuickPreview && opt.PreviewMaxPagesPerDepth > 0)
            {
                depthVisited.TryGetValue(item.Depth, out var countAtDepth);
                if (countAtDepth >= opt.PreviewMaxPagesPerDepth)
                    continue;
            }

            visited.Add(item.Url);

            if (!depthVisited.ContainsKey(item.Depth))
                depthVisited[item.Depth] = 0;
            depthVisited[item.Depth]++;

            string title = "";
            List<PageNavLink> outLinks = new();
            var visitSucceeded = false;

            _telemetry?.Emit(TelemetryPhase.Crawl, "page_visit_start", TelemetrySeverity.Info,
                item.Url, new Dictionary<string, object?>
                {
                    ["depth"] = item.Depth,
                    ["parentUrl"] = item.ParentUrl,
                    ["structural"] = item.StructuralBranch,
                    ["queueSize"] = q.Count
                });

            const int maxNavAttempts = 2;
            for (int attempt = 1; attempt <= maxNavAttempts; attempt++)
            {
                try
                {
                    await SafeGotoAsync(page, item.Url, ct, pause, opt);
                    title = await SafeTitleAsync(page, ct);

                    outLinks = await ExtractChildLinksAsync(
                        page: page,
                        currentUrl: item.Url,
                        startUrl: startAbs,
                        host: host,
                        opt: opt,
                        ct: ct,
                        pause: pause,
                        protectedTopLevel: primaryNavSet,
                        cmsExtractor: cmsExtractor);

                    visitSucceeded = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (attempt < maxNavAttempts)
                    {
                        _log.Warn($"NAV_VISIT_RETRY attempt={attempt} depth={item.Depth} url={item.Url} {ex.GetType().Name}: {ex.Message}");
                        await Task.Delay(800, ct);
                    }
                    else
                    {
                        _log.Warn($"NAV_VISIT_FAIL depth={item.Depth} url={item.Url} {ex.GetType().Name}: {ex.Message}");
                        var errorKind = ex is TimeoutException ? "timeout"
                            : ex.GetType().Name.Contains("Playwright") ? "playwright"
                            : "generic";
                        _telemetry?.Emit(TelemetryPhase.Crawl, "page_visit_fail", TelemetrySeverity.Warning,
                            item.Url, new Dictionary<string, object?>
                            {
                                ["depth"] = item.Depth,
                                ["errorKind"] = errorKind,
                                ["error"] = ex.Message
                            });
                    }
                }
            }

            if (visitSucceeded)
            {
                _telemetry?.Emit(TelemetryPhase.Crawl, "page_visit_success", TelemetrySeverity.Info,
                    item.Url, new Dictionary<string, object?>
                    {
                        ["depth"] = item.Depth,
                        ["parentUrl"] = item.ParentUrl,
                        ["outlinkCount"] = outLinks.Count,
                        ["titleLength"] = title.Length,
                        ["structural"] = item.StructuralBranch
                    });

                if (_telemetry != null)
                {
                    try
                    {
                        var fp = await PageFingerprintExtractor.ExtractAsync(page, item.Url, cms.Kind.ToString());
                        if (fp != null)
                        {
                            _telemetry.Emit(TelemetryPhase.Crawl, "page_fingerprint", TelemetrySeverity.Info,
                                item.Url, new Dictionary<string, object?>
                                {
                                    ["fingerprintHash"] = fp.FingerprintHash,
                                    ["pathShape"] = fp.PathShape,
                                    ["contentType"] = fp.ContentType,
                                    ["hasMain"] = fp.HasMain,
                                    ["hasBreadcrumb"] = fp.HasBreadcrumb,
                                    ["hasLocalNav"] = fp.HasLocalNav,
                                    ["sectionBucket"] = fp.SectionBucket
                                });
                        }
                    }
                    catch { }
                }
            }

            var actualParent = bestParentByUrl.TryGetValue(item.Url, out var storedParent)
                ? storedParent
                : (item.ParentUrl ?? "");

            var actualStructural = bestStructuralByUrl.TryGetValue(item.Url, out var storedStructural)
                ? storedStructural
                : item.StructuralBranch;

            var navItem = new NavItem
            {
                Url = item.Url,
                Title = !string.IsNullOrWhiteSpace(title)    ? title
                      : !string.IsNullOrWhiteSpace(item.Text) ? item.Text
                      : item.Url,
                Depth = item.Depth,
                ParentUrl = actualParent
            };

            if (allFlatByUrl.TryGetValue(item.Url, out var existingFlat))
            {
                existingFlat.Title = navItem.Title;
                existingFlat.Depth = Math.Min(existingFlat.Depth == 0 ? navItem.Depth : existingFlat.Depth, navItem.Depth);
            }
            else
            {
                allFlatByUrl[item.Url] = navItem;
            }

            var flatEdgeKey = $"{actualParent}|{item.Url}";
            if (allFlatEdges.Add(flatEdgeKey))
                allFlat.Add(navItem);

            if (actualStructural || item.Url.Equals(startAbs, StringComparison.OrdinalIgnoreCase))
            {
                var treeEdgeKey = $"{actualParent}|{item.Url}";
                if (treeFlatEdges.Add(treeEdgeKey))
                {
                    treeFlat.Add(new NavItem
                    {
                        Url = item.Url,
                        Title = navItem.Title,
                        Depth = navItem.Depth,
                        ParentUrl = navItem.ParentUrl
                    });
                }
            }

            var nextDepth = item.Depth + 1;
            if (nextDepth <= opt.MaxDepth && outLinks.Count > 0)
            {
                var enqueueLinks = outLinks;

                if (opt.QuickPreview && opt.PreviewMaxChildrenPerPage > 0 && enqueueLinks.Count > opt.PreviewMaxChildrenPerPage)
                    enqueueLinks = SampleSpreadLinks(enqueueLinks, opt.PreviewMaxChildrenPerPage);

                foreach (var link in enqueueLinks)
                {
                    var child = NormalizeInternalUrl(link.Url, item.Url, host, opt.DropQueryStrings);
                    if (string.IsNullOrWhiteSpace(child)) continue;

                    var attachAllowed = ShouldAttachUnderParent(
                        parentUrl: item.Url,
                        childUrl: child,
                        startUrl: startAbs,
                        protectedTopLevel: primaryNavSet,
                        isStructuralEdge: link.IsStructural);

                    if (!attachAllowed)
                        continue;

                    bool childStructural = IsStructuralChild(
                        parentUrl: item.Url,
                        childUrl: child,
                        startUrl: startAbs,
                        isParentStructural: actualStructural,
                        isStructuralEdge: link.IsStructural,
                        protectedTopLevel: primaryNavSet);

                    var parentForChild = ResolveParentForChild(
                        parentUrl: item.Url,
                        childUrl: child,
                        startUrl: startAbs,
                        isParentStructural: actualStructural,
                        isChildStructural: childStructural,
                        protectedTopLevel: primaryNavSet);

                    if (!discovered.Contains(child))
                    {
                        discovered.Add(child);
                        bestParentByUrl[child] = parentForChild;
                        bestStructuralByUrl[child] = childStructural;
                        q.Enqueue(new QItem(child, nextDepth, parentForChild, childStructural, link.Text ?? ""));
                    }
                    else
                    {
                        var currentBestParent = bestParentByUrl.TryGetValue(child, out var bp) ? bp : startAbs;
                        var currentBestStructural = bestStructuralByUrl.TryGetValue(child, out var bs) && bs;

                        if (ShouldUpgradeParent(
                            childUrl: child,
                            currentParentUrl: currentBestParent,
                            newParentUrl: parentForChild,
                            currentIsStructural: currentBestStructural,
                            newIsStructural: childStructural,
                            startUrl: startAbs))
                        {
                            bestParentByUrl[child] = parentForChild;
                            bestStructuralByUrl[child] = childStructural || currentBestStructural;

                            allFlatByUrl.TryGetValue(child, out var canonicalItem);
                            var childTitle = canonicalItem?.Title ?? child;
                            var childDepth = canonicalItem?.Depth ?? 0;

                            var newFlatEdgeKey = $"{parentForChild}|{child}";
                            if (allFlatEdges.Add(newFlatEdgeKey))
                            {
                                allFlat.Add(new NavItem
                                {
                                    Url = child,
                                    Title = childTitle,
                                    Depth = childDepth,
                                    ParentUrl = parentForChild
                                });
                            }

                            if (childStructural || currentBestStructural)
                            {
                                var newTreeEdgeKey = $"{parentForChild}|{child}";
                                if (treeFlatEdges.Add(newTreeEdgeKey))
                                {
                                    treeFlat.Add(new NavItem
                                    {
                                        Url = child,
                                        Title = childTitle,
                                        Depth = childDepth,
                                        ParentUrl = parentForChild
                                    });
                                }
                            }

                            _log.Event("NAV_REPARENT",
                                ("child", child),
                                ("oldParent", currentBestParent),
                                ("newParent", parentForChild),
                                ("structural", childStructural));
                        }
                    }
                }
            }

            var now = DateTimeOffset.Now;
            if ((now - lastProgress).TotalSeconds >= opt.ProgressEverySeconds)
            {
                lastProgress = now;
                _log.Event("NAV_PROGRESS",
                    ("host", host),
                    ("visited", allFlat.Count),
                    ("queue", q.Count),
                    ("maxPages", opt.MaxPagesPerSite),
                    ("elapsedSec", (int)sw.Elapsed.TotalSeconds));
            }
        }

        // Post-crawl URL-path reparenting pass.
        // SiteVision (and similar CMSes) often render section child pages via JavaScript,
        // so they are not visible in the DOM during the fast nav-crawl phase.  Those pages
        // end up discovered only via the start-page nav and are therefore recorded as direct
        // children of root.  After the crawl we scan allFlat for pages whose URL path puts
        // them structurally under a known section page and re-parent them so the tree
        // reflects the real hierarchy.  The fix to GetSectionPrefix / CanonicalizeSectionPrefix
        // makes IsUrlDescendantOf work correctly for numeric-ID SiteVision URLs.
        // For WordPress/Municipio, a second URL-path-based strategy handles clean-slug URLs
        // where intermediate section pages may not be in treeFlat.
        ReparentOrphansByUrlPath(allFlat, treeFlat, treeFlatEdges, startAbs, homepageSections, _log, _telemetry);

        // Synthetic intermediate parent pass.
        // When the URL-path reparenting pass above still leaves deep pages (path depth ≥ 2)
        // as direct root children, their intermediate parent URL simply never existed in the
        // crawl.  We create a synthetic placeholder node for each such missing prefix so the
        // archive viewer groups them rather than dumping hundreds of leaf pages at root.
        InsertSyntheticPrefixParents(allFlat, treeFlat, treeFlatEdges, startAbs, homepageSections, _log, _telemetry);

        allFlat = FinalizeFlatNavigation(allFlat, startAbs, _log);
        treeFlat = FinalizeTreeFlat(treeFlat, allFlat, _log);
        var visibleTreeFlat = BuildVisibleTreeFlat(
            treeFlat,
            allFlat,
            startAbs,
            rootClassification,
            protectedPrimaryRootSet,
            _log,
            _telemetry);

        // Phase 4: tag utility/meta pages for viewer grouping.
        TagUtilityPages(allFlat, _log);

        var nodes = NavTreeBuilder.Build(visibleTreeFlat, startAbs);

        foreach (var g in navGroups)
            g.Nodes = NavTreeBuilder.Build(g.Flat, startAbs);

        _telemetry?.Emit(TelemetryPhase.Crawl, "crawl_complete", TelemetrySeverity.Info,
            startAbs, new Dictionary<string, object?>
            {
                ["totalPages"] = allFlat.Count,
                ["maxDepthReached"] = depthVisited.Count > 0 ? depthVisited.Keys.Max() : 0,
                ["maxPagesReached"] = maxPagesReachedBeforeDedupe || allFlat.Count >= opt.MaxPagesPerSite,
                ["elapsedMs"] = (long)sw.Elapsed.TotalMilliseconds,
                ["pagesByDepth"] = string.Join(", ", depthVisited.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"))
            });

        return new NavIndex
        {
            Host = host,
            StartUrl = startAbs,
            CmsKind = cms.Kind.ToString(),
            GeneratedUtc = DateTime.UtcNow,
            Flat = allFlat,
            Nodes = nodes,
            NavGroups = navGroups,
            VisibleGroups = visibleGroups,
            HomepageSections = homepageSections
        };
    }

    private static List<PageNavLink> SampleSpreadLinks(List<PageNavLink> items, int take)
    {
        if (take <= 0) return items;
        if (items.Count <= take) return items;
        if (take == 1) return new List<PageNavLink> { items[0] };

        var res = new List<PageNavLink>(take);
        var step = (items.Count - 1) / (double)(take - 1);

        for (int i = 0; i < take; i++)
        {
            var idx = (int)Math.Round(i * step);
            idx = Math.Clamp(idx, 0, items.Count - 1);

            var sample = items[idx];
            if (sample != null)
                res.Add(sample);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dedup = new List<PageNavLink>(res.Count);

        foreach (var x in res)
        {
            if (x == null) continue;
            var u = x.Url ?? "";
            if (seen.Add(u))
                dedup.Add(x);
        }

        return dedup;
    }

    private async Task SafeGotoAsync(IPage page, string url, CancellationToken ct, PauseController? pause, Options opt)
    {
        ct.ThrowIfCancellationRequested();
        pause?.WaitIfPaused(ct);

        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                Timeout = _cfg.NavGotoTimeoutMs,
                WaitUntil = opt.NavWaitUntil
            });
        }
        catch (TimeoutException tex)
        {
            _log.Warn($"NAV_GOTO_TIMEOUT waitUntil={opt.NavWaitUntil} url={url} {tex.Message}");

            if (!opt.EnableFallbackGoto)
                throw;

            await page.GotoAsync(url, new PageGotoOptions
            {
                Timeout = _cfg.NavGotoTimeoutMs,
                WaitUntil = WaitUntilState.Load
            });
        }
        catch (PlaywrightException pex)
        {
            _log.Warn($"NAV_GOTO_PLAYWRIGHT url={url} {pex.Message}");
        }

        try
        {
            if (opt.WaitForAnyAnchorMs > 0)
            {
                await page.WaitForSelectorAsync("a[href]", new PageWaitForSelectorOptions
                {
                    Timeout = opt.WaitForAnyAnchorMs,
                    State = WaitForSelectorState.Attached
                });
            }
        }
        catch { }

        try
        {
            if (opt.FastNetworkIdleMs > 0)
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
                {
                    Timeout = opt.FastNetworkIdleMs
                });
            }
        }
        catch { }

        try
        {
            if (opt.MicroSettleDelayMs > 0)
                await page.WaitForTimeoutAsync(opt.MicroSettleDelayMs);
        }
        catch { }
    }

    private static async Task<string> SafeTitleAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return (await page.TitleAsync() ?? "").Trim(); }
        catch { return ""; }
    }

    private async Task<List<PageNavLink>> ExtractChildLinksAsync(
        IPage page,
        string currentUrl,
        string startUrl,
        string host,
        Options opt,
        CancellationToken ct,
        PauseController? pause,
        HashSet<string> protectedTopLevel,
        CmsAwareNavExtractor? cmsExtractor)
    {
        ct.ThrowIfCancellationRequested();
        pause?.WaitIfPaused(ct);

        var result = new List<PageNavLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (cmsExtractor != null)
        {
            try
            {
                var cmsLinks = await cmsExtractor.ExtractChildLinksAsync(
                    page,
                    currentUrl,
                    host,
                    opt.DropQueryStrings,
                    opt.MaxInnerNavLinks);

                foreach (var l in cmsLinks)
                {
                    var child = NormalizeInternalUrl(l?.Url, currentUrl, host, opt.DropQueryStrings);
                    if (string.IsNullOrWhiteSpace(child)) continue;

                    if (!ShouldAttachUnderParent(
                        parentUrl: currentUrl,
                        childUrl: child,
                        startUrl: startUrl,
                        protectedTopLevel: protectedTopLevel,
                        isStructuralEdge: l?.IsStructural ?? false))
                    {
                        continue;
                    }

                    if (seen.Add(child))
                    {
                        result.Add(new PageNavLink
                        {
                            Url = child,
                            Text = string.IsNullOrWhiteSpace(l?.Text) ? child : l!.Text.Trim(),
                            Kind = l?.Kind ?? "",
                            SourceType = string.IsNullOrWhiteSpace(l?.SourceType) ? "Generic" : l!.SourceType.Trim(),
                            DisplayRole = string.IsNullOrWhiteSpace(l?.DisplayRole) ? "VisibleContent" : l!.DisplayRole.Trim(),
                            GroupLabel = string.IsNullOrWhiteSpace(l?.GroupLabel) ? "Visible content" : l!.GroupLabel.Trim(),
                            Confidence = l?.Confidence ?? 0.0,
                            IsStructural = l?.IsStructural ?? false
                        });
                    }
                }

                if (result.Count > 0)
                {
                    _log.Event("NAV_CHILDREN_CMS",
                        ("url", currentUrl),
                        ("count", result.Count),
                        ("structural", result.Count(x => x.IsStructural)));
                    return result;
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"NAV_CMS_CHILD_EXTRACT_WARN url={currentUrl} err={ex.Message}");
            }
        }

        try
        {
            var local = await LocalNavExtractor.ExtractAsync(
                page,
                currentUrl,
                host,
                opt.DropQueryStrings,
                opt.MaxInnerNavLinks);

            if (local != null)
            {
                foreach (var l in local)
                {
                    var child = NormalizeInternalUrl(l?.Url, currentUrl, host, opt.DropQueryStrings);
                    if (string.IsNullOrWhiteSpace(child)) continue;

                    if (!ShouldAttachUnderParent(
                        parentUrl: currentUrl,
                        childUrl: child,
                        startUrl: startUrl,
                        protectedTopLevel: protectedTopLevel,
                        isStructuralEdge: l?.IsStructural ?? false))
                    {
                        continue;
                    }

                    if (seen.Add(child))
                    {
                        result.Add(new PageNavLink
                        {
                            Url = child,
                            Text = string.IsNullOrWhiteSpace(l?.Text) ? child : l!.Text.Trim(),
                            Kind = l?.Kind ?? "",
                            SourceType = string.IsNullOrWhiteSpace(l?.SourceType) ? "Generic" : l!.SourceType.Trim(),
                            DisplayRole = string.IsNullOrWhiteSpace(l?.DisplayRole) ? "VisibleContent" : l!.DisplayRole.Trim(),
                            GroupLabel = string.IsNullOrWhiteSpace(l?.GroupLabel) ? "Visible content" : l!.GroupLabel.Trim(),
                            Confidence = l?.Confidence ?? 0.0,
                            IsStructural = l?.IsStructural ?? false
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"NAV_LOCAL_EXTRACT_WARN url={currentUrl} err={ex.Message}");
        }

        if (result.Count > 0)
        {
            _log.Event("NAV_CHILDREN_LOCAL",
                ("url", currentUrl),
                ("count", result.Count),
                ("structural", result.Count(x => x.IsStructural)));
            return result;
        }

        try
        {
            string[] hrefs = await page.EvaluateAsync<string[]>(
                @"() => Array.from(document.querySelectorAll('a[href]'))
                            .map(a => a.getAttribute('href') || '')
                            .filter(h => h && h.trim().length > 0)");

            var rejectionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in hrefs)
            {
                var (child, rejectionReason) = ClassifyUrl(raw, currentUrl, host, opt.DropQueryStrings);

                if (rejectionReason != null)
                {
                    rejectionCounts.TryGetValue(rejectionReason, out var rc);
                    rejectionCounts[rejectionReason] = rc + 1;
                    _telemetry?.Emit(TelemetryPhase.LinkFiltering, "link_rejected", TelemetrySeverity.Debug,
                        raw?.Length > 200 ? raw[..200] : raw, new Dictionary<string, object?>
                        {
                            ["reason"] = rejectionReason,
                            ["sourceType"] = "fallback_href"
                        });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(child)) continue;

                if (!ShouldAttachUnderParent(
                    parentUrl: currentUrl,
                    childUrl: child,
                    startUrl: startUrl,
                    protectedTopLevel: protectedTopLevel,
                    isStructuralEdge: false))
                {
                    rejectionCounts.TryGetValue("outside_scope", out var osc);
                    rejectionCounts["outside_scope"] = osc + 1;
                    continue;
                }

                if (seen.Add(child))
                {
                    result.Add(new PageNavLink
                    {
                        Url = child,
                        Text = child,
                        Kind = "content",
                        SourceType = "Generic",
                        DisplayRole = "VisibleContent",
                        GroupLabel = "Visible content",
                        Confidence = 0.05,
                        IsStructural = false
                    });
                }
                else
                {
                    rejectionCounts.TryGetValue("duplicate", out var dup);
                    rejectionCounts["duplicate"] = dup + 1;
                }
            }

            if (rejectionCounts.Count > 0)
            {
                _telemetry?.Emit(TelemetryPhase.LinkFiltering, "link_rejections_summary",
                    TelemetrySeverity.Info, currentUrl,
                    new Dictionary<string, object?>(rejectionCounts.ToDictionary(kv => kv.Key, kv => (object?)kv.Value))
                    {
                        ["pageUrl"] = currentUrl,
                        ["accepted"] = result.Count
                    });
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"NAV_FALLBACK_LINKS_WARN url={currentUrl} err={ex.Message}");
        }

        if (opt.MaxFallbackSectionLinks > 0 && result.Count > opt.MaxFallbackSectionLinks)
            result = SampleSpreadLinks(result, opt.MaxFallbackSectionLinks);

        _log.Event("NAV_CHILDREN_FALLBACK",
            ("url", currentUrl),
            ("count", result.Count));

        return result;
    }

    // Returns the normalized URL and rejection reason (null = accepted).
    private static (string? Url, string? RejectionReason) ClassifyUrl(
        string? raw,
        string currentUrl,
        string host,
        bool dropQueryStrings)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (null, "empty_href");

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("#", StringComparison.Ordinal))
            return (null, "hash_link");
        if (trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            return (null, "javascript_link");
        if (trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return (null, "mailto");
        if (trimmed.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            return (null, "tel");

        var abs = Utils.ToAbsoluteUrl(currentUrl, trimmed);
        if (string.IsNullOrWhiteSpace(abs))
            return (null, "invalid_url");

        abs = Utils.NormalizeUrl(abs, dropQueryStrings);
        if (!Utils.IsHttpUrl(abs))
            return (null, "invalid_url");

        try
        {
            var u = new Uri(abs);
            if (!SameSiteHost(u.Host, host))
                return (null, "external_host");
            if (Utils.IsProbablyBinaryAsset(u.AbsolutePath))
                return (null, "binary_asset");
            var path = NormalizePath(u.AbsolutePath);
            if (IsNoisePath(path))
                return (null, "noise_path");
            return (abs, null);
        }
        catch
        {
            return (null, "invalid_url");
        }
    }

    private static string? NormalizeInternalUrl(
        string? raw,
        string currentUrl,
        string host,
        bool dropQueryStrings)
    {
        var abs = Utils.ToAbsoluteUrl(currentUrl, raw ?? "");
        if (string.IsNullOrWhiteSpace(abs)) return null;

        abs = Utils.NormalizeUrl(abs, dropQueryStrings);
        if (!Utils.IsHttpUrl(abs)) return null;

        try
        {
            var u = new Uri(abs);

            if (!SameSiteHost(u.Host, host))
                return null;

            if (Utils.IsProbablyBinaryAsset(u.AbsolutePath))
                return null;

            var path = NormalizePath(u.AbsolutePath);

            // Filter crawl amplifiers and CMS admin paths
            if (IsNoisePath(path))
                return null;

            if (!string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                var b = new UriBuilder(abs) { Host = host };
                abs = b.Uri.ToString().TrimEnd('/');
            }

            return abs;
        }
        catch
        {
            return null;
        }
    }

    private static bool ShouldAttachUnderParent(
        string parentUrl,
        string childUrl,
        string startUrl,
        HashSet<string> protectedTopLevel,
        bool isStructuralEdge)
    {
        if (string.IsNullOrWhiteSpace(parentUrl) || string.IsNullOrWhiteSpace(childUrl))
            return false;

        if (parentUrl.Equals(childUrl, StringComparison.OrdinalIgnoreCase))
            return false;

        if (protectedTopLevel.Contains(childUrl) &&
            !parentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (isStructuralEdge)
            return true;

        if (parentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase))
            return true;

        Uri parent;
        Uri child;

        try
        {
            parent = new Uri(parentUrl);
            child = new Uri(childUrl);
        }
        catch
        {
            return false;
        }

        if (!SameSiteHost(parent.Host, child.Host))
            return false;

        var parentSection = GetSectionPrefix(parent.AbsolutePath);
        var childPath = NormalizePath(child.AbsolutePath);

        if (string.IsNullOrWhiteSpace(parentSection) || string.IsNullOrWhiteSpace(childPath))
            return false;

        return childPath.StartsWith(parentSection + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStructuralChild(
        string parentUrl,
        string childUrl,
        string startUrl,
        bool isParentStructural,
        bool isStructuralEdge,
        HashSet<string> protectedTopLevel)
    {
        if (string.IsNullOrWhiteSpace(parentUrl) || string.IsNullOrWhiteSpace(childUrl))
            return false;

        if (protectedTopLevel.Contains(childUrl))
            return true;

        if (isStructuralEdge)
            return true;

        if (!isParentStructural)
            return false;

        if (parentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase))
            return false;

        return IsUrlDescendantOf(parentUrl, childUrl);
    }

    private static string ResolveParentForChild(
        string parentUrl,
        string childUrl,
        string startUrl,
        bool isParentStructural,
        bool isChildStructural,
        HashSet<string> protectedTopLevel)
    {
        if (protectedTopLevel.Contains(childUrl))
            return startUrl;

        if (string.IsNullOrWhiteSpace(parentUrl))
            return startUrl;

        if (parentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase))
            return startUrl;

        if (isChildStructural)
            return parentUrl;

        if (!isParentStructural && IsUrlDescendantOf(parentUrl, childUrl))
            return parentUrl;

        return startUrl;
    }

    private static bool ShouldUpgradeParent(
        string childUrl,
        string currentParentUrl,
        string newParentUrl,
        bool currentIsStructural,
        bool newIsStructural,
        string startUrl)
    {
        if (string.IsNullOrWhiteSpace(newParentUrl))
            return false;

        if (string.Equals(currentParentUrl, newParentUrl, StringComparison.OrdinalIgnoreCase))
            return false;

        if (newIsStructural && !currentIsStructural)
            return true;

        if (string.IsNullOrWhiteSpace(currentParentUrl) ||
            string.Equals(currentParentUrl, startUrl, StringComparison.OrdinalIgnoreCase))
            return !string.Equals(newParentUrl, startUrl, StringComparison.OrdinalIgnoreCase);

        if (IsUrlDescendantOf(currentParentUrl, newParentUrl))
            return false;

        if (IsUrlDescendantOf(newParentUrl, currentParentUrl))
            return true;

        return false;
    }

    private static bool IsUrlDescendantOf(string parentUrl, string childUrl)
    {
        Uri parent;
        Uri child;

        try
        {
            parent = new Uri(parentUrl);
            child = new Uri(childUrl);
        }
        catch
        {
            return false;
        }

        if (!SameSiteHost(parent.Host, child.Host))
            return false;

        var parentSection = GetSectionPrefix(parent.AbsolutePath);
        var childPath = NormalizePath(child.AbsolutePath);

        if (string.IsNullOrWhiteSpace(parentSection) || string.IsNullOrWhiteSpace(childPath))
            return false;

        return childPath.StartsWith(parentSection + "/", StringComparison.OrdinalIgnoreCase);
    }

    // Synthetic URL-prefix parent pass.
    //
    // After ReparentOrphansByUrlPath, some pages may still be direct root children because
    // their intermediate parent URL (e.g. /fritidsaktiviteter) was never crawled and therefore
    // has no entry in allFlat.  ReparentOrphansByUrlPath cannot fix these because it only
    // looks up ancestors that already exist in the known-URL set.
    //
    // This pass groups remaining deep root children by their immediate URL prefix and creates
    // a lightweight synthetic NavItem for each missing prefix that has ≥ 2 children.
    // Synthetic nodes are attached to the nearest known ancestor (or root if none exists),
    // and all orphaned children are re-parented under them.
    //
    // Safeguards:
    //   - Only synthesises when ≥ 2 children share the missing prefix.
    //   - Skips numeric-only or date-pattern slugs (e.g. /2024-05-01, /12345).
    //   - Skips prefixes that already exist in allFlat (no synthesis needed).
    //   - Never creates cycles.
    //   - Processes groups sorted by URL depth (shallow first) so chained missing
    //     parents (e.g. /a missing, /a/b missing) resolve correctly in one pass.
    private static void InsertSyntheticPrefixParents(
        List<NavItem> allFlat,
        List<NavItem> treeFlat,
        HashSet<string> treeFlatEdges,
        string startUrl,
        List<HomepageSection>? homepageSections = null,
        Logger? log = null,
        TelemetryWriter? telemetry = null)
    {
        // Build current known-URL index.
        var knownUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allFlatByUrl = new Dictionary<string, NavItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in allFlat)
        {
            if (string.IsNullOrWhiteSpace(it.Url)) continue;
            knownUrls.Add(it.Url);
            if (!allFlatByUrl.ContainsKey(it.Url))
                allFlatByUrl[it.Url] = it;
        }

        var treeFlatByUrl = new Dictionary<string, NavItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in treeFlat)
            if (!string.IsNullOrWhiteSpace(it.Url) && !treeFlatByUrl.ContainsKey(it.Url))
                treeFlatByUrl[it.Url] = it;

        // Count root children before changes for telemetry.
        var rootChildBefore = 0;
        var deepRootChildBefore = 0;
        foreach (var it in allFlat)
        {
            if (string.IsNullOrWhiteSpace(it.Url)) continue;
            if (it.Url.Equals(startUrl, StringComparison.OrdinalIgnoreCase)) continue;
            if (!it.ParentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase)) continue;
            rootChildBefore++;
            if (CountUrlPathSegments(it.Url) >= 2)
                deepRootChildBefore++;
        }

        log?.Event("ROOT_CHILD_QUALITY",
            ("rootChildCount",              rootChildBefore),
            ("deepRootChildCount",          deepRootChildBefore),
            ("suspiciousLeafRootChildCount", deepRootChildBefore));

        if (deepRootChildBefore == 0)
            return;

        // Find candidates: root children with path depth ≥ 2.
        var candidates = new List<NavItem>();
        foreach (var it in allFlat)
        {
            if (string.IsNullOrWhiteSpace(it.Url)) continue;
            if (it.Url.Equals(startUrl, StringComparison.OrdinalIgnoreCase)) continue;
            if (!it.ParentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase)) continue;
            if (CountUrlPathSegments(it.Url) < 2) continue;
            candidates.Add(it);
        }

        if (candidates.Count == 0)
            return;

        // Group candidates by their immediate URL prefix (one segment shorter).
        var prefixGroups = new Dictionary<string, List<NavItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in candidates)
        {
            var prefix = GetImmediatePrefixUrl(item.Url);
            if (string.IsNullOrWhiteSpace(prefix)) continue;
            if (prefix.Equals(startUrl, StringComparison.OrdinalIgnoreCase)) continue;
            if (!prefixGroups.TryGetValue(prefix, out var group))
            {
                group = new List<NavItem>();
                prefixGroups[prefix] = group;
            }
            group.Add(item);
        }

        // Process groups shallowest-first so chained missing parents resolve correctly.
        var sortedPrefixes = prefixGroups.Keys
            .OrderBy(CountUrlPathSegments)
            .ToList();

        var syntheticCount = 0;

        foreach (var prefixUrl in sortedPrefixes)
        {
            var children = prefixGroups[prefixUrl];

            var slug = GetLastPathSegment(prefixUrl);
            var syntheticParentUrl = FindBestSyntheticParentUrl(prefixUrl, knownUrls, startUrl);
            var decision = ScoreSyntheticParentCandidate(
                prefixUrl,
                slug,
                children,
                knownUrls,
                allFlat,
                startUrl,
                syntheticParentUrl,
                deepRootChildBefore);

            EmitDecision(log, telemetry, TelemetryPhase.TreeBuilding, "SYNTHETIC_PARENT_CANDIDATE", decision);

            if (!decision.Accepted)
            {
                EmitDecision(log, telemetry, TelemetryPhase.TreeBuilding, "SYNTHETIC_PARENT_REJECTED", decision);
                continue;
            }

            // Use homepage section title if one was captured for this URL (Phase 3).
            var hsTitle = homepageSections?.FirstOrDefault(
                hs => hs.Url.Equals(prefixUrl, StringComparison.OrdinalIgnoreCase))?.Title;
            var title = !string.IsNullOrWhiteSpace(hsTitle) ? hsTitle : SlugToTitle(slug);

            // Find best parent: nearest known URL ancestor or root.
            var syntheticDepth = allFlatByUrl.TryGetValue(syntheticParentUrl, out var parentItem)
                ? parentItem.Depth + 1
                : 1;

            // Create synthetic allFlat node.
            var syntheticNode = new NavItem
            {
                Url        = prefixUrl,
                Title      = title,
                ParentUrl  = syntheticParentUrl,
                Depth      = syntheticDepth,
                IsSynthetic = true
            };

            allFlat.Add(syntheticNode);
            allFlatByUrl[prefixUrl] = syntheticNode;
            knownUrls.Add(prefixUrl);

            // Create corresponding treeFlat node.
            if (!treeFlatByUrl.ContainsKey(prefixUrl))
            {
                var treeNode = new NavItem
                {
                    Url        = prefixUrl,
                    Title      = title,
                    ParentUrl  = syntheticParentUrl,
                    Depth      = syntheticDepth,
                    IsSynthetic = true
                };
                treeFlat.Add(treeNode);
                treeFlatByUrl[prefixUrl] = treeNode;
                treeFlatEdges.Add($"{syntheticParentUrl}|{prefixUrl}");
            }

            // Re-parent all children under the synthetic node.
            foreach (var child in children)
            {
                child.ParentUrl = prefixUrl;
                child.Depth     = syntheticDepth + 1;

                if (treeFlatByUrl.TryGetValue(child.Url, out var treeChild))
                {
                    treeChild.ParentUrl = prefixUrl;
                    treeChild.Depth     = syntheticDepth + 1;
                }

                var childEdgeKey = $"{prefixUrl}|{child.Url}";
                if (treeFlatEdges.Add(childEdgeKey) && !treeFlatByUrl.ContainsKey(child.Url))
                {
                    var newTreeChild = new NavItem
                    {
                        Url       = child.Url,
                        Title     = child.Title,
                        ParentUrl = prefixUrl,
                        Depth     = syntheticDepth + 1
                    };
                    treeFlat.Add(newTreeChild);
                    treeFlatByUrl[child.Url] = newTreeChild;
                }
            }

            log?.Event("SYNTHETIC_PARENT_CREATED",
                ("url",        prefixUrl),
                ("title",      title),
                ("childCount", children.Count),
                ("parentUrl",  syntheticParentUrl),
                ("confidence", decision.Confidence.ToString("0.00")),
                ("positiveEvidence", string.Join("|", decision.PositiveEvidence)),
                ("negativeEvidence", string.Join("|", decision.NegativeEvidence)),
                ("reason",     "synthetic_url_prefix_parent"));

            telemetry?.Emit(TelemetryPhase.TreeBuilding, "SYNTHETIC_PARENT_CREATED", TelemetrySeverity.Info,
                prefixUrl, DecisionFields(decision, new Dictionary<string, object?>
                {
                    ["parentUrl"] = syntheticParentUrl,
                    ["childCount"] = children.Count
                }));

            syntheticCount++;
        }

        if (syntheticCount == 0)
            return;

        // Report root-leaf pollution reduction.
        var deepRootChildAfter = 0;
        foreach (var it in allFlat)
        {
            if (string.IsNullOrWhiteSpace(it.Url)) continue;
            if (it.Url.Equals(startUrl, StringComparison.OrdinalIgnoreCase)) continue;
            if (!it.ParentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase)) continue;
            if (CountUrlPathSegments(it.Url) >= 2)
                deepRootChildAfter++;
        }

        log?.Event("ROOT_LEAF_POLLUTION_REDUCED",
            ("before",                deepRootChildBefore),
            ("after",                 deepRootChildAfter),
            ("syntheticParentsCreated", syntheticCount));
    }

    // Returns the URL one path segment shorter than the given URL.
    // e.g. https://www.eslov.se/fritidsaktiviteter/eslovs-tennisklubb
    //   => https://www.eslov.se/fritidsaktiviteter
    private static string? GetImmediatePrefixUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var segments = u.AbsolutePath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;
        var parentPath = "/" + string.Join("/", segments, 0, segments.Length - 1);
        return $"{u.Scheme}://{u.Host}{parentPath}";
    }

    // Returns the last path segment of a URL (the slug).
    private static string GetLastPathSegment(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath.TrimEnd('/');
            var lastSlash = path.LastIndexOf('/');
            return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        }
        catch { return url; }
    }

    // Returns true when a slug is all-numeric or matches a date pattern (YYYY, YYYY-MM, YYYY-MM-DD).
    // These are not real section names and should not get synthetic parents.
    private static bool IsNumericOrDateSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return true;
        if (slug.All(char.IsDigit)) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(slug, @"^\d{4}(-\d{2}){0,2}$");
    }

    // Converts a URL slug into a human-readable title.
    // "fritidsaktiviteter" => "Fritidsaktiviteter"
    // "lediga-jobb"        => "Lediga jobb"
    private static string SlugToTitle(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return slug;
        var spaced = slug.Replace('-', ' ');
        return spaced.Length > 0
            ? char.ToUpperInvariant(spaced[0]) + spaced[1..]
            : spaced;
    }

    // Walks up the URL path one segment at a time and returns the URL of the first
    // ancestor found in knownUrls.  Falls back to startUrl when none is found.
    private static string FindBestSyntheticParentUrl(
        string prefixUrl,
        HashSet<string> knownUrls,
        string startUrl)
    {
        if (!Uri.TryCreate(prefixUrl, UriKind.Absolute, out var u))
            return startUrl;

        var segments = u.AbsolutePath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (int i = segments.Length - 1; i >= 1; i--)
        {
            var ancestorPath = "/" + string.Join("/", segments, 0, i);
            var ancestorUrl  = $"{u.Scheme}://{u.Host}{ancestorPath}";
            if (ancestorUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase))
                return startUrl;
            if (knownUrls.Contains(ancestorUrl))
                return ancestorUrl;
        }

        return startUrl;
    }

    // After the crawl loop, pages seeded directly from the start page may have a more
    // specific natural parent (a structural section page whose URL is an ancestor of theirs).
    // This happens on:
    //   SiteVision: section landing pages render child-page grids via JS, invisible during crawl.
    //   WordPress/Municipio: WP REST API returns all pages flat; sitemap seeds attach to root.
    //
    // Two strategies are used in order:
    //   1. Section-page ancestor lookup (treeFlat depth-1 items + IsUrlDescendantOf).
    //      Handles SiteVision numeric-ID URLs via GetSectionPrefix/CanonicalizeSectionPrefix.
    //   2. URL-path ancestor lookup from allFlatByUrl (all visited pages).
    //      Handles WordPress clean-slug URLs where intermediate pages may not be in treeFlat.
    //
    // Candidates are sorted by URL path segment count (ascending) so that intermediate
    // section pages are reparented before their children — this ensures
    // item.Depth = parent.Depth + 1 is computed correctly in a single pass.
    //
    // When an item is reparented, its corresponding treeFlat entry (a separate object) is
    // also updated in-place so NavTreeBuilder.Build(treeFlat) sees the corrected parent.
    private static void ReparentOrphansByUrlPath(
        List<NavItem> allFlat,
        List<NavItem> treeFlat,
        HashSet<string> treeFlatEdges,
        string startUrl,
        List<HomepageSection>? homepageSections = null,
        Logger? log = null,
        TelemetryWriter? telemetry = null)
    {
        // Strategy 1 source: structural section pages already in treeFlat at depth 1.
        var sectionPages = treeFlat
            .Where(p => !string.IsNullOrWhiteSpace(p.Url)
                     && !p.Url.Equals(startUrl, StringComparison.OrdinalIgnoreCase)
                     && p.ParentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Strategy 2 source: all visited pages (allFlat) keyed by URL.
        var allFlatByUrl = new Dictionary<string, NavItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in allFlat)
            if (!string.IsNullOrWhiteSpace(it.Url) && !allFlatByUrl.ContainsKey(it.Url))
                allFlatByUrl[it.Url] = it;

        // Phase 3: augment section candidates with homepage sections that were crawled
        // but may not be structural treeFlat depth-1 items (e.g. discovered via visible
        // groups rather than primary nav). This lets URL-prefix reparenting use the
        // municipality's own IA anchors as candidate parents.
        if (homepageSections != null && homepageSections.Count > 0)
        {
            var sectionUrlSet = sectionPages
                .Select(p => p.Url)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var hs in homepageSections)
            {
                if (string.IsNullOrWhiteSpace(hs.Url)) continue;
                if (sectionUrlSet.Contains(hs.Url)) continue;
                if (allFlatByUrl.TryGetValue(hs.Url, out var hsItem))
                {
                    sectionPages.Add(hsItem);
                    sectionUrlSet.Add(hs.Url);
                }
            }
        }

        // treeFlat item index: for in-place parent update when reparenting.
        var treeFlatByUrl = new Dictionary<string, NavItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in treeFlat)
            if (!string.IsNullOrWhiteSpace(it.Url) && !treeFlatByUrl.ContainsKey(it.Url))
                treeFlatByUrl[it.Url] = it;

        if (sectionPages.Count == 0 && allFlatByUrl.Count == 0)
            return;

        // Snapshot root-level count before changes (for telemetry).
        var rootLevelBefore = 0;
        foreach (var it in allFlat)
        {
            if (!string.IsNullOrWhiteSpace(it.Url)
                && !it.Url.Equals(startUrl, StringComparison.OrdinalIgnoreCase)
                && it.ParentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase))
                rootLevelBefore++;
        }

        // Candidates: allFlat root-children sorted by segment count (parents before children).
        var candidates = new List<NavItem>(allFlat.Count);
        foreach (var it in allFlat)
        {
            if (string.IsNullOrWhiteSpace(it.Url)) continue;
            if (it.Url.Equals(startUrl, StringComparison.OrdinalIgnoreCase)) continue;
            if (!it.ParentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase)) continue;
            candidates.Add(it);
        }
        candidates.Sort((a, b) =>
            CountUrlPathSegments(a.Url).CompareTo(CountUrlPathSegments(b.Url)));

        var inferred = 0;

        foreach (var item in candidates)
        {
            // -- Strategy 1: deepest treeFlat-section-page ancestor (SiteVision-safe) --
            NavItem? bestParent = null;
            int bestPrefixLen = 0;

            foreach (var section in sectionPages)
            {
                if (section.Url.Equals(item.Url, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsUrlDescendantOf(section.Url, item.Url)) continue;

                if (!Uri.TryCreate(section.Url, UriKind.Absolute, out var sectionUri)) continue;
                var prefixLen = GetSectionPrefix(sectionUri.AbsolutePath).Length;
                if (prefixLen > bestPrefixLen)
                {
                    bestPrefixLen = prefixLen;
                    bestParent = section;
                }
            }

            // -- Strategy 2: URL-path lookup in allFlatByUrl (WordPress clean-slug URLs) --
            // Used when Strategy 1 found no ancestor (e.g. intermediate section page is in
            // allFlat but not in treeFlat, or was reparented and is no longer at depth 1).
            if (bestParent == null)
            {
                var ancestorUrl = FindNearestKnownAncestorUrl(item.Url, allFlatByUrl, startUrl);
                if (ancestorUrl != null)
                    bestParent = allFlatByUrl[ancestorUrl];
            }

            var isStructuralParent = bestParent != null && sectionPages.Contains(bestParent);
            var decision = ScoreUrlParentCandidate(item, bestParent, startUrl, isStructuralParent);

            if (bestParent != null)
                EmitDecision(log, telemetry, TelemetryPhase.TreeBuilding, "URL_PARENT_CANDIDATE", decision);

            if (bestParent == null)
            {
                if (CountUrlPathSegments(item.Url) >= 2)
                {
                    var noParent = new NavigationDecisionEvidence
                    {
                        DecisionType = "url_parent_inference",
                        CandidateUrl = item.Url,
                        CandidateTitle = item.Title,
                        Accepted = false,
                        Confidence = 0,
                        Threshold = 0.6,
                        NegativeEvidence = new List<string> { "no_known_url_ancestor" },
                        FinalReason = "rejected:no_known_url_ancestor"
                    };
                    EmitDecision(log, telemetry, TelemetryPhase.TreeBuilding, "URL_PARENT_REJECTED", noParent);
                }
                continue;
            }

            if (!decision.Accepted)
            {
                EmitDecision(log, telemetry, TelemetryPhase.TreeBuilding, "URL_PARENT_LOW_CONFIDENCE", decision);
                EmitDecision(log, telemetry, TelemetryPhase.TreeBuilding, "URL_PARENT_REJECTED", decision);
                continue;
            }

            item.ParentUrl = bestParent.Url;
            item.Depth     = bestParent.Depth + 1;

            // Update the treeFlat item in-place so NavTreeBuilder.Build(treeFlat) sees the
            // corrected parent.  treeFlat items are separate objects from allFlat items.
            if (treeFlatByUrl.TryGetValue(item.Url, out var treeItem)
                && treeItem.ParentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase))
            {
                treeItem.ParentUrl = bestParent.Url;
                treeItem.Depth     = bestParent.Depth + 1;
            }

            // Ensure the reparented item has a treeFlat edge under the new parent.
            var edgeKey = $"{bestParent.Url}|{item.Url}";
            if (treeFlatEdges.Add(edgeKey))
            {
                treeFlat.Add(new NavItem
                {
                    Url       = item.Url,
                    Title     = item.Title,
                    Depth     = item.Depth,
                    ParentUrl = item.ParentUrl
                });
            }

            log?.Event("URL_PARENT_INFERRED",
                ("url",    item.Url),
                ("parent", bestParent.Url),
                ("confidence", decision.Confidence.ToString("0.00")),
                ("positiveEvidence", string.Join("|", decision.PositiveEvidence)),
                ("negativeEvidence", string.Join("|", decision.NegativeEvidence)));

            telemetry?.Emit(TelemetryPhase.TreeBuilding, "URL_PARENT_INFERRED", TelemetrySeverity.Info,
                item.Url, DecisionFields(decision, new Dictionary<string, object?>
                {
                    ["parentUrl"] = bestParent.Url
                }));

            inferred++;
        }

        if (inferred > 0)
        {
            var rootLevelAfter = 0;
            foreach (var it in allFlat)
            {
                if (!string.IsNullOrWhiteSpace(it.Url)
                    && !it.Url.Equals(startUrl, StringComparison.OrdinalIgnoreCase)
                    && it.ParentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase))
                    rootLevelAfter++;
            }

            log?.Event("URL_HIERARCHY_RECONSTRUCTION_USED",
                ("inferred",       inferred),
                ("rootLevelBefore", rootLevelBefore),
                ("rootLevelAfter",  rootLevelAfter));

            log?.Event("ROOT_LEVEL_PAGE_REDUCED",
                ("before",  rootLevelBefore),
                ("after",   rootLevelAfter),
                ("reduced", rootLevelBefore - rootLevelAfter));

            try
            {
                var maxDepthAfter = allFlat.Count > 0 ? allFlat.Max(it => it.Depth) : 0;
                log?.Event("HIERARCHY_RECONSTRUCTION_SUMMARY",
                    ("startUrl",              startUrl),
                    ("rootLevelBefore",       rootLevelBefore),
                    ("rootLevelAfter",        rootLevelAfter),
                    ("inferredRelationships", inferred),
                    ("maxDepthAfter",         maxDepthAfter));
            }
            catch { }
        }
    }

    private static List<NavItem> FinalizeFlatNavigation(
        List<NavItem> flat,
        string startUrl,
        Logger? log)
    {
        var canonicalStart = CanonicalUrlKey(startUrl);
        var byKey = new Dictionary<string, NavItem>(StringComparer.OrdinalIgnoreCase);
        var firstOrder = new List<string>();
        var duplicateExtras = 0;

        foreach (var raw in flat)
        {
            var key = CanonicalUrlKey(raw.Url);
            if (string.IsNullOrWhiteSpace(key)) continue;

            var normalized = CloneWithCanonicalUrls(raw);

            if (!byKey.TryGetValue(key, out var existing))
            {
                byKey[key] = normalized;
                firstOrder.Add(key);
                continue;
            }

            duplicateExtras++;
            byKey[key] = ChooseBetterNavItem(existing, normalized, canonicalStart);
        }

        if (duplicateExtras > 0)
            log?.Event("DUPLICATE_URLS_REMOVED",
                ("duplicatesRemoved", duplicateExtras),
                ("uniqueUrls", byKey.Count));

        var keptKeys = byKey.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var urlByKey = byKey.ToDictionary(kv => kv.Key, kv => kv.Value.Url, StringComparer.OrdinalIgnoreCase);

        foreach (var item in byKey.Values)
        {
            var itemKey = CanonicalUrlKey(item.Url);

            if (itemKey.Equals(canonicalStart, StringComparison.OrdinalIgnoreCase))
            {
                item.ParentUrl = "";
                continue;
            }

            var parentKey = CanonicalUrlKey(item.ParentUrl);
            if (string.IsNullOrWhiteSpace(parentKey)
                || parentKey.Equals(itemKey, StringComparison.OrdinalIgnoreCase)
                || !keptKeys.Contains(parentKey)
                || IsUrlDescendantOf(item.Url, item.ParentUrl))
            {
                parentKey = FindNearestKnownAncestorKey(item.Url, keptKeys, canonicalStart)
                    ?? canonicalStart;
            }

            item.ParentUrl = urlByKey.TryGetValue(parentKey, out var parentUrl)
                ? parentUrl
                : startUrl;

            if (IsRawUrlLikeTitle(item.Title, item.Url))
            {
                var resolvedTitle = ResolveDisplayTitleFallback(item.Title, item.Url);
                if (!string.IsNullOrWhiteSpace(resolvedTitle) &&
                    !resolvedTitle.Equals(item.Title, StringComparison.OrdinalIgnoreCase))
                {
                    log?.Event("DISPLAY_TITLE_RESOLVED",
                        ("url", item.Url),
                        ("title", item.Title),
                        ("resolvedTitle", resolvedTitle),
                        ("reason", "humanized_slug"));
                    item.Title = resolvedTitle;
                }
                else
                {
                    log?.Event("DISPLAY_TITLE_FALLBACK_USED",
                        ("url", item.Url),
                        ("title", item.Title),
                        ("reason", "url_fallback"));
                }
            }
        }

        var recalculated = RecalculateDepths(
            firstOrder.Select(k => byKey[k]).ToList(),
            startUrl,
            log);

        return recalculated;
    }

    private static List<NavItem> FinalizeTreeFlat(
        List<NavItem> treeFlat,
        List<NavItem> correctedFlat,
        Logger? log)
    {
        if (treeFlat.Count == 0)
            return correctedFlat;

        var structuralKeys = treeFlat
            .Select(x => CanonicalUrlKey(x.Url))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var synced = correctedFlat
            .Where(x => structuralKeys.Contains(CanonicalUrlKey(x.Url)))
            .Select(CloneWithCanonicalUrls)
            .ToList();

        return synced.Count > 0 ? synced : correctedFlat;
    }

    private static List<NavItem> BuildVisibleTreeFlat(
        List<NavItem> treeFlat,
        List<NavItem> allFlat,
        string startUrl,
        RootCandidateClassification rootClassification,
        HashSet<string> protectedPrimaryRootKeys,
        Logger? log,
        TelemetryWriter? telemetry)
    {
        if (treeFlat.Count == 0)
            return allFlat;

        var acceptedRootKeys = rootClassification.AcceptedUrls
            .Select(CanonicalUrlKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rejectedRootKeys = rootClassification.RejectedUrls
            .Select(CanonicalUrlKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var canonicalStart = CanonicalUrlKey(startUrl);
        var hasRootClassification = rootClassification.CandidateCount > 0 && acceptedRootKeys.Count > 0;

        var visibleRootChildKeysBefore = treeFlat
            .Where(x => !CanonicalUrlKey(x.Url).Equals(canonicalStart, StringComparison.OrdinalIgnoreCase)
                     && CanonicalUrlKey(x.ParentUrl).Equals(canonicalStart, StringComparison.OrdinalIgnoreCase))
            .Select(x => CanonicalUrlKey(x.Url))
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var visibleRootChildrenBefore = visibleRootChildKeysBefore.Count;
        var acceptedPrimaryRootOverlap = acceptedRootKeys.Count(k => visibleRootChildKeysBefore.Contains(k));

        void LogSuppressionSkipped(string reason, int orphanedChildren = 0, int visibleRootChildrenAfter = -1)
        {
            var after = visibleRootChildrenAfter < 0 ? visibleRootChildrenBefore : visibleRootChildrenAfter;
            log?.Event("VISIBLE_ROOT_CLASSIFICATION_SKIPPED",
                ("reason", reason),
                ("rootCandidatesAccepted", rootClassification.AcceptedUrls.Count),
                ("rootCandidatesRejected", rootClassification.RejectedUrls.Count),
                ("acceptedPrimaryRootOverlap", acceptedPrimaryRootOverlap),
                ("visibleRootChildrenBefore", visibleRootChildrenBefore),
                ("visibleRootChildrenAfter", after),
                ("orphanedChildrenAfterSuppression", orphanedChildren),
                ("crawlableButNotVisibleRoot", CountAllFlatRootChildrenNotInTree(allFlat, treeFlat, startUrl)));

            EmitVisibleRootGuardrailTelemetry(
                telemetry,
                startUrl,
                "skipped",
                reason,
                rootClassification.AcceptedUrls.Count,
                rootClassification.RejectedUrls.Count,
                acceptedPrimaryRootOverlap,
                visibleRootChildrenBefore,
                after,
                CountAllFlatRootChildrenNotInTree(allFlat, treeFlat, startUrl),
                orphanedChildren);
        }

        if (!hasRootClassification)
        {
            LogSuppressionSkipped(acceptedRootKeys.Count == 0
                ? "no_accepted_homepage_anchors"
                : "no_homepage_root_classification");

            EmitVisibleRootTelemetry(
                telemetry,
                startUrl,
                rootClassification.AcceptedUrls.Count,
                rootClassification.RejectedUrls.Count,
                visibleRootChildrenBefore,
                visibleRootChildrenBefore,
                0,
                CountAllFlatRootChildrenNotInTree(allFlat, treeFlat, startUrl),
                0);
            return ApplyRootNavigationPurityFilter(
                treeFlat,
                allFlat,
                startUrl,
                rootClassification,
                protectedPrimaryRootKeys,
                log,
                telemetry);
        }

        var acceptedAnchorCount = acceptedRootKeys.Count;
        var sparseAcceptedAnchors = acceptedAnchorCount < 2 && visibleRootChildrenBefore >= 3;
        var noPrimaryOverlap = acceptedPrimaryRootOverlap == 0 && visibleRootChildrenBefore > 0;
        var lowPrimaryOverlap = visibleRootChildrenBefore >= 4
            && acceptedPrimaryRootOverlap > 0
            && acceptedPrimaryRootOverlap < Math.Min(2, acceptedAnchorCount);

        if (sparseAcceptedAnchors)
        {
            LogSuppressionSkipped("sparse_homepage_anchors");
            return ApplyRootNavigationPurityFilter(
                treeFlat,
                allFlat,
                startUrl,
                rootClassification,
                protectedPrimaryRootKeys,
                log,
                telemetry);
        }

        if (noPrimaryOverlap || lowPrimaryOverlap)
        {
            LogSuppressionSkipped(noPrimaryOverlap
                ? "homepage_anchors_do_not_overlap_primary_roots"
                : "homepage_anchors_low_overlap_with_primary_roots");
            return ApplyRootNavigationPurityFilter(
                treeFlat,
                allFlat,
                startUrl,
                rootClassification,
                protectedPrimaryRootKeys,
                log,
                telemetry);
        }

        var suppressedRootKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in treeFlat)
        {
            var itemKey = CanonicalUrlKey(item.Url);
            if (itemKey.Equals(canonicalStart, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!CanonicalUrlKey(item.ParentUrl).Equals(canonicalStart, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!acceptedRootKeys.Contains(itemKey))
                suppressedRootKeys.Add(itemKey);
        }

        var orphanedChildrenAfterSuppression = 0;
        var visibleByKey = new Dictionary<string, NavItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in treeFlat)
        {
            var itemKey = CanonicalUrlKey(item.Url);
            if (string.IsNullOrWhiteSpace(itemKey))
                continue;

            if (itemKey.Equals(canonicalStart, StringComparison.OrdinalIgnoreCase))
            {
                visibleByKey[itemKey] = CloneWithCanonicalUrls(item);
                continue;
            }

            if (suppressedRootKeys.Contains(itemKey))
                continue;

            var clone = CloneWithCanonicalUrls(item);
            var parentKey = CanonicalUrlKey(clone.ParentUrl);
            if (!visibleByKey.ContainsKey(parentKey))
            {
                var reassignedParent = FindAcceptedAncestorUrl(clone.Url, rootClassification.AcceptedUrls, startUrl);
                if (string.IsNullOrWhiteSpace(reassignedParent))
                {
                    orphanedChildrenAfterSuppression++;
                    continue;
                }

                clone.ParentUrl = reassignedParent;
            }

            visibleByKey[itemKey] = clone;
        }

        var visible = visibleByKey.Values.ToList();

        var visibleRootChildrenAfter = visible
            .Count(x => !CanonicalUrlKey(x.Url).Equals(canonicalStart, StringComparison.OrdinalIgnoreCase)
                     && CanonicalUrlKey(x.ParentUrl).Equals(canonicalStart, StringComparison.OrdinalIgnoreCase));

        var rejectedSuppressed = suppressedRootKeys.Count(k => rejectedRootKeys.Contains(k));
        var crawlableButNotVisibleRoot = CountAllFlatRootChildrenNotInTree(allFlat, visible, startUrl);

        var collapsedVisibleRoots = visibleRootChildrenBefore >= 3
            && visibleRootChildrenAfter < Math.Max(2, visibleRootChildrenBefore / 2);
        var massiveOrphaning = orphanedChildrenAfterSuppression >= 25
            && orphanedChildrenAfterSuppression > Math.Max(visibleRootChildrenAfter * 5, allFlat.Count / 10);

        if (visibleRootChildrenAfter == 0)
        {
            LogSuppressionSkipped("suppression_would_empty_navigation", orphanedChildrenAfterSuppression, visibleRootChildrenAfter);
            return ApplyRootNavigationPurityFilter(
                treeFlat,
                allFlat,
                startUrl,
                rootClassification,
                protectedPrimaryRootKeys,
                log,
                telemetry);
        }

        if (collapsedVisibleRoots)
        {
            LogSuppressionSkipped("suppression_would_collapse_visible_roots", orphanedChildrenAfterSuppression, visibleRootChildrenAfter);
            return ApplyRootNavigationPurityFilter(
                treeFlat,
                allFlat,
                startUrl,
                rootClassification,
                protectedPrimaryRootKeys,
                log,
                telemetry);
        }

        if (massiveOrphaning)
        {
            LogSuppressionSkipped("suppression_would_massively_orphan_descendants", orphanedChildrenAfterSuppression, visibleRootChildrenAfter);
            return ApplyRootNavigationPurityFilter(
                treeFlat,
                allFlat,
                startUrl,
                rootClassification,
                protectedPrimaryRootKeys,
                log,
                telemetry);
        }

        log?.Event("VISIBLE_ROOT_CLASSIFICATION_APPLIED",
            ("rootCandidatesAccepted", rootClassification.AcceptedUrls.Count),
            ("rootCandidatesRejected", rootClassification.RejectedUrls.Count),
            ("acceptedPrimaryRootOverlap", acceptedPrimaryRootOverlap),
            ("visibleRootChildrenBefore", visibleRootChildrenBefore),
            ("visibleRootChildrenAfter", visibleRootChildrenAfter),
            ("rejectedCandidatesSuppressedFromVisibleRoot", rejectedSuppressed),
            ("crawlableButNotVisibleRoot", crawlableButNotVisibleRoot),
            ("orphanedChildrenAfterSuppression", orphanedChildrenAfterSuppression));

        EmitVisibleRootTelemetry(
            telemetry,
            startUrl,
            rootClassification.AcceptedUrls.Count,
            rootClassification.RejectedUrls.Count,
            visibleRootChildrenBefore,
            visibleRootChildrenAfter,
            rejectedSuppressed,
            crawlableButNotVisibleRoot,
            orphanedChildrenAfterSuppression);

        EmitVisibleRootGuardrailTelemetry(
            telemetry,
            startUrl,
            "applied",
            "",
            rootClassification.AcceptedUrls.Count,
            rootClassification.RejectedUrls.Count,
            acceptedPrimaryRootOverlap,
            visibleRootChildrenBefore,
            visibleRootChildrenAfter,
            crawlableButNotVisibleRoot,
            orphanedChildrenAfterSuppression);

        return ApplyRootNavigationPurityFilter(
            visible,
            allFlat,
            startUrl,
            rootClassification,
            protectedPrimaryRootKeys,
            log,
            telemetry);
    }

    private static List<NavItem> ApplyRootNavigationPurityFilter(
        List<NavItem> visibleTreeFlat,
        List<NavItem> allFlat,
        string startUrl,
        RootCandidateClassification rootClassification,
        HashSet<string> protectedPrimaryRootKeys,
        Logger? log,
        TelemetryWriter? telemetry)
    {
        if (visibleTreeFlat.Count == 0)
            return visibleTreeFlat;

        var canonicalStart = CanonicalUrlKey(startUrl);
        var acceptedHomepageKeys = rootClassification.AcceptedUrls
            .Select(CanonicalUrlKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var childrenByParent = visibleTreeFlat
            .Where(x => !string.IsNullOrWhiteSpace(x.ParentUrl))
            .GroupBy(x => CanonicalUrlKey(x.ParentUrl), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => CanonicalUrlKey(x.Url)).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        var rootItems = visibleTreeFlat
            .Where(x => !CanonicalUrlKey(x.Url).Equals(canonicalStart, StringComparison.OrdinalIgnoreCase)
                     && CanonicalUrlKey(x.ParentUrl).Equals(canonicalStart, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var beforeRootCount = rootItems.Count;
        if (beforeRootCount == 0)
            return visibleTreeFlat;

        var accepted = 0;
        var demoted = 0;
        var warningClassifications = new HashSet<MunicipalRootClassificationKind>
        {
            MunicipalRootClassificationKind.MicrositeRoot,
            MunicipalRootClassificationKind.NewsOrEventRoot,
            MunicipalRootClassificationKind.ErrorOrSystemRoot,
            MunicipalRootClassificationKind.UtilityRoot
        };

        foreach (var item in rootItems)
        {
            var itemKey = CanonicalUrlKey(item.Url);
            var wasPrimary = protectedPrimaryRootKeys.Contains(itemKey);
            var hadChildren = childrenByParent.TryGetValue(itemKey, out var kids) && kids.Count > 0;
            var decision = MunicipalRootClassifier.Classify(item, new MunicipalRootContext
            {
                StartUrl = startUrl,
                WasPrimaryNav = wasPrimary,
                WasAcceptedHomepageAnchor = acceptedHomepageKeys.Contains(itemKey),
                HadChildren = hadChildren,
                SourceGroup = "visible_tree_root",
                BeforeRootCount = beforeRootCount
            });

            item.MunicipalRootClassification = decision.KindName;
            if (decision.Kind == MunicipalRootClassificationKind.UtilityRoot)
                item.IsUtility = true;

            log?.Event("MUNICIPAL_ROOT_CLASSIFIED",
                ("title", item.Title),
                ("url", item.Url),
                ("classification", decision.KindName),
                ("confidence", decision.Confidence.ToString("0.00")),
                ("reasons", string.Join("|", decision.Reasons)),
                ("evidence", string.Join("|", decision.EvidenceSignals)),
                ("sourceSignals", string.Join("|", decision.SourceSignals)),
                ("wasPrimaryNav", wasPrimary),
                ("hadChildren", hadChildren),
                ("sourceGroup", "visible_tree_root"),
                ("beforeRootCount", beforeRootCount),
                ("afterRootCount", beforeRootCount - demoted));

            telemetry?.Emit(TelemetryPhase.TreeBuilding, "MUNICIPAL_ROOT_CLASSIFIED", TelemetrySeverity.Info,
                item.Url, new Dictionary<string, object?>
                {
                    ["title"] = item.Title,
                    ["classification"] = decision.KindName,
                    ["confidence"] = decision.Confidence,
                    ["reasons"] = decision.Reasons,
                    ["evidenceSignals"] = decision.EvidenceSignals,
                    ["sourceSignals"] = decision.SourceSignals,
                    ["wasPrimaryNav"] = wasPrimary,
                    ["hadChildren"] = hadChildren,
                    ["sourceGroup"] = "visible_tree_root",
                    ["beforeRootCount"] = beforeRootCount,
                    ["afterRootCount"] = beforeRootCount - demoted
                });

            if (decision.IsEligible)
            {
                accepted++;
                log?.Event("MUNICIPAL_ROOT_ACCEPTED",
                    ("title", item.Title),
                    ("url", item.Url),
                    ("classification", decision.KindName),
                    ("confidence", decision.Confidence.ToString("0.00")),
                    ("reasons", string.Join("|", decision.Reasons)),
                    ("wasPrimaryNav", wasPrimary),
                    ("hadChildren", hadChildren),
                    ("sourceGroup", "visible_tree_root"),
                    ("beforeRootCount", beforeRootCount),
                    ("afterRootCount", beforeRootCount - demoted));

                telemetry?.Emit(TelemetryPhase.TreeBuilding, "MUNICIPAL_ROOT_ACCEPTED", TelemetrySeverity.Info,
                    item.Url, new Dictionary<string, object?>
                    {
                        ["title"] = item.Title,
                        ["classification"] = decision.KindName,
                        ["confidence"] = decision.Confidence,
                        ["reasons"] = decision.Reasons,
                        ["wasPrimaryNav"] = wasPrimary,
                        ["hadChildren"] = hadChildren,
                        ["sourceGroup"] = "visible_tree_root",
                        ["beforeRootCount"] = beforeRootCount,
                        ["afterRootCount"] = beforeRootCount - demoted
                    });
            }
            else
            {
                demoted++;
                log?.Event("MUNICIPAL_ROOT_DEMOTED",
                    ("title", item.Title),
                    ("url", item.Url),
                    ("classification", decision.KindName),
                    ("confidence", decision.Confidence.ToString("0.00")),
                    ("reasons", string.Join("|", decision.Reasons)),
                    ("evidence", string.Join("|", decision.EvidenceSignals)),
                    ("wasPrimaryNav", wasPrimary),
                    ("hadChildren", hadChildren),
                    ("sourceGroup", "visible_tree_root"),
                    ("beforeRootCount", beforeRootCount),
                    ("afterRootCount", beforeRootCount - demoted));

                telemetry?.Emit(TelemetryPhase.TreeBuilding, "MUNICIPAL_ROOT_DEMOTED", TelemetrySeverity.Warning,
                    item.Url, new Dictionary<string, object?>
                    {
                        ["title"] = item.Title,
                        ["classification"] = decision.KindName,
                        ["confidence"] = decision.Confidence,
                        ["reasons"] = decision.Reasons,
                        ["evidenceSignals"] = decision.EvidenceSignals,
                        ["wasPrimaryNav"] = wasPrimary,
                        ["hadChildren"] = hadChildren,
                        ["sourceGroup"] = "visible_tree_root",
                        ["beforeRootCount"] = beforeRootCount,
                        ["afterRootCount"] = beforeRootCount - demoted
                    });

                if (warningClassifications.Contains(decision.Kind))
                    log?.Warn($"MUNICIPAL_ROOT_WARNING classified={decision.KindName} title=\"{item.Title}\" url={item.Url}");
            }
        }

        var afterRootCountForPolicy = beforeRootCount - demoted;
        if (beforeRootCount >= 6 && demoted > beforeRootCount / 2)
            log?.Warn($"MUNICIPAL_ROOT_WARNING excessiveRootsDemoted={demoted} beforeRootCount={beforeRootCount} afterRootCount={afterRootCountForPolicy}");

        if (afterRootCountForPolicy < 3 && beforeRootCount >= 5)
            log?.Warn($"MUNICIPAL_ROOT_WARNING sparseNavigation beforeRootCount={beforeRootCount} afterRootCount={afterRootCountForPolicy}");

        log?.Event("MUNICIPAL_ROOT_POLICY_SUMMARY",
            ("beforeRootCount", beforeRootCount),
            ("afterRootCount", afterRootCountForPolicy),
            ("acceptedCount", accepted),
            ("demotedCount", demoted));

        telemetry?.Emit(TelemetryPhase.TreeBuilding, "MUNICIPAL_ROOT_POLICY_SUMMARY", TelemetrySeverity.Info,
            startUrl, new Dictionary<string, object?>
            {
                ["beforeRootCount"] = beforeRootCount,
                ["afterRootCount"] = afterRootCountForPolicy,
                ["acceptedCount"] = accepted,
                ["demotedCount"] = demoted
            });

        return visibleTreeFlat;

    }

    private static bool IsTopLevelSiteVisionNumericPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var path = NormalizePath(u.AbsolutePath);
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 1) return false;
        var segment = segments[0];
        if (!segment.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return false;
        var dot = segment.LastIndexOf('.');
        if (dot <= 0) return false;
        var stem = segment[..dot];
        var stemDot = stem.LastIndexOf('.');
        if (stemDot <= 0) return false;
        var suffix = stem[(stemDot + 1)..];
        return suffix.Length > 0 && suffix.All(char.IsDigit);
    }

    private static bool IsProtectedPrimaryRootCandidate(string url, string title, string startUrl)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return MunicipalRootClassifier.IsEligibleMunicipalRoot(
            url,
            title,
            startUrl,
            wasPrimaryNav: true,
            hadChildren: true,
            sourceGroup: "primary_nav_candidate");
    }

    private static bool IsStartPageAlias(string url, string startUrl)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (!Uri.TryCreate(startUrl, UriKind.Absolute, out var s)) return false;
        if (!u.Host.Equals(s.Host, StringComparison.OrdinalIgnoreCase)) return false;

        var path = NormalizePath(u.AbsolutePath).Trim('/');
        if (string.IsNullOrWhiteSpace(path)) return false;
        return path.Equals("index.html", StringComparison.OrdinalIgnoreCase)
            || path.Equals("index.htm", StringComparison.OrdinalIgnoreCase)
            || path.Equals("startsida", StringComparison.OrdinalIgnoreCase)
            || path.Equals("start", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootContentArchiveOrServiceSection(string url, string title)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var path = NormalizePath(u.AbsolutePath).Trim('/').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(path)) return false;

        var titleLower = (title ?? "").ToLowerInvariant();
        var pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var contentSectionTokens = new[]
        {
            "arkiv", "nyhet", "nyheter", "huvudnyheter", "evenemang",
            "kalender", "aktuellt", "servicemeddelanden", "drift",
            "driftinformation", "press", "kampanj"
        };

        if (pathSegments.Length <= 2
            && contentSectionTokens.Any(t => path.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return true;

        var contentTitleTokens = new[]
        {
            "nyheter", "huvudnyheter", "evenemang", "aktuellt",
            "servicemeddelanden", "driftinformation"
        };
        return pathSegments.Length <= 2
            && contentTitleTokens.Any(t => titleLower.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsArticleLikeRootTitle(string title)
    {
        var t = (title ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return false;
        if (t.Length >= 55) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"\b20\d{2}\b")) return true;

        var promoTokens = new[]
        {
            "sommarlov", "lov i ", "aktivitet", "aktiviteter", "firande",
            "pris", "prisas", "bidrag", "utryckning", "markarbeten",
            "underhÃ¥ll", "underhall", "schema", "aktuellt just nu"
        };
        return promoTokens.Any(x => t.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLikelyMunicipalRootTitleOrPath(string title, string url)
    {
        var text = ((title ?? "") + " " + (url ?? "")).ToLowerInvariant();
        var sectionTokens = new[]
        {
            "barn", "utbildning", "skola", "omsorg", "hjÃ¤lp", "hjalp",
            "uppleva", "gÃ¶ra", "gora", "bygga", "bo", "miljÃ¶", "miljo",
            "trafik", "resor", "jobb", "fÃ¶retag", "foretag",
            "nÃ¤ringsliv", "naringsliv", "kommun", "politik", "arbete",
            "infrastruktur"
        };

        return sectionTokens.Count(x => text.Contains(x, StringComparison.OrdinalIgnoreCase)) >= 2;
    }

    private static void EmitVisibleRootGuardrailTelemetry(
        TelemetryWriter? telemetry,
        string startUrl,
        string decision,
        string reason,
        int rootCandidatesAccepted,
        int rootCandidatesRejected,
        int acceptedPrimaryRootOverlap,
        int visibleRootChildrenBefore,
        int visibleRootChildrenAfter,
        int crawlableButNotVisibleRoot,
        int orphanedChildrenAfterSuppression)
    {
        var severity = decision.Equals("skipped", StringComparison.OrdinalIgnoreCase)
            ? TelemetrySeverity.Warning
            : TelemetrySeverity.Info;

        telemetry?.Emit(TelemetryPhase.TreeBuilding, "visible_root_suppression_guardrail", severity,
            startUrl, new Dictionary<string, object?>
            {
                ["decision"] = decision,
                ["reason"] = reason,
                ["rootCandidatesAccepted"] = rootCandidatesAccepted,
                ["rootCandidatesRejected"] = rootCandidatesRejected,
                ["acceptedPrimaryRootOverlap"] = acceptedPrimaryRootOverlap,
                ["visibleRootChildrenBefore"] = visibleRootChildrenBefore,
                ["visibleRootChildrenAfter"] = visibleRootChildrenAfter,
                ["crawlableButNotVisibleRoot"] = crawlableButNotVisibleRoot,
                ["orphanedChildrenAfterSuppression"] = orphanedChildrenAfterSuppression
            });
    }

    private static void EmitVisibleRootTelemetry(
        TelemetryWriter? telemetry,
        string startUrl,
        int rootCandidatesAccepted,
        int rootCandidatesRejected,
        int visibleRootChildrenBefore,
        int visibleRootChildrenAfter,
        int rejectedCandidatesSuppressedFromVisibleRoot,
        int crawlableButNotVisibleRoot,
        int orphanedChildrenAfterSuppression)
    {
        telemetry?.Emit(TelemetryPhase.TreeBuilding, "visible_root_classification_applied", TelemetrySeverity.Info,
            startUrl, new Dictionary<string, object?>
            {
                ["rootCandidatesAccepted"] = rootCandidatesAccepted,
                ["rootCandidatesRejected"] = rootCandidatesRejected,
                ["visibleRootChildrenBefore"] = visibleRootChildrenBefore,
                ["visibleRootChildrenAfter"] = visibleRootChildrenAfter,
                ["rejectedCandidatesSuppressedFromVisibleRoot"] = rejectedCandidatesSuppressedFromVisibleRoot,
                ["crawlableButNotVisibleRoot"] = crawlableButNotVisibleRoot,
                ["orphanedChildrenAfterSuppression"] = orphanedChildrenAfterSuppression
            });
    }

    private static int CountAllFlatRootChildrenNotInTree(List<NavItem> allFlat, List<NavItem> treeFlat, string startUrl)
    {
        var treeRootKeys = treeFlat
            .Where(x => CanonicalUrlKey(x.ParentUrl).Equals(CanonicalUrlKey(startUrl), StringComparison.OrdinalIgnoreCase))
            .Select(x => CanonicalUrlKey(x.Url))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return allFlat
            .Where(x => !CanonicalUrlKey(x.Url).Equals(CanonicalUrlKey(startUrl), StringComparison.OrdinalIgnoreCase)
                     && CanonicalUrlKey(x.ParentUrl).Equals(CanonicalUrlKey(startUrl), StringComparison.OrdinalIgnoreCase))
            .Select(x => CanonicalUrlKey(x.Url))
            .Where(k => !treeRootKeys.Contains(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static string? FindAcceptedAncestorUrl(string url, HashSet<string> acceptedRootUrls, string startUrl)
    {
        foreach (var accepted in acceptedRootUrls)
        {
            if (accepted.Equals(url, StringComparison.OrdinalIgnoreCase))
                continue;

            if (accepted.Equals(startUrl, StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsUrlDescendantOf(accepted, url))
                return accepted;
        }

        return null;
    }

    private static NavItem ChooseBetterNavItem(NavItem a, NavItem b, string canonicalStart)
    {
        var scoreA = NavItemScore(a, canonicalStart);
        var scoreB = NavItemScore(b, canonicalStart);

        var bWins = scoreB > scoreA;
        var winner = CloneWithCanonicalUrls(bWins ? b : a);
        var other = bWins ? a : b;

        if (TitleScore(other) > TitleScore(winner))
            winner.Title = other.Title;

        winner.IsExternal = a.IsExternal || b.IsExternal;
        winner.IsJsDynamic = a.IsJsDynamic || b.IsJsDynamic;
        winner.IsDisplayOnly = a.IsDisplayOnly && b.IsDisplayOnly;
        winner.IsSynthetic = a.IsSynthetic || b.IsSynthetic;
        return winner;
    }

    private static int NavItemScore(NavItem item, string canonicalStart)
    {
        var urlKey = CanonicalUrlKey(item.Url);
        var parentKey = CanonicalUrlKey(item.ParentUrl);
        var isRoot = urlKey.Equals(canonicalStart, StringComparison.OrdinalIgnoreCase);
        var hasParent = !string.IsNullOrWhiteSpace(parentKey)
            && !parentKey.Equals(urlKey, StringComparison.OrdinalIgnoreCase)
            && !IsUrlDescendantOf(item.Url, item.ParentUrl);

        var score = 0;
        if (isRoot) score += 10_000;
        if (hasParent) score += 1_000;
        if (hasParent && !parentKey.Equals(canonicalStart, StringComparison.OrdinalIgnoreCase)) score += 500;
        if (!isRoot && item.Depth > 0) score += 200;
        if (!isRoot && item.Depth == 0) score -= 1_000;
        score += CountUrlPathSegments(item.ParentUrl) * 20;
        score += Math.Min(100, TitleScore(item));
        return score;
    }

    private static int TitleScore(NavItem item)
    {
        var title = (item.Title ?? "").Trim();
        if (title.Length == 0) return 0;
        if (title.Equals(item.Url, StringComparison.OrdinalIgnoreCase)) return 1;
        if (title.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return 1;
        if (title.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return 1;
        return Math.Min(title.Length, 200);
    }

    private static bool IsRawUrlLikeTitle(string? title, string? url)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) return true;
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("/", StringComparison.Ordinal)) return true;
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            var host = u.Host.Trim();
            if (t.Equals(host, StringComparison.OrdinalIgnoreCase)) return true;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                && t.Equals(host[4..], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return !string.IsNullOrWhiteSpace(url) && t.Equals(url, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDisplayTitleFallback(string? title, string? url)
    {
        if (!IsRawUrlLikeTitle(title, url))
            return (title ?? "").Trim();

        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            var segments = u.AbsolutePath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0)
                return SlugToTitle(segments[^1]);
            return Utils.HostToMunicipality(u.Host);
        }

        return (url ?? title ?? "").Trim();
    }

    private static List<NavItem> RecalculateDepths(
        List<NavItem> flat,
        string startUrl,
        Logger? log)
    {
        var canonicalStart = CanonicalUrlKey(startUrl);
        var byKey = new Dictionary<string, NavItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in flat)
        {
            var key = CanonicalUrlKey(item.Url);
            if (!string.IsNullOrWhiteSpace(key) && !byKey.ContainsKey(key))
                byKey[key] = item;
        }

        var depthByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cyclesBroken = 0;

        int DepthFor(string key, HashSet<string> path)
        {
            if (depthByKey.TryGetValue(key, out var cached))
                return cached;

            if (!byKey.TryGetValue(key, out var item))
                return 0;

            if (key.Equals(canonicalStart, StringComparison.OrdinalIgnoreCase))
            {
                item.ParentUrl = "";
                return depthByKey[key] = 0;
            }

            if (!path.Add(key))
            {
                item.ParentUrl = byKey.TryGetValue(canonicalStart, out var startItem)
                    ? startItem.Url
                    : startUrl;
                cyclesBroken++;
                log?.Event("DEPTH_RECALC_CYCLE_BROKEN", ("url", item.Url));
                return depthByKey[key] = 1;
            }

            var parentKey = CanonicalUrlKey(item.ParentUrl);
            if (string.IsNullOrWhiteSpace(parentKey)
                || parentKey.Equals(key, StringComparison.OrdinalIgnoreCase)
                || !byKey.ContainsKey(parentKey))
            {
                parentKey = FindNearestKnownAncestorKey(item.Url, byKey.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), canonicalStart)
                    ?? canonicalStart;

                item.ParentUrl = byKey.TryGetValue(parentKey, out var inferredParent)
                    ? inferredParent.Url
                    : startUrl;
            }

            var depth = 1 + DepthFor(parentKey, path);
            path.Remove(key);
            return depthByKey[key] = depth;
        }

        var changed = 0;
        foreach (var item in flat)
        {
            var key = CanonicalUrlKey(item.Url);
            var oldDepth = item.Depth;
            item.Depth = DepthFor(key, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (item.Depth != oldDepth) changed++;
        }

        log?.Event("DEPTH_RECALCULATED",
            ("items", flat.Count),
            ("changed", changed),
            ("cyclesBroken", cyclesBroken),
            ("maxDepth", flat.Count > 0 ? flat.Max(x => x.Depth) : 0));

        return flat;
    }

    private static NavItem CloneWithCanonicalUrls(NavItem item)
    {
        return new NavItem
        {
            Url = CanonicalDisplayUrl(item.Url),
            Title = item.Title ?? "",
            Depth = item.Depth,
            ParentUrl = string.IsNullOrWhiteSpace(item.ParentUrl) ? "" : CanonicalDisplayUrl(item.ParentUrl),
            IsExternal = item.IsExternal,
            IsJsDynamic = item.IsJsDynamic,
            IsDisplayOnly = item.IsDisplayOnly,
            IsSynthetic = item.IsSynthetic,
            IsUtility = item.IsUtility
        };
    }

    private static string? FindNearestKnownAncestorKey(
        string url,
        HashSet<string> knownKeys,
        string canonicalStart)
    {
        if (!Uri.TryCreate(CanonicalDisplayUrl(url), UriKind.Absolute, out var u))
            return null;

        var segments = u.AbsolutePath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        for (int i = segments.Length - 1; i >= 1; i--)
        {
            var ancestorPath = "/" + string.Join("/", segments, 0, i);
            var ancestor = CanonicalUrlKey($"{u.Scheme}://{u.Host}{ancestorPath}");
            if (ancestor.Equals(canonicalStart, StringComparison.OrdinalIgnoreCase))
                return null;
            if (knownKeys.Contains(ancestor))
                return ancestor;
        }

        return null;
    }

    private static string CanonicalUrlKey(string? url)
        => CanonicalDisplayUrl(url).ToLowerInvariant();

    private static string CanonicalDisplayUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";

        try
        {
            var u = new Uri(url.Trim(), UriKind.Absolute);
            var builder = new UriBuilder(u)
            {
                Scheme = u.Scheme.ToLowerInvariant(),
                Host = u.Host.ToLowerInvariant(),
                Query = "",
                Fragment = ""
            };

            if ((builder.Scheme == "http" && builder.Port == 80)
                || (builder.Scheme == "https" && builder.Port == 443))
                builder.Port = -1;

            var result = builder.Uri.GetLeftPart(UriPartial.Path);
            return result.TrimEnd('/');
        }
        catch
        {
            return (url ?? "").Trim().TrimEnd('/');
        }
    }

    /// <summary>
    /// Walks up the URL path one segment at a time (most-specific first) and returns
    /// the URL of the first ancestor found in <paramref name="knownUrls"/>.
    /// Returns null when no known ancestor exists other than the site root itself.
    /// </summary>
    private static string? FindNearestKnownAncestorUrl(
        string url,
        Dictionary<string, NavItem> knownUrls,
        string startUrl)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;

        var segments = u.AbsolutePath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Need ≥ 2 segments for there to be a non-root parent path.
        if (segments.Length < 2) return null;

        for (int i = segments.Length - 1; i >= 1; i--)
        {
            var ancestorPath = "/" + string.Join("/", segments, 0, i);
            var ancestorUrl  = $"{u.Scheme}://{u.Host}{ancestorPath}";

            if (ancestorUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase))
                return null;

            if (knownUrls.ContainsKey(ancestorUrl))
                return ancestorUrl;
        }

        return null;
    }

    private static int CountUrlPathSegments(string url)
    {
        try
        {
            return new Uri(url).AbsolutePath
                .TrimEnd('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string GetSectionPrefix(string path)
    {
        path = NormalizePath(path);

        if (path == "/")
            return "/";

        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            path = path[..^5];

        // SiteVision often appends numeric page IDs before .html (e.g. /barnochutbildning.2142.html).
        // Child URLs use the clean section path (/barnochutbildning/...), so strip the numeric suffix
        // so that parent-child prefix containment checks work correctly.
        path = CanonicalizeSectionPrefix(path);

        return path;
    }

    // Strips a trailing SiteVision numeric page-ID segment of the form .\d+ from a path.
    // Example: /barnochutbildning.2142 => /barnochutbildning
    private static string CanonicalizeSectionPrefix(string path)
    {
        int dot = path.LastIndexOf('.');
        if (dot > 0)
        {
            var suffix = path[(dot + 1)..];
            if (suffix.Length > 0 && suffix.All(char.IsDigit))
                path = path[..dot];
        }
        return path;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        path = path.Trim();

        if (!path.StartsWith("/", StringComparison.Ordinal))
            path = "/" + path;

        while (path.Contains("//", StringComparison.Ordinal))
            path = path.Replace("//", "/", StringComparison.Ordinal);

        if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
            path = path[..^1];

        return path;
    }

    private static bool IsNoisePath(string path)
    {
        var p = path.TrimEnd('/');

        // SiteVision / Swedish municipality crawl amplifiers
        if (p.Equals("/webbkarta", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("/webbkarta/", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.EndsWith("/rss", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Contains("/nyhetsarkiv/", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Contains("/driftinformation", StringComparison.OrdinalIgnoreCase)) return true;

        // WordPress admin / system paths
        if (p.Equals("/wp-admin", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("/wp-admin/", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Equals("/wp-login.php", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("/wp-json/", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static bool SameSiteHost(string a, string b)
    {
        static string Norm(string h)
        {
            h ??= "";
            h = h.Trim().ToLowerInvariant();
            if (h.StartsWith("www.", StringComparison.Ordinal))
                h = h[4..];
            return h;
        }

        return Norm(a) == Norm(b);
    }

    // ── Phase 1: Homepage Structural Section Extraction ─────────────────────
    //
    // Detects municipality-authored IA anchors from homepage card/tile clusters.
    // A "card cluster" is a group of ≥ 4 same-host single-segment-path links
    // under the same parent DOM element in the main content area (not in
    // header/footer/nav).  These clusters represent the site's intended root
    // taxonomy (e.g. "Omsorg och stöd", "Förskola, skola och utbildning").
    private async Task<RootCandidateClassification> ExtractHomepageSectionsAsync(
        IPage page,
        string startUrl,
        string host,
        bool dropQueryStrings,
        Logger? log)
    {
        try
        {
            var raw = await page.EvaluateAsync<HomepageSectionJs[]>(@"(siteHost) => {
                const normHost = h => (h || '').toLowerCase().replace(/^www\./, '');
                const targetHost = normHost(siteHost);
                const util = /login|logga-in|admin|sok|search|kontakt|tillganglighet|social|facebook|instagram|e-tjanst|e-service/i;
                const article = /nyhet|nyheter|huvudnyheter|servicemeddelanden|evenemang|kalender|press|artikel|arkiv|aktuellt|blogg|20\d\d/i;

                // Collect links that live inside structural chrome (header/footer/nav) so we
                // can exclude them.  We want only links from the *main content* area.
                const excluded = new Set();
                document.querySelectorAll('header, footer').forEach(el => {
                    el.querySelectorAll('a').forEach(a => excluded.add(a));
                });
                document.querySelectorAll('nav, [role=navigation]').forEach(el => {
                    el.querySelectorAll('a').forEach(a => excluded.add(a));
                });

                // Search inside <main> / #content / article / body (in preference order).
                const mainEl =
                    document.querySelector('main') ||
                    document.querySelector('[role=main]') ||
                    document.querySelector('#content, #main-content, .main-content, article') ||
                    document.body;

                const seen = new Set();
                const candidates = [];
                let globalOrder = 0;
                const parentCounts = new Map();
                const anchors = Array.from(mainEl.querySelectorAll('a[href]'));
                for (const a of anchors) {
                    const parent = a.closest('section, ul, ol, div, main, article') || a.parentElement || mainEl;
                    const key = parent ? (parent.tagName + ':' + Array.from(parent.parentElement ? parent.parentElement.children : []).indexOf(parent)) : 'main';
                    parentCounts.set(key, (parentCounts.get(key) || 0) + 1);
                    a.__wsParentKey = key;
                }

                for (const a of anchors) {
                    try {
                        const url = new URL(a.href, location.href);
                        const segs = url.pathname.replace(/\/$/, '').split('/').filter(Boolean);
                        const text = (a.textContent || '').trim().replace(/\s+/g, ' ');
                        const rect = a.getBoundingClientRect();
                        const container = a.closest('article, li, .card, .tile, [class*=card], [class*=tile], [class*=grid], [class*=puff], [class*=box]');
                        const parentKey = a.__wsParentKey || '';
                        const normalized = url.protocol + '//' + url.host + (url.pathname.replace(/\/$/, '') || '/');

                        if (!seen.has(normalized)) {
                            seen.add(normalized);
                            candidates.push({
                                url: normalized,
                                title: text,
                                domOrder: globalOrder++,
                                sameHost: normHost(url.host) === targetHost,
                                inMain: true,
                                inExcludedChrome: excluded.has(a),
                                pathSegments: segs.length,
                                hasVisibleTitle: text.length >= 3 && text.length <= 120,
                                cardLike: !!container,
                                siblingCount: parentCounts.get(parentKey) || 0,
                                utilityLike: util.test(url.pathname) || util.test(text),
                                articleLike: article.test(url.pathname),
                                binaryLike: /\.(pdf|docx?|xlsx?|pptx?|zip|jpg|jpeg|png|gif|webp|svg)$/i.test(url.pathname),
                                tinyOrHidden: rect.width < 24 || rect.height < 12 || getComputedStyle(a).visibility === 'hidden' || getComputedStyle(a).display === 'none'
                            });
                        }
                    } catch (_) {}
                }

                return candidates.sort((a, b) => a.domOrder - b.domOrder);
            }", host);

            var classification = new RootCandidateClassification();
            if (raw == null || raw.Length == 0)
                return classification;

            var accepted = 0;
            var rejected = 0;
            classification.CandidateCount = raw.Length;
            foreach (var r in raw)
            {
                var url = NormalizeInternalUrl(r.url, startUrl, host, dropQueryStrings);
                if (string.IsNullOrWhiteSpace(url))
                {
                    rejected++;
                    continue;
                }

                var decision = ScoreHomepageAnchorCandidate(r, url);
                EmitDecision(log, _telemetry, TelemetryPhase.NavStartExtraction, "HOMEPAGE_ANCHOR_CANDIDATE", decision);
                if (!decision.Accepted)
                {
                    rejected++;
                    classification.RejectedUrls.Add(url);
                    EmitDecision(log, _telemetry, TelemetryPhase.NavStartExtraction, "HOMEPAGE_ANCHOR_REJECTED", decision);
                    continue;
                }

                accepted++;
                classification.AcceptedUrls.Add(url);
                classification.Accepted.Add(new HomepageSection
                {
                    Url = url,
                    Title = (r.title ?? "").Trim(),
                    DomOrder = r.domOrder,
                    Confidence = decision.Confidence,
                    PositiveEvidence = decision.PositiveEvidence,
                    NegativeEvidence = decision.NegativeEvidence
                });
                EmitDecision(log, _telemetry, TelemetryPhase.NavStartExtraction, "HOMEPAGE_ANCHOR_ACCEPTED", decision);
            }

            log?.Event("HOMEPAGE_SECTIONS_EXTRACTED",
                ("host", host),
                ("count", classification.Accepted.Count),
                ("sample", string.Join(", ", classification.Accepted.Take(5).Select(s => $"'{s.Title}'"))));

            log?.Event("HOMEPAGE_ANCHOR_GROUP_SUMMARY",
                ("host", host),
                ("candidates", raw.Length),
                ("accepted", accepted),
                ("rejected", rejected),
                ("threshold", "0.68"));

            _telemetry?.Emit(TelemetryPhase.NavStartExtraction, "HOMEPAGE_ANCHOR_GROUP_SUMMARY",
                TelemetrySeverity.Info, startUrl, new Dictionary<string, object?>
                {
                    ["candidates"] = raw.Length,
                    ["accepted"] = accepted,
                    ["rejected"] = rejected,
                    ["threshold"] = 0.68
                });

            return classification;
        }
        catch (Exception ex)
        {
            log?.Warn($"HOMEPAGE_SECTIONS_WARN host={host} err={ex.Message}");
            return new RootCandidateClassification();
        }
    }

    // Playwright JS deserialization target — property names must match JS camelCase exactly.
    private sealed class HomepageSectionJs
    {
        public string url { get; set; } = "";
        public string title { get; set; } = "";
        public int domOrder { get; set; }
        public bool sameHost { get; set; }
        public bool inMain { get; set; }
        public bool inExcludedChrome { get; set; }
        public int pathSegments { get; set; }
        public bool hasVisibleTitle { get; set; }
        public bool cardLike { get; set; }
        public int siblingCount { get; set; }
        public bool utilityLike { get; set; }
        public bool articleLike { get; set; }
        public bool binaryLike { get; set; }
        public bool tinyOrHidden { get; set; }
    }

    // ── Phase 4: Utility / Meta Page Tagging ────────────────────────────────
    //
    // Tags pages whose URL path or title matches utility/meta heuristics with
    // NavItem.IsUtility = true.  The viewer uses this flag to visually separate
    // these pages from the municipality's primary IA sections.
    private static NavigationDecisionEvidence ScoreHomepageAnchorCandidate(HomepageSectionJs r, string normalizedUrl)
    {
        const double threshold = 0.68;
        var d = new NavigationDecisionEvidence
        {
            DecisionType = "homepage_structural_anchor",
            CandidateUrl = normalizedUrl,
            CandidateTitle = (r.title ?? "").Trim(),
            Threshold = threshold
        };

        var score = 0.0;
        AddEvidence(d, r.sameHost, "internal_same_host_url", "external_url", 0.18, -0.55, ref score);
        AddEvidence(d, r.inMain && !r.inExcludedChrome, "main_content", "footer_header_nav_utility", 0.16, -0.28, ref score);
        AddEvidence(d, r.cardLike, "card_tile_grid_like_link", "", 0.14, 0, ref score);
        AddEvidence(d, r.pathSegments == 1, "short_section_like_path", r.pathSegments > 2 ? "deep_detail_path" : "", 0.16, -0.14, ref score);
        AddEvidence(d, r.hasVisibleTitle, "visible_title_text", "missing_or_generic_title_text", 0.16, -0.18, ref score);
        AddEvidence(d, r.siblingCount >= 3, "sibling_card_pattern", "", 0.14, 0, ref score);
        AddEvidence(d, !r.utilityLike, "not_utility_link", "login_admin_search_social_eservice", 0.08, -0.34, ref score);
        AddEvidence(d, !r.articleLike, "not_article_event_news_detail_path", "dated_article_event_news_detail_path", 0.08, -0.26, ref score);
        AddEvidence(d, !r.binaryLike, "not_document_binary", "document_binary", 0.05, -0.45, ref score);
        AddEvidence(d, !r.tinyOrHidden, "not_hidden_tiny_link", "hidden_tiny_link", 0.05, -0.24, ref score);

        d.Confidence = Clamp01(score);
        if (r.pathSegments != 1 || r.articleLike)
            d.Confidence = Math.Min(d.Confidence, 0.62);
        d.Accepted = d.Confidence >= d.Threshold;
        d.FinalReason = d.Accepted ? "accepted:confidence_at_or_above_threshold" : "rejected:confidence_below_threshold";
        return d;
    }

    private static NavigationDecisionEvidence ScoreSyntheticParentCandidate(
        string prefixUrl,
        string slug,
        List<NavItem> children,
        HashSet<string> knownUrls,
        List<NavItem> allFlat,
        string startUrl,
        string syntheticParentUrl,
        int deepRootChildBefore)
    {
        const double threshold = 0.72;
        var d = new NavigationDecisionEvidence
        {
            DecisionType = "synthetic_url_prefix_parent",
            CandidateUrl = prefixUrl,
            CandidateTitle = SlugToTitle(slug),
            Threshold = threshold
        };

        var score = 0.0;
        AddEvidence(d, !knownUrls.Contains(prefixUrl), "missing_prefix_not_already_known", "prefix_already_known", 0.12, -0.7, ref score);
        AddEvidence(d, children.Count >= 2, "multiple_children_share_missing_prefix", "only_one_child", 0.28, -0.35, ref score);
        AddEvidence(d, allFlat.Any(x => string.Equals(x.ParentUrl, prefixUrl, StringComparison.OrdinalIgnoreCase)), "missing_prefix_referenced_as_parent_url", "", 0.2, 0, ref score);
        AddEvidence(d, !IsDatedOrArticleLikeUrl(prefixUrl), "prefix_stable_not_dated_article_like", "dated_article_like_url", 0.18, -0.45, ref score);
        AddEvidence(d, Uri.TryCreate(prefixUrl, UriKind.Absolute, out var prefixUri) && !Utils.IsProbablyBinaryAsset(prefixUri.AbsolutePath), "not_document_binary", "document_binary", 0.12, -0.45, ref score);
        AddEvidence(d, !IsUtilityPage(prefixUrl, slug), "not_admin_search_login_path", "admin_search_login_path", 0.1, -0.35, ref score);
        AddEvidence(d, !IsUrlDescendantOf(prefixUrl, syntheticParentUrl), "does_not_create_cycle", "creates_cycle", 0.18, -0.8, ref score);
        AddEvidence(d, CountUrlPathSegments(prefixUrl) <= 4, "not_too_deep_or_narrow", "too_deep_narrow", 0.08, -0.2, ref score);
        AddEvidence(d, !syntheticParentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase), "nearest_known_ancestor_exists", "falls_back_to_root_ancestor", 0.08, 0, ref score);
        AddEvidence(d, deepRootChildBefore > 0, "reduces_root_pollution", "", 0.1, 0, ref score);

        if (IsNumericOrDateSlug(slug) || slug.Contains('?') || slug.Contains('#') || slug.Contains('='))
        {
            d.NegativeEvidence.Add("unstable_or_invalid_slug");
            score -= 0.55;
        }

        d.Confidence = Clamp01(score);
        d.Accepted = d.Confidence >= d.Threshold;
        d.FinalReason = d.Accepted ? "accepted:strong_prefix_parent_evidence" : "rejected:insufficient_prefix_parent_evidence";
        return d;
    }

    private static NavigationDecisionEvidence ScoreUrlParentCandidate(NavItem item, NavItem? bestParent, string startUrl, bool structuralParent)
    {
        const double threshold = 0.6;
        var d = new NavigationDecisionEvidence
        {
            DecisionType = "url_parent_inference",
            CandidateUrl = item.Url,
            CandidateTitle = item.Title,
            Threshold = threshold
        };

        var score = 0.0;
        AddEvidence(d, bestParent != null, "known_ancestor_parent_found", "no_known_url_ancestor", 0.35, -0.55, ref score);
        if (bestParent != null)
        {
            AddEvidence(d, IsUrlDescendantOf(bestParent.Url, item.Url), "child_url_descends_from_parent_url", "url_not_descendant", 0.28, -0.5, ref score);
            AddEvidence(d, !bestParent.Url.Equals(startUrl, StringComparison.OrdinalIgnoreCase), "parent_more_specific_than_root", "root_parent_only", 0.12, -0.15, ref score);
            AddEvidence(d, structuralParent, "parent_is_structural_section_candidate", "parent_from_url_lookup_only", 0.14, 0, ref score);
            AddEvidence(d, !IsDatedOrArticleLikeUrl(bestParent.Url), "parent_stable_not_article_like", "parent_article_like", 0.1, -0.25, ref score);
            AddEvidence(d, !IsUrlDescendantOf(item.Url, bestParent.Url), "does_not_create_cycle", "creates_cycle", 0.16, -0.8, ref score);
        }
        AddEvidence(d, CountUrlPathSegments(item.Url) >= 2, "child_has_deep_path", "child_is_top_level", 0.08, -0.12, ref score);
        AddEvidence(d, !IsUtilityPage(item.Url, item.Title), "child_not_utility_path", "child_utility_path", 0.05, -0.18, ref score);

        d.Confidence = Clamp01(score);
        d.Accepted = d.Confidence >= d.Threshold;
        d.FinalReason = d.Accepted ? "accepted:url_path_parent_confident" : "rejected:url_path_parent_low_confidence";
        return d;
    }

    private static void AddEvidence(
        NavigationDecisionEvidence d,
        bool condition,
        string positive,
        string negative,
        double positiveWeight,
        double negativeWeight,
        ref double score)
    {
        if (condition)
        {
            if (!string.IsNullOrWhiteSpace(positive)) d.PositiveEvidence.Add(positive);
            score += positiveWeight;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(negative)) d.NegativeEvidence.Add(negative);
            score += negativeWeight;
        }
    }

    private static void EmitDecision(
        Logger? log,
        TelemetryWriter? telemetry,
        TelemetryPhase phase,
        string eventName,
        NavigationDecisionEvidence d)
    {
        log?.Event(eventName,
            ("decisionType", d.DecisionType),
            ("candidateUrl", d.CandidateUrl),
            ("candidateTitle", d.CandidateTitle),
            ("accepted", d.Accepted),
            ("confidence", d.Confidence.ToString("0.00")),
            ("threshold", d.Threshold.ToString("0.00")),
            ("positiveEvidence", string.Join("|", d.PositiveEvidence)),
            ("negativeEvidence", string.Join("|", d.NegativeEvidence)),
            ("finalReason", d.FinalReason));

        telemetry?.Emit(phase, eventName, d.Accepted ? TelemetrySeverity.Info : TelemetrySeverity.Debug,
            d.CandidateUrl, DecisionFields(d));
    }

    private static Dictionary<string, object?> DecisionFields(
        NavigationDecisionEvidence d,
        Dictionary<string, object?>? extra = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["decisionType"] = d.DecisionType,
            ["candidateUrl"] = d.CandidateUrl,
            ["candidateTitle"] = d.CandidateTitle,
            ["accepted"] = d.Accepted,
            ["confidence"] = d.Confidence,
            ["threshold"] = d.Threshold,
            ["positiveEvidence"] = d.PositiveEvidence,
            ["negativeEvidence"] = d.NegativeEvidence,
            ["finalReason"] = d.FinalReason
        };
        if (extra != null)
            foreach (var kv in extra)
                fields[kv.Key] = kv.Value;
        return fields;
    }

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    private static bool IsDatedOrArticleLikeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return true;
        var path = u.AbsolutePath.ToLowerInvariant();
        if (System.Text.RegularExpressions.Regex.IsMatch(path, @"/20\d{2}([-/]\d{2})?([-/]\d{2})?(/|$)"))
            return true;
        var tokens = new[] { "nyhet", "nyheter", "huvudnyheter", "servicemeddelanden", "evenemang", "kalender", "artikel", "press", "arkiv", "aktuellt", "blogg" };
        return tokens.Any(t => path.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static void TagUtilityPages(List<NavItem> allFlat, Logger? log)
    {
        var tagged = 0;
        foreach (var item in allFlat)
        {
            if (!item.IsUtility && IsUtilityPage(item.Url, item.Title))
            {
                item.IsUtility = true;
                tagged++;
            }
        }
        if (tagged > 0)
            log?.Event("UTILITY_PAGES_TAGGED", ("count", tagged));
    }

    private static bool IsUtilityPage(string url, string title)
    {
        return MunicipalRootClassifier.IsUtilityPage(url, title);

#pragma warning disable CS0162
        string path = "";
        try { path = new Uri(url).AbsolutePath.ToLowerInvariant(); } catch { }
        var t = (title ?? "").ToLowerInvariant();

        // URL path keyword matching
        var pathKeywords = new[]
        {
            "kontakt", "tillganglighet", "tillgaenglighet", "accessibility",
            "press", "grafisk", "intranat", "intranet", "admin",
            "logga-in", "login", "ticket", "e-post", "epost",
            "webbplatsen", "om-webbplatsen", "medarbetare", "for-medarbetare"
        };
        foreach (var kw in pathKeywords)
            if (path.Contains(kw, StringComparison.Ordinal)) return true;

        // Title keyword matching (handles pages whose URL is clean but title is revealing)
        var titleKeywords = new[]
        {
            "kontakt", "tillgänglighet", "accessibility", "intranät", "intranet",
            "press", "grafisk profil", "ticket server", "e-post", "om webbplatsen",
            "för medarbetare", "medarbetare", "logga in", "server"
        };
        foreach (var kw in titleKeywords)
            if (t.Contains(kw, StringComparison.Ordinal)) return true;

        return false;
#pragma warning restore CS0162
    }

    // Returns true if the URL looks like a top-level municipal service section
    // (single path segment, not a news/event/search/archive/utility page).
    // Used by the structural-promotion fallback to decide which visible seeds
    // to promote to the structural primary list.
    private static bool IsLikelyMunicipalSection(string url)
    {
        return MunicipalRootClassifier.IsLikelyMunicipalSection(url);

#pragma warning disable CS0162
        string path;
        try { path = new Uri(url).AbsolutePath; }
        catch { return false; }

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Only top-level single-segment paths (e.g. /barn-och-utbildning).
        // Inner paths like /nyheter/article-name or /jobb/lediga-jobb are skipped.
        if (segments.Length != 1)
            return false;

        var seg = segments[0].ToLowerInvariant();

        // Reject known non-structural path prefixes
        if (seg.Contains("nyheter") || seg.Contains("nyhet") || seg.Contains("news")
            || seg.Contains("artikel") || seg.Contains("pressrelease") || seg.Contains("pressrum"))
            return false;

        if (seg.Contains("evenemang") || seg.Contains("event")
            || seg.Contains("kalender") || seg.Contains("aktivitet"))
            return false;

        if (seg.Contains("arkiv") || seg.Contains("archive")
            || seg.Contains("sok") || seg.Contains("search") || seg.Contains("hitta"))
            return false;

        if (seg.Contains("cookie") || seg.Contains("integritet") || seg.Contains("gdpr")
            || seg.Contains("om-kakor") || seg.Contains("kakor"))
            return false;

        if (seg.Contains("webbkarta") || seg.Contains("sitemap")
            || seg.Contains("driftinformation") || seg.Contains("tillganglighet")
            || seg.Contains("tillgaenglighet"))
            return false;

        return true;
#pragma warning restore CS0162
    }
}
