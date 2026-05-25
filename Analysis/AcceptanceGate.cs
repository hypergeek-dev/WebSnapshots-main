using System.Text;
using System.Text.Json;

namespace WebSnapshots.Analysis;

/// <summary>
/// Reads a comparison-report.json and decides whether a candidate scraper change is acceptable.
/// Does not apply code or auto-accept patches. It only writes a machine-readable decision.
/// </summary>
public static class AcceptanceGate
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<AcceptanceResult> EvaluateAsync(string comparisonReportPath)
    {
        var result = new AcceptanceResult
        {
            Accepted = false,
            Decision = "requires_human_review"
        };

        if (!File.Exists(comparisonReportPath))
        {
            result.Reasons.Add("comparison-report.json not found; cannot evaluate.");
            result.Decision = "rejected";
            return result;
        }

        RunComparison? cmp;
        try
        {
            var json = await File.ReadAllTextAsync(comparisonReportPath, Encoding.UTF8);
            cmp = JsonSerializer.Deserialize<RunComparison>(json, _jsonOpts);
        }
        catch (Exception ex)
        {
            result.Reasons.Add($"Failed to parse comparison report: {ex.Message}");
            result.Decision = "rejected";
            return result;
        }

        if (cmp == null)
        {
            result.Reasons.Add("Comparison report is empty or invalid.");
            result.Decision = "rejected";
            return result;
        }

        result.Improvements.AddRange(cmp.Improvements);
        result.Regressions.AddRange(cmp.Regressions);

        var profile = cmp.DiagnosticProfile;
        var currentFlatPages = GetCurrentInt(cmp, "flatPageCount");
        var currentTreeNodes = GetCurrentInt(cmp, "treeNodeCount");
        var currentNavGroups = GetCurrentInt(cmp, "navGroupCount");

        if (cmp.CurrentQualityErrors.Count > 0)
            Reject(result, "CRITICAL: Current run has quality errors: " + string.Join(", ", cmp.CurrentQualityErrors));

        if (currentFlatPages == 0)
            Reject(result, "CRITICAL: Current run captured zero crawl pages.");

        if (profile == "smoke")
        {
            if (cmp.CmsChanged)
                Review(result, $"CMS changed in smoke run ({cmp.PreviousCms} -> {cmp.CurrentCms}).");

            return FinalizeDecision(result, cmp);
        }

        var capturesScreenshots =
            profile == "snapshot-diagnostic" ||
            profile == "full-validation" ||
            string.IsNullOrWhiteSpace(profile);

        var flatPageRegression = HasRegression(cmp, "flatPageCount");
        var navDiagnosticHasQualityOffset =
            profile == "nav-diagnostic" &&
            cmp.CurrentSaturated &&
            (HasImprovement(cmp, "orphanPageCount") ||
             HasImprovement(cmp, "treeNodeCount") ||
             HasImprovement(cmp, "duplicateTitleCount") ||
             HasImprovement(cmp, "duplicateUrlCount"));

        if (flatPageRegression && !navDiagnosticHasQualityOffset)
            Reject(result, "CRITICAL: Flat page count dropped by more than 10%.");
        else if (flatPageRegression)
            Review(result, "Flat page count dropped, but nav-diagnostic quality/saturation improved; requires human review instead of automatic rejection.");

        if (HasRegression(cmp, "treeNodeCount"))
            Reject(result, "CRITICAL: Tree node count collapsed.");

        if (capturesScreenshots && HasRegression(cmp, "pagesCaptured"))
            Reject(result, "CRITICAL: Pages captured dropped significantly.");

        if (cmp.CmsChanged)
            Review(result, $"CMS detection changed ({cmp.PreviousCms} -> {cmp.CurrentCms}).");

        if (cmp.PrimaryNavGroupChanged)
            Review(result, $"Primary nav group changed ({cmp.PreviousPrimaryNavGroup} -> {cmp.CurrentPrimaryNavGroup}).");

        if (HasRegression(cmp, "orphanPageCount"))
            Review(result, "Orphan page count increased significantly.");

        if (HasRegression(cmp, "timeoutCount"))
            Review(result, "Timeout count increased.");

        if (HasRegression(cmp, "failedVisitCount"))
            Review(result, "Failed visits increased.");

        if (capturesScreenshots && HasRegression(cmp, "snapshotFailCount"))
            Review(result, "Snapshot failures increased.");

        if (profile == "cms-diagnostic")
        {
            if (string.IsNullOrWhiteSpace(cmp.CurrentCms))
                Review(result, "CMS diagnostic did not detect a CMS.");

            if (currentNavGroups == 0 && currentTreeNodes == 0)
                Review(result, "CMS diagnostic did not establish start navigation.");
        }

        if (profile == "nav-diagnostic")
        {
            if (cmp.CurrentUniqueTemplateFingerprints == 0)
                Review(result, "Nav diagnostic has no template fingerprint coverage.");

            if (cmp.PreviousSaturated && !cmp.CurrentSaturated)
                Review(result, "Saturation was previously reached but is no longer reached.");

            if (HasRegression(cmp, "uniqueTemplateFingerprints"))
                Review(result, "Template coverage dropped.");
        }

        if (profile == "snapshot-diagnostic" && HasRegression(cmp, "textExtractFailCount"))
            Review(result, "Text extraction failures increased.");

        if (profile == "full-validation" && !cmp.CurrentSaturated && cmp.CurrentUniqueTemplateFingerprints > 0)
            Review(result, "Full validation did not reach saturation; verify whether the page budget is sufficient.");

        foreach (var flag in cmp.RequiresHumanReview)
            Review(result, $"Flagged: {flag}");

        return FinalizeDecision(result, cmp);
    }

    private static AcceptanceResult FinalizeDecision(AcceptanceResult result, RunComparison cmp)
    {
        if (result.Decision == "requires_human_review" && result.Reasons.Count == 0)
        {
            if (cmp.Improvements.Count > 0 && cmp.Regressions.Count == 0)
            {
                result.Accepted = true;
                result.Decision = "accepted";
                result.Reasons.Add("No regressions detected. Improvements observed.");
            }
            else if (cmp.Regressions.Count == 0 && cmp.RequiresHumanReview.Count == 0 &&
                     !cmp.CmsChanged && !cmp.PrimaryNavGroupChanged)
            {
                result.Accepted = true;
                result.Decision = "accepted";
                result.Reasons.Add("No regressions and no flags.");
            }
        }

        return result;
    }

    private static void Reject(AcceptanceResult result, string reason)
    {
        result.Reasons.Add(reason);
        result.Decision = "rejected";
        result.Accepted = false;
    }

    private static void Review(AcceptanceResult result, string reason)
    {
        result.Reasons.Add(reason);
        if (result.Decision != "rejected")
            result.Decision = "requires_human_review";
    }

    private static bool HasRegression(RunComparison cmp, string metric)
        => cmp.Regressions.Any(r => r.StartsWith(metric, StringComparison.OrdinalIgnoreCase));

    private static bool HasImprovement(RunComparison cmp, string metric)
        => cmp.Improvements.Any(r => r.StartsWith(metric, StringComparison.OrdinalIgnoreCase));

    private static int GetCurrentInt(RunComparison cmp, string metric)
        => ToInt(cmp.MetricDeltas.FirstOrDefault(d => d.Metric == metric)?.Current);

    private static int ToInt(object? value)
    {
        if (value == null) return 0;
        if (value is int i) return i;
        if (value is long l) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n)) return n;
            if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var s)) return s;
        }
        return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }
}
