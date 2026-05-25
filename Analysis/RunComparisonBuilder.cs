using System.Text.Json;

namespace WebSnapshots.Analysis;

public static class RunComparisonBuilder
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<RunComparison> BuildAsync(
        string previousDir,
        string currentDir)
    {
        var prev = await LoadReportAsync(previousDir);
        var curr = await LoadReportAsync(currentDir);

        var cmp = new RunComparison
        {
            PreviousRunId = prev?.RunId ?? "",
            CurrentRunId = curr?.RunId ?? "",
            TargetId = curr?.TargetId ?? prev?.TargetId ?? "",
            Municipality = curr?.Municipality ?? prev?.Municipality ?? "",
            GeneratedUtc = DateTime.UtcNow.ToString("o")
        };

        if (prev == null || curr == null)
        {
            cmp.RequiresHumanReview.Add("One or both quality reports are missing — cannot compare.");
            cmp.OverallVerdict = "requires_human_review";
            return cmp;
        }

        cmp.PreviousQualityErrors = prev.Warnings
            .Where(w => string.Equals(w.Severity, "error", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
        cmp.CurrentQualityErrors = curr.Warnings
            .Where(w => string.Equals(w.Severity, "error", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
        cmp.PreviousQualityWarnings = prev.Warnings
            .Where(w => string.Equals(w.Severity, "warning", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
        cmp.CurrentQualityWarnings = curr.Warnings
            .Where(w => string.Equals(w.Severity, "warning", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        if (cmp.CurrentQualityErrors.Count > 0)
            cmp.RequiresHumanReview.Add("Current run has quality errors: " + string.Join(", ", cmp.CurrentQualityErrors));

        CompareCms(prev, curr, cmp);
        CompareNavGroup(prev, curr, cmp);
        CompareMetrics(prev, curr, cmp);
        CompareUrls(previousDir, currentDir, cmp);
        CompareParentRelationships(previousDir, currentDir, cmp);
        ComparePagesByDepth(prev, curr, cmp);
        CompareRejectedReasons(prev, curr, cmp);
        CompareSaturation(prev, curr, cmp);
        DetermineVerdict(cmp);

        return cmp;
    }

    private static async Task<QualityReport?> LoadReportAsync(string dir)
    {
        var path = Path.Combine(dir, "quality-report.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<QualityReport>(json, _jsonOpts);
        }
        catch { return null; }
    }

    private static void CompareCms(QualityReport prev, QualityReport curr, RunComparison cmp)
    {
        cmp.PreviousCms = prev.DetectedCms;
        cmp.CurrentCms = curr.DetectedCms;
        cmp.CmsChanged = !string.Equals(prev.DetectedCms, curr.DetectedCms, StringComparison.OrdinalIgnoreCase);

        if (cmp.CmsChanged)
            cmp.RequiresHumanReview.Add(
                $"CMS detection changed from '{prev.DetectedCms}' to '{curr.DetectedCms}'.");
    }

    private static void CompareNavGroup(QualityReport prev, QualityReport curr, RunComparison cmp)
    {
        cmp.PreviousPrimaryNavGroup = prev.Metrics.PrimaryNavGroupId;
        cmp.CurrentPrimaryNavGroup = curr.Metrics.PrimaryNavGroupId;
        cmp.PrimaryNavGroupChanged = !string.Equals(
            prev.Metrics.PrimaryNavGroupId,
            curr.Metrics.PrimaryNavGroupId,
            StringComparison.OrdinalIgnoreCase);

        if (cmp.PrimaryNavGroupChanged)
            cmp.RequiresHumanReview.Add(
                $"Primary nav group changed from '{prev.Metrics.PrimaryNavGroupId}' to '{curr.Metrics.PrimaryNavGroupId}'.");
    }

    private static void CompareMetrics(QualityReport prev, QualityReport curr, RunComparison cmp)
    {
        AddInt(cmp, "flatPageCount", prev.Metrics.FlatPageCount, curr.Metrics.FlatPageCount,
            lowerIsBad: true, threshold: 0.10);
        AddInt(cmp, "treeNodeCount", prev.Metrics.TreeNodeCount, curr.Metrics.TreeNodeCount,
            lowerIsBad: true, threshold: 0.15);
        AddInt(cmp, "navGroupCount", prev.Metrics.NavGroupCount, curr.Metrics.NavGroupCount,
            lowerIsBad: true, threshold: 0.20);
        AddInt(cmp, "visibleGroupCount", prev.Metrics.VisibleGroupCount, curr.Metrics.VisibleGroupCount,
            lowerIsBad: true, threshold: 0.20);
        AddInt(cmp, "orphanPageCount", prev.Metrics.OrphanPageCount, curr.Metrics.OrphanPageCount,
            lowerIsBad: false, threshold: 0.20);
        AddInt(cmp, "duplicateUrlCount", prev.Metrics.DuplicateUrlCount, curr.Metrics.DuplicateUrlCount,
            lowerIsBad: false, threshold: 0.20);
        AddInt(cmp, "duplicateTitleCount", prev.Metrics.DuplicateTitleCount, curr.Metrics.DuplicateTitleCount,
            lowerIsBad: false, threshold: 0.20);
        AddInt(cmp, "failedVisitCount", prev.Metrics.FailedVisitCount, curr.Metrics.FailedVisitCount,
            lowerIsBad: false, threshold: 0.20);
        AddInt(cmp, "timeoutCount", prev.Metrics.TimeoutCount, curr.Metrics.TimeoutCount,
            lowerIsBad: false, threshold: 0.30);
        AddInt(cmp, "snapshotFailCount", prev.Metrics.SnapshotFailCount, curr.Metrics.SnapshotFailCount,
            lowerIsBad: false, threshold: 0.20);
        AddInt(cmp, "textExtractFailCount", prev.Metrics.TextExtractFailCount, curr.Metrics.TextExtractFailCount,
            lowerIsBad: false, threshold: 0.20);
        AddInt(cmp, "pagesCaptured", prev.Metrics.PagesCaptured, curr.Metrics.PagesCaptured,
            lowerIsBad: true, threshold: 0.10);
        AddInt(cmp, "emptyTitleCount", prev.Metrics.EmptyTitleCount, curr.Metrics.EmptyTitleCount,
            lowerIsBad: false, threshold: 0.20);
    }

    private static void AddInt(RunComparison cmp, string metric, int prev, int curr,
        bool lowerIsBad, double threshold)
    {
        var delta = curr - prev;
        string verdict;

        if (prev == 0 && curr == 0)
            verdict = "unchanged";
        else if (delta == 0)
            verdict = "unchanged";
        else if (lowerIsBad)
        {
            var pctDrop = prev > 0 ? (double)-delta / prev : 0;
            if (delta < 0 && pctDrop >= threshold)
            {
                verdict = "regressed";
                cmp.Regressions.Add($"{metric}: {prev} → {curr} ({-delta} drop, {pctDrop:P0})");
            }
            else if (delta > 0)
            {
                verdict = "improved";
                cmp.Improvements.Add($"{metric}: {prev} → {curr} (+{delta})");
            }
            else
                verdict = "unchanged";
        }
        else // lower is good
        {
            var pctRise = prev > 0 ? (double)delta / prev : 0;
            if (delta > 0 && pctRise >= threshold)
            {
                verdict = "regressed";
                cmp.Regressions.Add($"{metric}: {prev} → {curr} (+{delta} rise, {pctRise:P0})");
            }
            else if (delta < 0 && prev > 0)
            {
                verdict = "improved";
                cmp.Improvements.Add($"{metric}: {prev} → {curr} ({delta})");
            }
            else
                verdict = "unchanged";
        }

        cmp.MetricDeltas.Add(new MetricDelta
        {
            Metric = metric,
            Previous = prev,
            Current = curr,
            Delta = delta,
            Verdict = verdict
        });
    }

    private static void CompareUrls(string previousDir, string currentDir, RunComparison cmp)
    {
        var prevUrls = LoadUrlsFromNav(previousDir);
        var currUrls = LoadUrlsFromNav(currentDir);

        cmp.UrlsAdded = currUrls.Except(prevUrls, StringComparer.OrdinalIgnoreCase).Take(100).ToList();
        cmp.UrlsRemoved = prevUrls.Except(currUrls, StringComparer.OrdinalIgnoreCase).Take(100).ToList();

        if (cmp.UrlsRemoved.Count > 20)
            cmp.RequiresHumanReview.Add(
                $"{cmp.UrlsRemoved.Count} URLs were present previously but are missing now.");
    }

    private static void CompareParentRelationships(string previousDir, string currentDir, RunComparison cmp)
    {
        var prevParents = LoadParentMapFromNav(previousDir);
        var currParents = LoadParentMapFromNav(currentDir);

        var commonUrls = prevParents.Keys.Intersect(currParents.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var url in commonUrls)
        {
            var prevParent = prevParents[url] ?? "";
            var currParent = currParents[url] ?? "";
            if (!string.Equals(prevParent, currParent, StringComparison.OrdinalIgnoreCase))
            {
                cmp.ChangedParentRelationshipCount++;
                if (cmp.ChangedParentRelationships.Count < 100)
                {
                    cmp.ChangedParentRelationships.Add(new ParentRelationshipChange
                    {
                        Url = url,
                        PreviousParentUrl = prevParent,
                        CurrentParentUrl = currParent
                    });
                }
            }
        }

        if (cmp.ChangedParentRelationshipCount > Math.Max(10, commonUrls.Count / 5))
            cmp.RequiresHumanReview.Add(
                $"{cmp.ChangedParentRelationshipCount} parent relationships changed — verify tree quality.");
    }

    private static Dictionary<string, string> LoadParentMapFromNav(string dir)
    {
        var path = Path.Combine(dir, "nav.json");
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            var nav = JsonSerializer.Deserialize<NavIndex>(json, _jsonOpts);
            return nav?.Flat?
                .Where(x => !string.IsNullOrWhiteSpace(x.Url))
                .GroupBy(x => x.Url ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().ParentUrl ?? "", StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static void ComparePagesByDepth(QualityReport prev, QualityReport curr, RunComparison cmp)
    {
        foreach (var key in prev.Metrics.PagesByDepth.Keys
            .Union(curr.Metrics.PagesByDepth.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(ParseDepthKey))
        {
            prev.Metrics.PagesByDepth.TryGetValue(key, out var p);
            curr.Metrics.PagesByDepth.TryGetValue(key, out var c);
            cmp.PagesByDepthDelta[key] = BuildDelta($"depth:{key}", p, c);
        }
    }

    private static void CompareRejectedReasons(QualityReport prev, QualityReport curr, RunComparison cmp)
    {
        foreach (var key in prev.Metrics.RejectedLinksByReason.Keys
            .Union(curr.Metrics.RejectedLinksByReason.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            prev.Metrics.RejectedLinksByReason.TryGetValue(key, out var p);
            curr.Metrics.RejectedLinksByReason.TryGetValue(key, out var c);
            cmp.RejectedLinksByReasonDelta[key] = BuildDelta(key, p, c);
        }
    }

    private static MetricDelta BuildDelta(string metric, int prev, int curr)
    {
        var delta = curr - prev;
        return new MetricDelta
        {
            Metric = metric,
            Previous = prev,
            Current = curr,
            Delta = delta,
            Verdict = delta == 0 ? "unchanged" : "changed"
        };
    }

    private static int ParseDepthKey(string key)
        => int.TryParse(key, out var i) ? i : int.MaxValue;

    private static HashSet<string> LoadUrlsFromNav(string dir)
    {
        var path = Path.Combine(dir, "nav.json");
        if (!File.Exists(path)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            var nav = JsonSerializer.Deserialize<NavIndex>(json, _jsonOpts);
            return nav?.Flat?.Select(x => x.Url ?? "")
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static void CompareSaturation(QualityReport prev, QualityReport curr, RunComparison cmp)
    {
        cmp.DiagnosticProfile = curr.DiagnosticProfile;
        cmp.PreviousSaturated = prev.Saturation.Saturated;
        cmp.CurrentSaturated = curr.Saturation.Saturated;
        cmp.PreviousUniqueTemplateFingerprints = prev.Saturation.UniqueTemplateFingerprints;
        cmp.CurrentUniqueTemplateFingerprints = curr.Saturation.UniqueTemplateFingerprints;
        cmp.PreviousNewPatternsInLastWindow = prev.Saturation.NewPatternsInLastWindow;
        cmp.CurrentNewPatternsInLastWindow = curr.Saturation.NewPatternsInLastWindow;

        if (!curr.Saturation.Enabled)
        {
            cmp.SaturationNote = "No fingerprint data (profile may not capture enough pages).";
            return;
        }

        var prevFp = prev.Saturation.UniqueTemplateFingerprints;
        var currFp = curr.Saturation.UniqueTemplateFingerprints;
        cmp.NewPatternCount = Math.Max(0, currFp - prevFp);
        cmp.RemovedPatternCount = Math.Max(0, prevFp - currFp);
        cmp.ChangedPatternCount = Math.Abs(currFp - prevFp);

        if (curr.Saturation.Saturated)
        {
            cmp.SaturationNote = $"Saturated at {currFp} unique template patterns — coverage looks complete.";
        }
        else
        {
            var newInWindow = curr.Saturation.NewPatternsInLastWindow;
            cmp.SaturationNote = $"Not saturated: {newInWindow} new patterns in last {curr.Saturation.WindowSize} pages. " +
                                 $"Consider running with more pages or using the full-validation profile.";

            if (newInWindow > 5)
                cmp.RequiresHumanReview.Add(
                    $"Saturation not reached ({newInWindow} new patterns in last window) — coverage may be incomplete.");
        }

        if (currFp > prevFp + 3)
            cmp.Improvements.Add($"uniqueTemplateFingerprints: {prevFp} → {currFp} (+{currFp - prevFp} new page templates seen)");
        else if (prevFp > currFp + 3)
        {
            cmp.Regressions.Add($"uniqueTemplateFingerprints: {prevFp} → {currFp} (fewer templates seen — possible coverage loss)");
            cmp.RequiresHumanReview.Add("Template coverage dropped significantly.");
        }

        var sameBudget = EqualsMetricConfig(prev, curr, "maxPages") &&
                         EqualsMetricConfig(prev, curr, "maxDepth");
        if (sameBudget && prev.Saturation.Saturated && !curr.Saturation.Saturated)
            cmp.RequiresHumanReview.Add("Saturation was previously reached under the same page/depth budget but is no longer reached.");
    }

    private static bool EqualsMetricConfig(QualityReport prev, QualityReport curr, string key)
    {
        prev.Config.TryGetValue(key, out var p);
        curr.Config.TryGetValue(key, out var c);
        return string.Equals(p?.ToString(), c?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static void DetermineVerdict(RunComparison cmp)
    {
        if (cmp.Regressions.Count > 0 && cmp.Improvements.Count == 0)
            cmp.OverallVerdict = "regressed";
        else if (cmp.Improvements.Count > 0 && cmp.Regressions.Count == 0)
            cmp.OverallVerdict = "improved";
        else if (cmp.RequiresHumanReview.Count > 0)
            cmp.OverallVerdict = "requires_human_review";
        else if (cmp.Regressions.Count > 0)
            cmp.OverallVerdict = "requires_human_review";
        else
            cmp.OverallVerdict = "unchanged";
    }
}
