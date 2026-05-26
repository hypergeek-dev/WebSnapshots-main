using System.Text;
using System.Text.Json;
using WebSnapshots.Telemetry;

namespace WebSnapshots.Analysis;

/// <summary>
/// Reads telemetry.jsonl + nav.json from a scrape folder and produces
/// quality-report.json + quality-report.md.
/// </summary>
public static class QualityReportBuilder
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<QualityReport> BuildAsync(
        string siteDir,
        string runId,
        string targetId,
        string municipality,
        string host,
        string startUrl,
        string expectedCms,
        Dictionary<string, object?> config,
        string diagnosticProfile = "",
        int saturationWindowSize = 50)
    {
        var report = new QualityReport
        {
            DiagnosticProfile = diagnosticProfile,
            RunId = runId,
            TargetId = targetId,
            Municipality = municipality,
            Host = host,
            StartUrl = startUrl,
            ExpectedCms = expectedCms,
            GeneratedUtc = DateTime.UtcNow.ToString("o"),
            Config = config
        };

        await LoadNavMetricsAsync(siteDir, report);
        await LoadTelemetryMetricsAsync(siteDir, report, saturationWindowSize);
        BuildWarnings(report);

        var jsonPath = Path.Combine(siteDir, "quality-report.json");
        var mdPath = Path.Combine(siteDir, "quality-report.md");

        await File.WriteAllTextAsync(jsonPath,
            JsonSerializer.Serialize(report, _jsonOpts),
            Encoding.UTF8);

        await File.WriteAllTextAsync(mdPath,
            RenderMarkdown(report),
            Encoding.UTF8);

        return report;
    }

    // ── Nav metrics from nav.json ────────────────────────────────────────────

    private static async Task LoadNavMetricsAsync(string siteDir, QualityReport report)
    {
        var navPath = Path.Combine(siteDir, "nav.json");
        if (!File.Exists(navPath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(navPath, Encoding.UTF8);
            var nav = JsonSerializer.Deserialize<NavIndex>(json, _jsonOpts);
            if (nav == null) return;

            report.Metrics.FlatPageCount = nav.Flat?.Count ?? 0;
            report.Metrics.NavGroupCount = nav.NavGroups?.Count ?? 0;
            report.Metrics.VisibleGroupCount = nav.VisibleGroups?.Count ?? 0;

            report.Metrics.TreeNodeCount = CountTreeNodes(nav.Nodes);

            if (nav.NavGroups != null && nav.NavGroups.Count > 0)
            {
                var primary = nav.NavGroups.OrderBy(g => g.Rank).First();
                report.Metrics.PrimaryNavGroupId = primary.Id ?? "";
            }

            if (nav.Flat != null)
            {
                // Orphans: pages whose ParentUrl is not in Flat (except start URL)
                var urlSet = nav.Flat.Select(x => x.Url ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
                report.Metrics.OrphanPageCount = nav.Flat.Count(x =>
                    !string.IsNullOrWhiteSpace(x.Url) &&
                    !string.Equals(x.Url, nav.StartUrl, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(x.ParentUrl) &&
                    !urlSet.Contains(x.ParentUrl));

                // Duplicate URLs
                report.Metrics.DuplicateUrlCount = nav.Flat
                    .GroupBy(x => x.Url ?? "", StringComparer.OrdinalIgnoreCase)
                    .Count(g => g.Count() > 1);

                // Duplicate titles (non-empty)
                report.Metrics.DuplicateTitleCount = nav.Flat
                    .Where(x => !string.IsNullOrWhiteSpace(x.Title))
                    .GroupBy(x => x.Title ?? "", StringComparer.OrdinalIgnoreCase)
                    .Count(g => g.Count() > 1);

                // Empty titles
                report.Metrics.EmptyTitleCount = nav.Flat.Count(x => string.IsNullOrWhiteSpace(x.Title));
            }

            // Root-child quality metrics
            if (!string.IsNullOrWhiteSpace(nav.StartUrl) && nav.Flat != null)
            {
                report.Metrics.SyntheticParentCount = nav.Flat.Count(x => x.IsSynthetic);

                // Homepage IA metrics (Phase 7)
                report.Metrics.HomepageAnchoredSections = nav.HomepageSections?.Count ?? 0;
                var homepageSectionUrls = (nav.HomepageSections ?? new List<HomepageSection>())
                    .Select(hs => hs.Url)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Build child set for singleton detection (children-of-root that have their own children)
                var urlsWithChildren = nav.Flat
                    .Where(x => !string.IsNullOrWhiteSpace(x.ParentUrl))
                    .Select(x => x.ParentUrl)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var rootChildCount   = 0;
                var deepRootChild    = 0;
                var structuralRoot   = 0;
                var utilityRoot      = 0;
                var rawUrlTitleRoot  = 0;
                var syntheticRoot    = 0;
                var discoveredRoot   = 0;
                var deepLeafRoot     = 0;
                var singletonRoot    = 0;

                foreach (var it in nav.Flat)
                {
                    if (string.IsNullOrWhiteSpace(it.Url)) continue;
                    if (it.Url.Equals(nav.StartUrl, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!it.ParentUrl.Equals(nav.StartUrl, StringComparison.OrdinalIgnoreCase)) continue;

                    rootChildCount++;

                    var isDeep = CountNavPathSegments(it.Url) >= 2;
                    if (isDeep) deepRootChild++;

                    var isSingleton = !urlsWithChildren.Contains(it.Url);
                    if (isSingleton) singletonRoot++;
                    if (it.IsSynthetic) syntheticRoot++;
                    if (IsRawUrlTitle(it.Title, it.Url)) rawUrlTitleRoot++;

                    if (homepageSectionUrls.Contains(it.Url))
                        structuralRoot++;
                    else if (it.IsUtility)
                        utilityRoot++;
                    else
                    {
                        discoveredRoot++;
                        if (isDeep) deepLeafRoot++;
                    }
                }

                report.Metrics.RootChildCount         = rootChildCount;
                report.Metrics.DeepRootChildCount      = deepRootChild;
                report.Metrics.StructuralRootChildren  = structuralRoot;
                report.Metrics.UtilityRootChildren     = utilityRoot;
                report.Metrics.RawUrlTitleRootChildren = rawUrlTitleRoot;
                report.Metrics.SyntheticRootChildren   = syntheticRoot;
                report.Metrics.DiscoveredRootChildren  = discoveredRoot;
                report.Metrics.DeepLeafRootChildren    = deepLeafRoot;
                report.Metrics.SingletonRootChildren   = singletonRoot;
                report.Metrics.RootTopologyVerdict     = GetRootTopologyVerdict(report.Metrics);
            }

            // host / startUrl from nav if not set
            if (string.IsNullOrWhiteSpace(report.Host)) report.Host = nav.Host ?? "";
            if (string.IsNullOrWhiteSpace(report.StartUrl)) report.StartUrl = nav.StartUrl ?? "";
        }
        catch (Exception ex)
        {
            report.Warnings.Add(QualityWarning.Warn("nav_load_failed",
                $"Could not load nav.json: {ex.Message}"));
        }
    }

    private static int CountTreeNodes(List<NavNode>? nodes)
    {
        if (nodes == null) return 0;
        var count = 0;
        foreach (var n in nodes)
            count += 1 + CountTreeNodes(n.Children);
        return count;
    }

    // ── Telemetry metrics from telemetry.jsonl ────────────────────────────────

    private static async Task LoadTelemetryMetricsAsync(string siteDir, QualityReport report, int windowSize)
    {
        var telPath = Path.Combine(siteDir, "telemetry.jsonl");
        if (!File.Exists(telPath))
            return;

        var failedVisits = 0;
        var timeouts = 0;
        var snapshotFails = 0;
        var textExtractFails = 0;
        var criticalEvents = 0;
        var outLinkTotal = 0;
        var outLinkPageCount = 0;
        var pagesCaptured = 0;
        var maxDepth = 0;
        var maxPagesReached = false;
        var pagesByDepth = new Dictionary<int, int>();
        var rejectionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Saturation tracking
        var seenFingerprints = new Dictionary<string, (string PathShape, string ContentType, string ExampleUrl, int Count)>(StringComparer.OrdinalIgnoreCase);
        var seenPathPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRejectionReasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenErrorClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Per-page: was any new pattern added at this page? (true = yes, false = no)
        var pageNewPattern = new List<bool>();

        try
        {
            var lines = await File.ReadAllLinesAsync(telPath, Encoding.UTF8);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                TelemetryEvent? evt;
                try { evt = JsonSerializer.Deserialize<TelemetryEvent>(line, _jsonOpts); }
                catch { continue; }
                if (evt == null) continue;

                switch (evt.EventName)
                {
                    case "crawl_failed":
                        failedVisits++;
                        criticalEvents++;
                        seenErrorClasses.Add("crawl_failed");
                        report.Warnings.Add(QualityWarning.Error("crawl_failed",
                            $"Crawl failed before producing a usable navigation result: {GetField(evt, "error") ?? "unknown error"}"));
                        report.SuspectedFailureModes.Add("crawl_runtime_failure");
                        break;

                    case "cms_detected":
                        report.DetectedCms = GetField(evt, "kind") ?? "";
                        report.CmsConfidence = GetField(evt, "confidence") ?? "";
                        var mismatch = GetFieldBool(evt, "mismatch");
                        if (mismatch.HasValue) report.CmsMismatch = mismatch.Value;
                        else if (!string.IsNullOrWhiteSpace(report.ExpectedCms) && !string.IsNullOrWhiteSpace(report.DetectedCms))
                            report.CmsMismatch = !string.Equals(report.DetectedCms, report.ExpectedCms, StringComparison.OrdinalIgnoreCase);
                        break;

                    case "page_visit_success":
                        var depth = GetFieldInt(evt, "depth") ?? 0;
                        if (depth > maxDepth) maxDepth = depth;
                        pagesByDepth.TryGetValue(depth, out var dCount);
                        pagesByDepth[depth] = dCount + 1;
                        var outLinks = GetFieldInt(evt, "outlinkCount") ?? 0;
                        outLinkTotal += outLinks;
                        outLinkPageCount++;
                        break;

                    case "page_visit_fail":
                        failedVisits++;
                        var errKind = GetField(evt, "errorKind") ?? "";
                        if (errKind.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                            timeouts++;
                        seenErrorClasses.Add(string.IsNullOrWhiteSpace(errKind) ? "generic" : errKind);
                        break;

                    case "page_fingerprint":
                    {
                        var hash = GetField(evt, "fingerprintHash") ?? "";
                        var pathShape = GetField(evt, "pathShape") ?? "";
                        var contentType = GetField(evt, "contentType") ?? "";
                        var url = evt.Url ?? "";

                        var newThisPage = false;

                        if (!string.IsNullOrWhiteSpace(hash))
                        {
                            if (!seenFingerprints.ContainsKey(hash))
                            {
                                seenFingerprints[hash] = (pathShape, contentType, url, 1);
                                newThisPage = true;
                            }
                            else
                            {
                                var (ps, ct, eu, cnt) = seenFingerprints[hash];
                                seenFingerprints[hash] = (ps, ct, eu, cnt + 1);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(pathShape) && seenPathPatterns.Add(pathShape))
                            newThisPage = true;

                        if (!string.IsNullOrWhiteSpace(contentType) && seenContentTypes.Add(contentType))
                            newThisPage = true;

                        pageNewPattern.Add(newThisPage);
                        break;
                    }

                    case "snapshot_success":
                        pagesCaptured++;
                        break;

                    case "snapshot_fail":
                        snapshotFails++;
                        var snapErr = GetField(evt, "errorKind") ?? "snapshot_error";
                        seenErrorClasses.Add(snapErr);
                        break;

                    case "text_extract_fail":
                        textExtractFails++;
                        break;

                    case "crawl_complete":
                        var mr = GetFieldBool(evt, "maxPagesReached");
                        if (mr.HasValue) maxPagesReached = mr.Value;
                        break;

                    case "visible_links_promoted_to_structural":
                        report.Metrics.StructuralFallbackUsed = true;
                        report.Metrics.StructuralFallbackReason = GetField(evt, "reason") ?? "primary nav had zero usable links; visible section links promoted";
                        break;

                    case "link_rejected":
                        var reason = GetField(evt, "reason") ?? "unknown";
                        rejectionCounts.TryGetValue(reason, out var rc);
                        rejectionCounts[reason] = rc + 1;
                        seenRejectionReasons.Add(reason);
                        break;

                    case "link_rejections_summary":
                        // Aggregate per-page rejection summaries into totals
                        if (evt.Fields != null)
                        {
                            foreach (var kv in evt.Fields)
                            {
                                if (kv.Key is "pageUrl" or "accepted") continue;
                                var count = 0;
                                if (kv.Value is JsonElement je && je.TryGetInt32(out var jc)) count = jc;
                                else if (kv.Value is int ic) count = ic;
                                if (count > 0)
                                {
                                    rejectionCounts.TryGetValue(kv.Key, out var existing);
                                    rejectionCounts[kv.Key] = existing + count;
                                    seenRejectionReasons.Add(kv.Key);
                                }
                            }
                        }
                        break;
                }

                if (string.Equals(evt.Severity, "critical", StringComparison.OrdinalIgnoreCase) &&
                    evt.EventName != "crawl_failed")
                {
                    criticalEvents++;
                }
            }
        }
        catch (Exception ex)
        {
            report.Warnings.Add(QualityWarning.Warn("telemetry_load_failed",
                $"Could not fully parse telemetry.jsonl: {ex.Message}"));
        }

        report.Metrics.FailedVisitCount = failedVisits;
        report.Metrics.TimeoutCount = timeouts;
        report.Metrics.SnapshotFailCount = snapshotFails;
        report.Metrics.TextExtractFailCount = textExtractFails;
        report.Metrics.MaxDepthReached = maxDepth;
        report.Metrics.MaxPagesReached = maxPagesReached;
        report.Metrics.PagesCaptured = pagesCaptured;
        report.Metrics.AverageOutLinksPerPage = outLinkPageCount > 0
            ? Math.Round((double)outLinkTotal / outLinkPageCount, 1)
            : 0;
        report.Metrics.PagesByDepth = pagesByDepth.ToDictionary(
            kv => kv.Key.ToString(),
            kv => kv.Value);
        report.Metrics.RejectedLinksByReason = rejectionCounts;

        if (criticalEvents > 0)
        {
            report.Warnings.Add(QualityWarning.Error("critical_telemetry_events",
                $"{criticalEvents} critical telemetry event(s) were emitted during the run.",
                criticalEvents));
        }

        // ── Saturation report ────────────────────────────────────────────────
        var hasFingerprintData = seenFingerprints.Count > 0 || seenPathPatterns.Count > 0;

        var newInWindow = 0;
        if (pageNewPattern.Count > 0)
        {
            var window = pageNewPattern.TakeLast(windowSize).ToList();
            newInWindow = window.Count(x => x);
        }

        var saturated = hasFingerprintData &&
                        pageNewPattern.Count >= windowSize &&
                        newInWindow == 0;

        string saturationReason = "";
        if (!hasFingerprintData)
            saturationReason = "No page fingerprint events in telemetry — fingerprinting may be disabled.";
        else if (pageNewPattern.Count < windowSize)
            saturationReason = $"Only {pageNewPattern.Count} pages fingerprinted — window ({windowSize}) not filled yet.";
        else if (saturated)
            saturationReason = $"No new fingerprints, path patterns, content types, or rejection reasons in last {windowSize} pages.";
        else
            saturationReason = $"{newInWindow} new patterns in last {windowSize} pages — still discovering.";

        report.Saturation = new SaturationReport
        {
            Enabled = hasFingerprintData,
            UniqueTemplateFingerprints = seenFingerprints.Count,
            UniquePathPatterns = seenPathPatterns.Count,
            UniqueRejectionReasons = seenRejectionReasons.Count,
            UniqueErrorClasses = seenErrorClasses.Count,
            UniqueContentTypes = seenContentTypes.Count,
            NewPatternsInLastWindow = newInWindow,
            WindowSize = windowSize,
            Saturated = saturated,
            SaturationReason = saturationReason,
            TopFingerprints = seenFingerprints
                .OrderByDescending(kv => kv.Value.Count)
                .Take(20)
                .Select(kv => new FingerprintSummary
                {
                    Hash = kv.Key,
                    PathShape = kv.Value.PathShape,
                    ContentType = kv.Value.ContentType,
                    Count = kv.Value.Count,
                    ExampleUrl = kv.Value.ExampleUrl
                })
                .ToList()
        };
    }

    // ── Warning detection ────────────────────────────────────────────────────

    private static void BuildWarnings(QualityReport r)
    {
        var m = r.Metrics;
        var warns = r.Warnings;

        if (m.FlatPageCount == 0)
        {
            warns.Add(QualityWarning.Error("no_pages_found",
                "Crawl produced zero pages. Scraper likely failed entirely."));
            r.SuspectedFailureModes.Add("complete_crawl_failure");
        }

        if (m.FlatPageCount > 0 && m.TreeNodeCount < m.FlatPageCount * 0.3)
        {
            warns.Add(QualityWarning.Warn("tree_far_smaller_than_flat",
                $"Tree has {m.TreeNodeCount} nodes but flat has {m.FlatPageCount} pages — many pages are not structurally placed.",
                m.TreeNodeCount, (int)(m.FlatPageCount * 0.3)));
            r.SuspectedFailureModes.Add("weak_navigation_extraction");
        }

        if (m.FlatPageCount > 0 && (double)m.OrphanPageCount / m.FlatPageCount > 0.4)
        {
            warns.Add(QualityWarning.Warn("high_orphan_ratio",
                $"{m.OrphanPageCount} of {m.FlatPageCount} pages have no valid parent ({m.OrphanPageCount * 100 / m.FlatPageCount}%).",
                m.OrphanPageCount));
            r.SuspectedFailureModes.Add("broken_parent_assignment");
        }

        if (m.FlatPageCount > 0 && (double)m.DuplicateTitleCount / m.FlatPageCount > 0.15)
        {
            warns.Add(QualityWarning.Warn("high_duplicate_titles",
                $"{m.DuplicateTitleCount} duplicate titles out of {m.FlatPageCount} pages.",
                m.DuplicateTitleCount));
        }

        if (m.FlatPageCount > 0 && (double)m.EmptyTitleCount / m.FlatPageCount > 0.1)
        {
            warns.Add(QualityWarning.Warn("high_empty_title_ratio",
                $"{m.EmptyTitleCount} pages have empty titles ({m.EmptyTitleCount * 100 / m.FlatPageCount}%).",
                m.EmptyTitleCount));
        }

        // Root-child quality: a very high root-child count means the archive tree
        // is visually unusable regardless of total page count or depth metrics.
        if (m.RootChildCount > 50)
        {
            warns.Add(QualityWarning.Error("root_child_explosion",
                $"Root node has {m.RootChildCount} direct children — tree is visually unusable. " +
                "Expected ≤ ~20 for a well-structured municipal site.",
                m.RootChildCount));
            r.SuspectedFailureModes.Add("root_child_explosion");
        }

        if (m.DeepRootChildCount > 20)
        {
            warns.Add(QualityWarning.Error("root_leaf_pollution",
                $"{m.DeepRootChildCount} root children have multi-segment URLs (path depth ≥ 2). " +
                "These are leaf or article pages misplaced directly under the root node.",
                m.DeepRootChildCount));
            r.SuspectedFailureModes.Add("root_leaf_pollution");
        }
        else if (m.DeepRootChildCount > 0)
        {
            warns.Add(QualityWarning.Warn("root_child_path_depth_too_high",
                $"{m.DeepRootChildCount} root children have multi-segment paths — possible orphan placement.",
                m.DeepRootChildCount));
        }

        if (m.RawUrlTitleRootChildren > 0)
        {
            warns.Add(QualityWarning.Error("raw_url_labels_at_root",
                $"{m.RawUrlTitleRootChildren} direct root children still have raw URL labels.",
                m.RawUrlTitleRootChildren));
            r.SuspectedFailureModes.Add("raw_url_labels_at_root");
        }

        if (m.SyntheticParentCount > 0)
        {
            warns.Add(QualityWarning.Info("synthetic_parents_inserted",
                $"{m.SyntheticParentCount} synthetic intermediate parent node(s) were inserted to group " +
                "leaf pages whose URL-prefix parent was never crawled."));
        }

        // Phase 7: homepage IA quality warnings ───────────────────────────────

        if (m.HomepageAnchoredSections > 0)
        {
            warns.Add(QualityWarning.Info("homepage_sections_found",
                $"{m.HomepageAnchoredSections} homepage card/tile sections detected — " +
                $"{m.StructuralRootChildren} are anchored at root as structural IA sections."));
        }
        else if (m.FlatPageCount > 20)
        {
            warns.Add(QualityWarning.Warn("missing_homepage_sections",
                "No homepage card/tile sections were detected — root hierarchy cannot be anchored " +
                "to the municipality's intended IA. Check if the homepage is JS-heavy or blocked."));
            r.SuspectedFailureModes.Add("missing_homepage_sections");
        }

        if (m.UtilityRootChildren > 5)
        {
            warns.Add(QualityWarning.Warn("utility_root_pollution",
                $"{m.UtilityRootChildren} utility/meta pages (Kontakt, Tillgänglighet, etc.) appear " +
                "as direct root children — they are grouped in the 'Verktyg & information' viewer section."));
        }

        if (m.DiscoveredRootChildren > 10)
        {
            warns.Add(QualityWarning.Warn("orphan_content_cluster",
                $"{m.DiscoveredRootChildren} pages at root could not be attached to any structural " +
                "IA section — they appear as 'Övrigt innehåll' in the viewer.",
                m.DiscoveredRootChildren));
            if (m.DeepLeafRootChildren > 10 || m.RootChildCount > 30)
                r.SuspectedFailureModes.Add("orphan_content_cluster");
        }

        if (m.MaxPagesReached)
        {
            warns.Add(QualityWarning.Warn("max_pages_reached",
                "Crawl was capped by max-pages limit — site may have more content."));
            r.RecommendedInvestigationAreas.Add("Increase --max-pages-per-site and re-run to check for hidden depth.");
        }

        if (m.MaxDepthReached is > 0 and < 2)
        {
            warns.Add(QualityWarning.Warn("low_max_depth",
                $"Max depth reached was only {m.MaxDepthReached} — navigation extraction likely shallow."));
            r.SuspectedFailureModes.Add("shallow_crawl_depth");
        }

        var capturesScreenshots =
            string.IsNullOrWhiteSpace(r.DiagnosticProfile) ||
            string.Equals(r.DiagnosticProfile, "snapshot-diagnostic", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.DiagnosticProfile, "full-validation", StringComparison.OrdinalIgnoreCase);

        if (capturesScreenshots && m.FlatPageCount > 5 && m.PagesCaptured < m.FlatPageCount / 2)
        {
            warns.Add(QualityWarning.Warn("low_snapshot_ratio",
                $"Only {m.PagesCaptured} snapshots captured but {m.FlatPageCount} pages were crawled.",
                m.PagesCaptured, m.FlatPageCount));
            r.SuspectedFailureModes.Add("snapshot_phase_failure");
        }

        if (r.CmsMismatch)
        {
            warns.Add(QualityWarning.Warn("cms_mismatch",
                $"Expected CMS '{r.ExpectedCms}' but detected '{r.DetectedCms}'. CMS-specific extractor may not apply."));
            r.RecommendedInvestigationAreas.Add($"Verify CMS signals for {r.Host} — may need target config update or new CMS extractor.");
        }

        if (string.IsNullOrWhiteSpace(r.Metrics.PrimaryNavGroupId))
        {
            warns.Add(QualityWarning.Warn("no_primary_nav_group",
                "No nav group was identified as primary — crawl seeded from fallback heuristics only."));
            r.SuspectedFailureModes.Add("nav_group_selection_failed");
        }

        if (m.NavGroupCount > 0 && m.FlatPageCount > 0 && m.AverageOutLinksPerPage < 2)
        {
            warns.Add(QualityWarning.Warn("low_average_outlinks",
                $"Average outlinks per page is {m.AverageOutLinksPerPage:F1} — links may not be extracted correctly.",
                m.AverageOutLinksPerPage));
        }

        if (m.TimeoutCount > 5)
        {
            warns.Add(QualityWarning.Warn("high_timeout_count",
                $"{m.TimeoutCount} page timeouts — site may be slow or blocking the crawler.",
                m.TimeoutCount));
            r.RecommendedInvestigationAreas.Add("Check --nav-goto-timeout and network conditions.");
        }

        if (m.TextExtractFailCount > m.FlatPageCount * 0.2 && m.FlatPageCount > 0)
        {
            warns.Add(QualityWarning.Warn("high_text_extract_fail",
                $"{m.TextExtractFailCount} text extraction failures.",
                m.TextExtractFailCount));
        }

        if (m.RejectedLinksByReason.TryGetValue("binary_asset", out var binRejected) && binRejected > 50)
        {
            warns.Add(QualityWarning.Info("many_binary_rejections",
                $"{binRejected} links rejected as binary assets — check binary asset filter patterns."));
        }

        if (m.RejectedLinksByReason.TryGetValue("external_host", out var extRejected) && extRejected > 200)
        {
            warns.Add(QualityWarning.Info("many_external_rejections",
                $"{extRejected} links rejected as external — site may heavily link to external resources."));
        }

        // ── Profile + saturation interpretation ─────────────────────────────
        var profile = r.DiagnosticProfile;
        var sat = r.Saturation;

        var isDiagnostic = !string.IsNullOrWhiteSpace(profile) &&
            !string.Equals(profile, "full-validation", StringComparison.OrdinalIgnoreCase);

        if (isDiagnostic)
        {
            r.RecommendedInvestigationAreas.Add(
                $"This is a '{profile}' diagnostic run — it is a representative sample, not a full crawl. " +
                "More pages does not automatically mean better. Fewer pages does not mean worse.");
        }

        r.RecommendedInvestigationAreas.Add(
            "Determinism note: crawler settings and report ordering are stable, but the source website is live; content, navigation, redirects, and server timing can drift between runs.");

        if (sat.Enabled)
        {
            if (sat.Saturated)
            {
                warns.Add(QualityWarning.Info("saturation_reached",
                    $"Pattern saturation reached — no new fingerprints/path-patterns in last {sat.WindowSize} pages. " +
                    $"This diagnostic run is likely representative."));
                r.RecommendedInvestigationAreas.Add(
                    "Saturation reached — this diagnostic sample is representative. Consider running full-validation next.");
            }
            else if (sat.Enabled && m.FlatPageCount > 10)
            {
                var shallowProfile = profile == "smoke" || profile == "cms-diagnostic";
                if (shallowProfile)
                {
                    warns.Add(QualityWarning.Info("not_saturated",
                        $"Pattern saturation not reached after {m.FlatPageCount} pages — " +
                        $"expected for the '{profile}' profile, which is not designed for coverage measurement. " +
                        "Run nav-diagnostic or full-validation to evaluate pattern coverage."));
                }
                else
                {
                    warns.Add(QualityWarning.Info("not_saturated",
                        $"Pattern saturation not reached after {m.FlatPageCount} pages " +
                        $"({sat.NewPatternsInLastWindow} new patterns in last {sat.WindowSize}). " +
                        "Larger diagnostic or full-validation run may reveal more issues."));
                    r.RecommendedInvestigationAreas.Add(
                        "Not yet saturated — consider larger diagnostic run (more maxPages) or full-validation.");
                }
            }
        }

        // ── Snap-only/nav-only distinction ───────────────────────────────────
        if (m.PagesCaptured == 0 && m.FlatPageCount > 0 && isDiagnostic)
        {
            r.RecommendedInvestigationAreas.Add(
                "No snapshots captured (expected for nav-diagnostic / cms-diagnostic profiles). " +
                "Run snapshot-diagnostic or full-validation to check screenshot quality.");
        }
    }

    // ── Markdown rendering ───────────────────────────────────────────────────

    private static string RenderMarkdown(QualityReport r)
    {
        var sb = new StringBuilder();
        var m = r.Metrics;

        sb.AppendLine($"# Quality Report: {r.Municipality} ({r.TargetId})");
        sb.AppendLine();
        sb.AppendLine($"**Run:** `{r.RunId}`  ");
        if (!string.IsNullOrWhiteSpace(r.DiagnosticProfile))
            sb.AppendLine($"**Profile:** `{r.DiagnosticProfile}`  ");
        sb.AppendLine($"**Generated:** {r.GeneratedUtc}  ");
        sb.AppendLine($"**Start URL:** {r.StartUrl}  ");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(r.DiagnosticProfile))
        {
            var isFullVal = string.Equals(r.DiagnosticProfile, "full-validation", StringComparison.OrdinalIgnoreCase);
            sb.AppendLine("> **Interpretation note:** " + (isFullVal
                ? "This is a full-validation run. Completeness and capture count matter."
                : $"This is a `{r.DiagnosticProfile}` diagnostic sample. It is NOT a full crawl. " +
                  "More pages ≠ better. Fewer pages ≠ worse. Evaluate tree quality, orphan ratio, and pattern coverage."));
            sb.AppendLine("> **Determinism note:** Settings and ordering are stable, but the source website is live and may drift between runs.");
            sb.AppendLine();
        }

        sb.AppendLine("## CMS");
        sb.AppendLine($"| Field | Value |");
        sb.AppendLine($"|---|---|");
        sb.AppendLine($"| Expected | `{r.ExpectedCms}` |");
        sb.AppendLine($"| Detected | `{r.DetectedCms}` |");
        sb.AppendLine($"| Confidence | `{r.CmsConfidence}` |");
        sb.AppendLine($"| Mismatch | `{r.CmsMismatch}` |");
        sb.AppendLine();

        sb.AppendLine("## Crawl Metrics");
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|---|---|");
        sb.AppendLine($"| Flat pages | {m.FlatPageCount} |");
        sb.AppendLine($"| Tree nodes | {m.TreeNodeCount} |");
        sb.AppendLine($"| Nav groups | {m.NavGroupCount} |");
        sb.AppendLine($"| Visible groups | {m.VisibleGroupCount} |");
        sb.AppendLine($"| Primary nav group | `{m.PrimaryNavGroupId}` |");
        if (m.StructuralFallbackUsed)
            sb.AppendLine($"| Structural fallback | `true` — {m.StructuralFallbackReason} |");
        sb.AppendLine($"| Orphan pages | {m.OrphanPageCount} |");
        sb.AppendLine($"| Duplicate URLs | {m.DuplicateUrlCount} |");
        sb.AppendLine($"| Duplicate titles | {m.DuplicateTitleCount} |");
        sb.AppendLine($"| Empty titles | {m.EmptyTitleCount} |");
        sb.AppendLine($"| Failed visits | {m.FailedVisitCount} |");
        sb.AppendLine($"| Timeouts | {m.TimeoutCount} |");
        sb.AppendLine($"| Snapshot fails | {m.SnapshotFailCount} |");
        sb.AppendLine($"| Text extract fails | {m.TextExtractFailCount} |");
        sb.AppendLine($"| Avg outlinks/page | {m.AverageOutLinksPerPage:F1} |");
        sb.AppendLine($"| Max depth reached | {m.MaxDepthReached} |");
        sb.AppendLine($"| Max pages reached | {m.MaxPagesReached} |");
        sb.AppendLine($"| Pages captured | {m.PagesCaptured} |");
        sb.AppendLine($"| Root children | {m.RootChildCount} |");
        sb.AppendLine($"| Deep root children (path≥2) | {m.DeepRootChildCount} |");
        sb.AppendLine($"| Root topology verdict | {m.RootTopologyVerdict} |");
        sb.AppendLine($"| Synthetic parent nodes | {m.SyntheticParentCount} |");
        sb.AppendLine($"| Homepage sections detected | {m.HomepageAnchoredSections} |");
        sb.AppendLine($"| Structural root children | {m.StructuralRootChildren} |");
        sb.AppendLine($"| Utility root children | {m.UtilityRootChildren} |");
        sb.AppendLine($"| Raw URL title root children | {m.RawUrlTitleRootChildren} |");
        sb.AppendLine($"| Synthetic root children | {m.SyntheticRootChildren} |");
        sb.AppendLine($"| Discovered root children | {m.DiscoveredRootChildren} |");
        sb.AppendLine($"| Deep-leaf root children | {m.DeepLeafRootChildren} |");
        sb.AppendLine($"| Singleton root children | {m.SingletonRootChildren} |");
        sb.AppendLine();

        if (m.PagesByDepth.Count > 0)
        {
            sb.AppendLine("## Pages by Depth");
            sb.AppendLine($"| Depth | Pages |");
            sb.AppendLine($"|---|---|");
            foreach (var kv in m.PagesByDepth.OrderBy(x => int.Parse(x.Key)))
                sb.AppendLine($"| {kv.Key} | {kv.Value} |");
            sb.AppendLine();
        }

        if (m.RejectedLinksByReason.Count > 0)
        {
            sb.AppendLine("## Rejected Links by Reason");
            sb.AppendLine($"| Reason | Count |");
            sb.AppendLine($"|---|---|");
            foreach (var kv in m.RejectedLinksByReason.OrderByDescending(x => x.Value))
                sb.AppendLine($"| {kv.Key} | {kv.Value} |");
            sb.AppendLine();
        }

        if (r.Saturation.Enabled)
        {
            sb.AppendLine("## Pattern Saturation");
            sb.AppendLine($"| Field | Value |");
            sb.AppendLine($"|---|---|");
            sb.AppendLine($"| Saturated | `{r.Saturation.Saturated}` |");
            sb.AppendLine($"| Unique fingerprints | {r.Saturation.UniqueTemplateFingerprints} |");
            sb.AppendLine($"| Unique path patterns | {r.Saturation.UniquePathPatterns} |");
            sb.AppendLine($"| Unique content types | {r.Saturation.UniqueContentTypes} |");
            sb.AppendLine($"| Unique rejection reasons | {r.Saturation.UniqueRejectionReasons} |");
            sb.AppendLine($"| Unique error classes | {r.Saturation.UniqueErrorClasses} |");
            sb.AppendLine($"| New patterns in last {r.Saturation.WindowSize} pages | {r.Saturation.NewPatternsInLastWindow} |");
            sb.AppendLine($"| Status | {r.Saturation.SaturationReason} |");
            sb.AppendLine();

            if (r.Saturation.TopFingerprints.Count > 0)
            {
                sb.AppendLine("### Top Template Fingerprints");
                sb.AppendLine($"| Hash | Path Shape | Content Type | Count |");
                sb.AppendLine($"|---|---|---|---|");
                foreach (var fp in r.Saturation.TopFingerprints.Take(10))
                    sb.AppendLine($"| `{fp.Hash}` | `{fp.PathShape}` | {fp.ContentType} | {fp.Count} |");
                sb.AppendLine();
            }
        }

        if (r.Warnings.Count > 0)
        {
            sb.AppendLine("## Warnings");
            foreach (var w in r.Warnings.OrderByDescending(w => w.Severity))
                sb.AppendLine($"- **[{w.Severity.ToUpperInvariant()}]** `{w.Code}` — {w.Message}");
            sb.AppendLine();
        }

        if (r.SuspectedFailureModes.Count > 0)
        {
            sb.AppendLine("## Suspected Failure Modes");
            foreach (var f in r.SuspectedFailureModes)
                sb.AppendLine($"- `{f}`");
            sb.AppendLine();
        }

        if (r.RecommendedInvestigationAreas.Count > 0)
        {
            sb.AppendLine("## Recommended Investigation Areas");
            foreach (var a in r.RecommendedInvestigationAreas)
                sb.AppendLine($"- {a}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static int CountNavPathSegments(string url)
    {
        try
        {
            return new Uri(url).AbsolutePath
                .TrimEnd('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Length;
        }
        catch { return 0; }
    }

    // ── Field extraction helpers ─────────────────────────────────────────────

    private static bool IsRawUrlTitle(string? title, string? url)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) return true;
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("/", StringComparison.Ordinal)) return true;
        return !string.IsNullOrWhiteSpace(url) && t.Equals(url, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRootTopologyVerdict(QualityMetrics m)
    {
        if (m.RootChildCount > 50 || m.DeepRootChildCount > 20 || m.RawUrlTitleRootChildren > 0)
            return "FAIL";

        if (m.DeepRootChildCount > 0 || m.UtilityRootChildren > 5 || m.DiscoveredRootChildren > 10 || m.SyntheticRootChildren > 8)
            return "PASS_WITH_MINOR_ISSUES";

        return "PASS";
    }

    private static string? GetField(TelemetryEvent evt, string key)
    {
        if (evt.Fields == null || !evt.Fields.TryGetValue(key, out var v)) return null;
        if (v is JsonElement je) return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();
        return v?.ToString();
    }

    private static int? GetFieldInt(TelemetryEvent evt, string key)
    {
        if (evt.Fields == null || !evt.Fields.TryGetValue(key, out var v)) return null;
        if (v is JsonElement je && je.TryGetInt32(out var i)) return i;
        if (v is int vi) return vi;
        if (int.TryParse(v?.ToString(), out var pi)) return pi;
        return null;
    }

    private static bool? GetFieldBool(TelemetryEvent evt, string key)
    {
        if (evt.Fields == null || !evt.Fields.TryGetValue(key, out var v)) return null;
        if (v is JsonElement je && je.ValueKind == JsonValueKind.True) return true;
        if (v is JsonElement je2 && je2.ValueKind == JsonValueKind.False) return false;
        if (v is bool b) return b;
        if (bool.TryParse(v?.ToString(), out var pb)) return pb;
        return null;
    }
}
