using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace WebSnapshots;

public sealed class NavCrawler
{
    private readonly SnapshotConfig _cfg;
    private readonly PlaywrightRunner _pw;
    private readonly Logger _log;

    public sealed class Options
    {
        public int MaxDepth { get; set; } = 3;
        public int MaxPagesPerSite { get; set; } = 1500;
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

    private sealed record QItem(string Url, int Depth, string ParentUrl, bool StructuralBranch);

    public NavCrawler(SnapshotConfig cfg, PlaywrightRunner pw, Logger log)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _pw = pw ?? throw new ArgumentNullException(nameof(pw));
        _log = log ?? throw new ArgumentNullException(nameof(log));
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
            cmsExtractor = new CmsAwareNavExtractor(cms, _log);

            _log.Event("NAV_CMS_DETECTED",
                ("host", host),
                ("cms", cms.Kind.ToString()),
                ("confidence", cms.Confidence),
                ("signals", string.Join(" | ", cms.Signals ?? new List<string>())));

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
        var visibleSeedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in visibleGroups)
        {
            foreach (var it in g.Flat)
            {
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

        _log.Event("NAV_GROUPS_EXTRACTED",
            ("host", host),
            ("groups", navGroups.Count),
            ("primaryCount", primaryNavUrls.Count),
            ("visibleGroups", visibleGroups.Count),
            ("visibleSeedUrls", visibleSeedUrls.Count));

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
                        q.Enqueue(new QItem(u, 1, startAbs, link.IsStructural));
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
                    }
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
                Title = string.IsNullOrWhiteSpace(title) ? item.Url : title,
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
                        q.Enqueue(new QItem(child, nextDepth, parentForChild, childStructural));
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

        var nodes = NavTreeBuilder.Build(treeFlat, startAbs);

        foreach (var g in navGroups)
            g.Nodes = NavTreeBuilder.Build(g.Flat, startAbs);

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

            foreach (var raw in hrefs)
            {
                var child = NormalizeInternalUrl(raw, currentUrl, host, opt.DropQueryStrings);
                if (string.IsNullOrWhiteSpace(child)) continue;

                if (!ShouldAttachUnderParent(
                    parentUrl: currentUrl,
                    childUrl: child,
                    startUrl: startUrl,
                    protectedTopLevel: protectedTopLevel,
                    isStructuralEdge: false))
                {
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

            // Filter known crawl amplifiers / noise pages
            if (path.Equals("/webbkarta", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/webbkarta/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

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

    private static string GetSectionPrefix(string path)
    {
        path = NormalizePath(path);

        if (path == "/")
            return "/";

        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            path = path[..^5];

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
}