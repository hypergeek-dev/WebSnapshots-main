using System.Text;
using System.Text.Json;

namespace WebSnapshots.Analysis;

/// <summary>
/// Produces a compact ai-review-pack.md for an AI coding agent to inspect.
/// Short enough to fit in one context window. Evidence-first, no guessing.
/// </summary>
public static class AiReviewPackBuilder
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task BuildAsync(string siteDir, RunComparison? comparison = null)
    {
        var reportPath = Path.Combine(siteDir, "quality-report.json");
        if (!File.Exists(reportPath))
        {
            Console.Error.WriteLine($"[REVIEW-PACK] quality-report.json not found in {siteDir}");
            return;
        }

        QualityReport? report;
        try
        {
            var json = await File.ReadAllTextAsync(reportPath, Encoding.UTF8);
            report = JsonSerializer.Deserialize<QualityReport>(json, _jsonOpts);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[REVIEW-PACK] Failed to load quality report: {ex.Message}");
            return;
        }

        if (report == null) return;

        var failedUrls = await LoadFailedUrlsAsync(siteDir);
        var rejectedPatterns = report.Metrics.RejectedLinksByReason;

        var sb = new StringBuilder();

        sb.AppendLine("# AI Review Pack");
        sb.AppendLine();
        sb.AppendLine("> This file is generated for an AI coding agent.");
        sb.AppendLine("> Read the evidence. Do not guess. Do not make broad rewrites.");
        sb.AppendLine("> Propose one narrow, measurable change at a time.");
        sb.AppendLine();

        sb.AppendLine("## Target");
        sb.AppendLine($"- **ID:** `{report.TargetId}`");
        sb.AppendLine($"- **Municipality:** {report.Municipality}");
        sb.AppendLine($"- **Host:** {report.Host}");
        sb.AppendLine($"- **Start URL:** {report.StartUrl}");
        sb.AppendLine($"- **Run:** `{report.RunId}`");
        sb.AppendLine();

        AppendProfileSection(sb, report);

        sb.AppendLine("## CMS");
        sb.AppendLine($"- Expected: `{report.ExpectedCms}`");
        sb.AppendLine($"- Detected: `{report.DetectedCms}` (confidence: {report.CmsConfidence})");
        if (report.CmsMismatch)
            sb.AppendLine($"- **MISMATCH** — CMS-specific extractor paths may not apply.");
        sb.AppendLine();

        AppendQualityScore(sb, report);
        AppendSaturationSection(sb, report);
        AppendSuspiciousFingerprints(sb, report);
        AppendWarnings(sb, report);
        AppendFailureModes(sb, report);
        AppendFailedUrls(sb, failedUrls);
        AppendRejectedPatterns(sb, rejectedPatterns);
        AppendNavSummary(sb, report);
        AppendComparisonSummary(sb, comparison);
        AppendRelevantFiles(sb, siteDir);
        AppendNextSteps(sb, report, comparison);
        AppendFullValidationNeed(sb, report);
        AppendForbiddenAssumptions(sb);

        var outPath = Path.Combine(siteDir, "ai-review-pack.md");
        await File.WriteAllTextAsync(outPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"[REVIEW-PACK] Written: {outPath}");
    }

    private static void AppendProfileSection(StringBuilder sb, QualityReport r)
    {
        var profile = string.IsNullOrWhiteSpace(r.DiagnosticProfile) ? "unknown" : r.DiagnosticProfile;
        sb.AppendLine("## Diagnostic Profile");
        sb.AppendLine($"- **Profile:** `{profile}`");

        var interpretation = profile switch
        {
            "smoke" => "Smoke run: only checks basic connectivity and CMS detection. Page count is intentionally minimal.",
            "cms-diagnostic" => "CMS diagnostic: checks CMS detection fidelity and nav extraction. Page count is secondary.",
            "nav-diagnostic" => "Nav diagnostic: evaluates navigation structure completeness. Screenshot coverage is secondary.",
            "snapshot-diagnostic" => "Snapshot diagnostic: evaluates screenshot quality and text extraction. Nav structure is secondary.",
            "full-validation" => "Full validation: all metrics apply. Low page count or snapshot failures are regressions.",
            _ => "Profile not recognized — apply general judgment."
        };

        sb.AppendLine($"- **Interpretation:** {interpretation}");
        sb.AppendLine();
    }

    private static void AppendSaturationSection(StringBuilder sb, QualityReport r)
    {
        var sat = r.Saturation;
        if (!sat.Enabled) return;

        sb.AppendLine("## Pattern Saturation");

        var status = sat.Saturated
            ? $"**SATURATED** — {sat.UniqueTemplateFingerprints} unique templates, coverage appears complete."
            : $"**Not saturated** — {sat.NewPatternsInLastWindow} new patterns in last {sat.WindowSize} pages.";

        sb.AppendLine($"- Status: {status}");
        sb.AppendLine($"- Unique template fingerprints: {sat.UniqueTemplateFingerprints}");
        sb.AppendLine($"- Unique path patterns: {sat.UniquePathPatterns}");
        sb.AppendLine($"- Unique content types: {sat.UniqueContentTypes}");
        sb.AppendLine($"- Unique rejection reasons: {sat.UniqueRejectionReasons}");

        if (!string.IsNullOrWhiteSpace(sat.SaturationReason))
            sb.AppendLine($"- Saturation reason: {sat.SaturationReason}");

        if (sat.TopFingerprints.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Top template fingerprints:");
            sb.AppendLine("| Hash | Path shape | Content type | Count |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var fp in sat.TopFingerprints.Take(10))
                sb.AppendLine($"| `{fp.Hash}` | `{fp.PathShape}` | {fp.ContentType} | {fp.Count} |");
        }

        sb.AppendLine();
    }

    private static void AppendNextSteps(StringBuilder sb, QualityReport r, RunComparison? cmp)
    {
        var profile = r.DiagnosticProfile;
        sb.AppendLine("## Recommended Next Steps");

        if (profile == "smoke")
        {
            sb.AppendLine($"1. Run `cms-diagnostic` for deeper CMS/nav analysis:");
            sb.AppendLine($"   `dotnet run -- diagnostic --target {r.TargetId} --profile cms-diagnostic`");
        }
        else if (profile == "cms-diagnostic")
        {
            sb.AppendLine($"1. Run `nav-diagnostic` to evaluate navigation completeness:");
            sb.AppendLine($"   `dotnet run -- diagnostic --target {r.TargetId} --profile nav-diagnostic`");
        }
        else if (profile == "nav-diagnostic")
        {
            var satNote = r.Saturation.Enabled && !r.Saturation.Saturated
                ? $" (not saturated — {r.Saturation.NewPatternsInLastWindow} new patterns still appearing)"
                : "";

            if (!string.IsNullOrEmpty(satNote))
            {
                sb.AppendLine($"1. Coverage is not saturated{satNote}. Consider running with more pages:");
                sb.AppendLine($"   `dotnet run -- diagnostic --target {r.TargetId} --profile full-validation`");
            }
            else if (cmp != null)
            {
                sb.AppendLine($"1. Compare results:");
                sb.AppendLine($"   `dotnet run -- compare --previous <baseline> --current <current>`");
            }
            else
            {
                sb.AppendLine($"1. Run snapshot diagnostic to evaluate screenshot quality:");
                sb.AppendLine($"   `dotnet run -- diagnostic --target {r.TargetId} --profile snapshot-diagnostic`");
            }
        }
        else if (profile == "snapshot-diagnostic")
        {
            sb.AppendLine($"1. Run full validation:");
            sb.AppendLine($"   `dotnet run -- diagnostic --target {r.TargetId} --profile full-validation`");
        }
        else if (profile == "full-validation" && cmp != null)
        {
            sb.AppendLine($"1. Run acceptance gate: `dotnet run -- acceptance --comparison <comparison-report.json>`");
        }

        sb.AppendLine();
    }

    private static void AppendSuspiciousFingerprints(StringBuilder sb, QualityReport r)
    {
        var suspicious = r.Saturation.TopFingerprints
            .Where(fp => fp.Count == 1)
            .Take(10)
            .ToList();

        if (suspicious.Count == 0) return;

        sb.AppendLine("## Suspicious Template Fingerprints");
        sb.AppendLine("| Hash | Path shape | Content type | Example |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var fp in suspicious)
            sb.AppendLine($"| `{fp.Hash}` | `{fp.PathShape}` | {fp.ContentType} | {fp.ExampleUrl} |");
        sb.AppendLine();
    }

    private static void AppendFullValidationNeed(StringBuilder sb, QualityReport r)
    {
        var profile = r.DiagnosticProfile;
        var stillNeeded = profile != "full-validation";

        sb.AppendLine("## Full Validation Needed");
        sb.AppendLine(stillNeeded
            ? "- Yes. Diagnostic profiles are for feedback-loop evidence; run `full-validation` before treating the archive behavior as final."
            : "- No additional full-validation profile is required for this run, but compare against a previous validation baseline before release.");
        sb.AppendLine();
    }

    private static void AppendQualityScore(StringBuilder sb, QualityReport r)
    {
        var m = r.Metrics;
        var errorCount = r.Warnings.Count(w => w.Severity == "error");
        var warnCount = r.Warnings.Count(w => w.Severity == "warning");

        sb.AppendLine("## Quality Summary");
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|---|---|");
        sb.AppendLine($"| Flat pages | {m.FlatPageCount} |");
        sb.AppendLine($"| Tree nodes | {m.TreeNodeCount} |");
        sb.AppendLine($"| Pages captured | {m.PagesCaptured} |");
        sb.AppendLine($"| Failed visits | {m.FailedVisitCount} |");
        sb.AppendLine($"| Timeouts | {m.TimeoutCount} |");
        sb.AppendLine($"| Snapshot fails | {m.SnapshotFailCount} |");
        sb.AppendLine($"| Orphan pages | {m.OrphanPageCount} |");
        sb.AppendLine($"| Empty titles | {m.EmptyTitleCount} |");
        sb.AppendLine($"| Max depth reached | {m.MaxDepthReached} |");
        sb.AppendLine($"| Errors | {errorCount} |");
        sb.AppendLine($"| Warnings | {warnCount} |");
        sb.AppendLine();
    }

    private static void AppendWarnings(StringBuilder sb, QualityReport r)
    {
        if (r.Warnings.Count == 0) return;

        sb.AppendLine("## Top Warnings");
        foreach (var w in r.Warnings.OrderByDescending(w => w.Severity).Take(10))
            sb.AppendLine($"- **[{w.Severity.ToUpperInvariant()}]** `{w.Code}` — {Truncate(w.Message, 280)}");
        sb.AppendLine();
    }

    private static void AppendFailureModes(StringBuilder sb, QualityReport r)
    {
        if (r.SuspectedFailureModes.Count == 0 && r.RecommendedInvestigationAreas.Count == 0)
            return;

        if (r.SuspectedFailureModes.Count > 0)
        {
            sb.AppendLine("## Suspected Failure Modes");
            foreach (var f in r.SuspectedFailureModes)
                sb.AppendLine($"- `{f}`");
            sb.AppendLine();
        }

        if (r.RecommendedInvestigationAreas.Count > 0)
        {
            sb.AppendLine("## Suggested Investigation");
            foreach (var a in r.RecommendedInvestigationAreas)
                sb.AppendLine($"- {a}");
            sb.AppendLine();
        }
    }

    private static void AppendFailedUrls(StringBuilder sb, List<string> failedUrls)
    {
        if (failedUrls.Count == 0) return;

        sb.AppendLine($"## Top Failed URLs ({Math.Min(failedUrls.Count, 20)} of {failedUrls.Count})");
        foreach (var u in failedUrls.Take(20))
            sb.AppendLine($"- {u}");
        sb.AppendLine();
    }

    private static void AppendRejectedPatterns(StringBuilder sb, Dictionary<string, int> rejected)
    {
        if (rejected.Count == 0) return;

        sb.AppendLine("## Rejected Link Patterns (Top 20)");
        sb.AppendLine($"| Reason | Count |");
        sb.AppendLine($"|---|---|");
        foreach (var kv in rejected.OrderByDescending(x => x.Value).Take(20))
            sb.AppendLine($"| {kv.Key} | {kv.Value} |");
        sb.AppendLine();
    }

    private static void AppendNavSummary(StringBuilder sb, QualityReport r)
    {
        sb.AppendLine("## Navigation Selection Summary");
        sb.AppendLine($"- Nav groups found: {r.Metrics.NavGroupCount}");
        sb.AppendLine($"- Visible groups found: {r.Metrics.VisibleGroupCount}");
        sb.AppendLine($"- Primary group selected: `{r.Metrics.PrimaryNavGroupId}`");
        if (string.IsNullOrWhiteSpace(r.Metrics.PrimaryNavGroupId))
            sb.AppendLine($"  - **No primary group** — crawl fell back to heuristic seeding.");
        sb.AppendLine();
    }

    private static void AppendComparisonSummary(StringBuilder sb, RunComparison? cmp)
    {
        if (cmp == null)
        {
            sb.AppendLine("## Before/After Comparison");
            sb.AppendLine("_No comparison available (this is the first or only run)._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("## Before/After Comparison");
        sb.AppendLine($"**Verdict: {cmp.OverallVerdict.ToUpperInvariant()}**");
        sb.AppendLine();

        if (cmp.Improvements.Count > 0)
        {
            sb.AppendLine("Improvements:");
            foreach (var i in cmp.Improvements) sb.AppendLine($"- {i}");
        }

        if (cmp.Regressions.Count > 0)
        {
            sb.AppendLine("Regressions:");
            foreach (var r in cmp.Regressions) sb.AppendLine($"- **{r}**");
        }

        if (cmp.RequiresHumanReview.Count > 0)
        {
            sb.AppendLine("Requires human review:");
            foreach (var r in cmp.RequiresHumanReview) sb.AppendLine($"- {r}");
        }

        sb.AppendLine();
    }

    private static void AppendRelevantFiles(StringBuilder sb, string siteDir)
    {
        sb.AppendLine("## Relevant Files");
        void AddIfExists(string file, string description)
        {
            var path = Path.Combine(siteDir, file);
            if (File.Exists(path))
                sb.AppendLine($"- `{path}` — {description}");
        }

        AddIfExists("nav.json", "crawled navigation structure");
        AddIfExists("telemetry.jsonl", "structured diagnostic events");
        AddIfExists("quality-report.json", "quality metrics and warnings");
        AddIfExists("comparison-report.json", "before/after comparison");

        var coreFiles = new[]
        {
            "NavCrawler.cs",
            "CmsAwareNavExtractor.cs",
            "LocalNavExtractor.cs",
            "CmsDetector.cs",
            "Snapshotter.cs",
            "TextExtractor.cs",
        };

        var projectRoot = Path.GetFullPath(Path.Combine(siteDir, "..\\..\\.."));
        sb.AppendLine();
        sb.AppendLine("Core scraper files likely relevant to this run:");
        foreach (var f in coreFiles)
        {
            var candidates = Directory.GetFiles(projectRoot, f, SearchOption.TopDirectoryOnly);
            foreach (var c in candidates)
                sb.AppendLine($"- `{c}`");
        }

        sb.AppendLine();
    }

    private static void AppendForbiddenAssumptions(StringBuilder sb)
    {
        sb.AppendLine("## Forbidden Assumptions");
        sb.AppendLine("Before proposing any change, you must read the evidence above. Then:");
        sb.AppendLine();
        sb.AppendLine("- Do **not** assume more pages means better.");
        sb.AppendLine("- Do **not** assume fewer pages means worse.");
        sb.AppendLine("- Do **not** patch CMS-specific logic based on one municipality unless telemetry supports it.");
        sb.AppendLine("- Do **not** remove existing extractor paths without regression checking.");
        sb.AppendLine("- Do **not** change output format unless required.");
        sb.AppendLine("- Do **not** make broad rewrites when a narrow change is possible.");
        sb.AppendLine("- Do **not** auto-accept your own code changes — always request a re-run and comparison.");
        sb.AppendLine("- Do **not** guess at CMS-specific behavior — read the telemetry signals.");
        sb.AppendLine();
        sb.AppendLine("**One narrow change. One re-run. One comparison. Then decide.**");
    }

    private static async Task<List<string>> LoadFailedUrlsAsync(string siteDir)
    {
        var telPath = Path.Combine(siteDir, "telemetry.jsonl");
        if (!File.Exists(telPath)) return new List<string>();

        var failed = new List<string>();
        try
        {
            var lines = await File.ReadAllLinesAsync(telPath, Encoding.UTF8);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("eventName", out var ev) &&
                        ev.GetString() == "page_visit_fail" &&
                        root.TryGetProperty("url", out var url))
                    {
                        var u = url.GetString();
                        if (!string.IsNullOrWhiteSpace(u))
                            failed.Add(u);
                    }
                }
                catch { }
            }
        }
        catch { }

        return failed;
    }

    private static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxChars)
            return value ?? "";

        var compact = value.Replace("\r", " ").Replace("\n", " ");
        return compact.Length <= maxChars ? compact : compact[..maxChars] + "...";
    }
}
