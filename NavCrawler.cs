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
                    if (primaryNavSet.Add(u))
                        primaryNavUrls.Add(u);

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
        ReparentOrphansByUrlPath(allFlat, treeFlat, treeFlatEdges, startAbs);

        var nodes = NavTreeBuilder.Build(treeFlat, startAbs);

        foreach (var g in navGroups)
            g.Nodes = NavTreeBuilder.Build(g.Flat, startAbs);

        _telemetry?.Emit(TelemetryPhase.Crawl, "crawl_complete", TelemetrySeverity.Info,
            startAbs, new Dictionary<string, object?>
            {
                ["totalPages"] = allFlat.Count,
                ["maxDepthReached"] = depthVisited.Count > 0 ? depthVisited.Keys.Max() : 0,
                ["maxPagesReached"] = allFlat.Count >= opt.MaxPagesPerSite,
                ["elapsedMs"] = (long)sw.Elapsed.TotalMilliseconds,
                ["pagesByDepth"] = string.Join(", ", depthVisited.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"))
            });

        return new NavIndex
        {
            Host = host,
            StartUrl = startAbs,
            GeneratedUtc = DateTime.UtcNow,
            Flat = allFlat,
            Nodes = nodes,
            NavGroups = navGroups,
            VisibleGroups = visibleGroups
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

    // After the crawl loop, pages seeded directly from the start page may have a more
    // specific natural parent (a structural section page whose URL is an ancestor of theirs).
    // This happens on CMS sites (e.g. SiteVision) where section landing pages render their
    // child-page grid via JavaScript, making it invisible during the fast nav-crawl phase.
    // We scan allFlat for such pages and re-parent them to the best structural section ancestor.
    // GetSectionPrefix/CanonicalizeSectionPrefix must be correct for this to work.
    private static void ReparentOrphansByUrlPath(
        List<NavItem> allFlat,
        List<NavItem> treeFlat,
        HashSet<string> treeFlatEdges,
        string startUrl)
    {
        // Structural section pages: treeFlat items that are direct children of root (depth 1).
        var sectionPages = treeFlat
            .Where(p => !string.IsNullOrWhiteSpace(p.Url)
                     && !p.Url.Equals(startUrl, StringComparison.OrdinalIgnoreCase)
                     && p.ParentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sectionPages.Count == 0) return;

        foreach (var item in allFlat)
        {
            if (string.IsNullOrWhiteSpace(item.Url)) continue;
            if (item.Url.Equals(startUrl, StringComparison.OrdinalIgnoreCase)) continue;
            if (!item.ParentUrl.Equals(startUrl, StringComparison.OrdinalIgnoreCase)) continue;

            // Find the deepest (longest prefix) structural section page that is an ancestor.
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

            if (bestParent == null) continue;

            item.ParentUrl = bestParent.Url;
            item.Depth = bestParent.Depth + 1;

            var edgeKey = $"{bestParent.Url}|{item.Url}";
            if (treeFlatEdges.Add(edgeKey))
            {
                treeFlat.Add(new NavItem
                {
                    Url = item.Url,
                    Title = item.Title,
                    Depth = item.Depth,
                    ParentUrl = item.ParentUrl
                });
            }
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

    // Returns true if the URL looks like a top-level municipal service section
    // (single path segment, not a news/event/search/archive/utility page).
    // Used by the structural-promotion fallback to decide which visible seeds
    // to promote to the structural primary list.
    private static bool IsLikelyMunicipalSection(string url)
    {
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
    }
}