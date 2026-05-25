using System.Text;
using System.Text.Json;

namespace WebSnapshots.Analysis;

public static class ComparisonReportWriter
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    public static async Task WriteAsync(string outputDir, RunComparison cmp)
    {
        var jsonPath = Path.Combine(outputDir, "comparison-report.json");
        var mdPath = Path.Combine(outputDir, "comparison-report.md");

        await File.WriteAllTextAsync(jsonPath,
            JsonSerializer.Serialize(cmp, _jsonOpts),
            Encoding.UTF8);

        await File.WriteAllTextAsync(mdPath,
            RenderMarkdown(cmp),
            Encoding.UTF8);
    }

    private static string RenderMarkdown(RunComparison cmp)
    {
        var sb = new StringBuilder();

        var verdictEmoji = cmp.OverallVerdict switch
        {
            "improved" => "IMPROVED",
            "regressed" => "REGRESSED",
            "requires_human_review" => "REQUIRES HUMAN REVIEW",
            _ => "UNCHANGED"
        };

        sb.AppendLine($"# Comparison Report: {cmp.Municipality} ({cmp.TargetId})");
        sb.AppendLine();
        sb.AppendLine($"**Overall verdict: {verdictEmoji}**");
        sb.AppendLine();
        sb.AppendLine($"| Field | Value |");
        sb.AppendLine($"|---|---|");
        sb.AppendLine($"| Previous run | `{cmp.PreviousRunId}` |");
        sb.AppendLine($"| Current run | `{cmp.CurrentRunId}` |");
        if (!string.IsNullOrWhiteSpace(cmp.DiagnosticProfile))
            sb.AppendLine($"| Profile | `{cmp.DiagnosticProfile}` |");
        sb.AppendLine($"| Generated | {cmp.GeneratedUtc} |");
        sb.AppendLine();

        if (cmp.CmsChanged)
        {
            sb.AppendLine("## CMS Change");
            sb.AppendLine($"> **CMS changed from `{cmp.PreviousCms}` to `{cmp.CurrentCms}`** — requires human review.");
            sb.AppendLine();
        }

        if (cmp.PrimaryNavGroupChanged)
        {
            sb.AppendLine("## Primary Nav Group Change");
            sb.AppendLine($"> **Primary nav group changed from `{cmp.PreviousPrimaryNavGroup}` to `{cmp.CurrentPrimaryNavGroup}`** — requires human review.");
            sb.AppendLine();
        }

        sb.AppendLine("## Metric Deltas");
        sb.AppendLine($"| Metric | Previous | Current | Delta | Verdict |");
        sb.AppendLine($"|---|---|---|---|---|");
        foreach (var d in cmp.MetricDeltas)
        {
            var verdict = d.Verdict switch
            {
                "improved" => "improved",
                "regressed" => "**regressed**",
                _ => "unchanged"
            };
            sb.AppendLine($"| {d.Metric} | {d.Previous} | {d.Current} | {FormatDelta(d.Delta)} | {verdict} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Saturation");
        sb.AppendLine($"| Field | Previous | Current |");
        sb.AppendLine($"|---|---:|---:|");
        sb.AppendLine($"| Saturated | `{cmp.PreviousSaturated}` | `{cmp.CurrentSaturated}` |");
        sb.AppendLine($"| Unique template fingerprints | {cmp.PreviousUniqueTemplateFingerprints} | {cmp.CurrentUniqueTemplateFingerprints} |");
        sb.AppendLine($"| New patterns in last window | {cmp.PreviousNewPatternsInLastWindow} | {cmp.CurrentNewPatternsInLastWindow} |");
        if (!string.IsNullOrWhiteSpace(cmp.SaturationNote))
            sb.AppendLine($"| Note |  | {cmp.SaturationNote} |");
        sb.AppendLine();

        if (cmp.PagesByDepthDelta.Count > 0)
        {
            sb.AppendLine("## Pages By Depth");
            sb.AppendLine($"| Depth | Previous | Current | Delta |");
            sb.AppendLine($"|---|---:|---:|---:|");
            foreach (var kv in cmp.PagesByDepthDelta.OrderBy(x => ParseDepthKey(x.Key)))
                sb.AppendLine($"| {kv.Key} | {kv.Value.Previous} | {kv.Value.Current} | {FormatDelta(kv.Value.Delta)} |");
            sb.AppendLine();
        }

        if (cmp.RejectedLinksByReasonDelta.Count > 0)
        {
            sb.AppendLine("## Rejected Links By Reason");
            sb.AppendLine($"| Reason | Previous | Current | Delta |");
            sb.AppendLine($"|---|---:|---:|---:|");
            foreach (var kv in cmp.RejectedLinksByReasonDelta.OrderByDescending(x => DeltaMagnitude(x.Value)))
                sb.AppendLine($"| {kv.Key} | {kv.Value.Previous} | {kv.Value.Current} | {FormatDelta(kv.Value.Delta)} |");
            sb.AppendLine();
        }

        if (cmp.ChangedParentRelationshipCount > 0)
        {
            sb.AppendLine($"## Changed Parent Relationships ({cmp.ChangedParentRelationshipCount})");
            foreach (var ch in cmp.ChangedParentRelationships.Take(30))
                sb.AppendLine($"- `{ch.Url}`: `{ch.PreviousParentUrl}` → `{ch.CurrentParentUrl}`");
            if (cmp.ChangedParentRelationshipCount > 30)
                sb.AppendLine($"- _(and {cmp.ChangedParentRelationshipCount - 30} more)_");
            sb.AppendLine();
        }

        if (cmp.Improvements.Count > 0)
        {
            sb.AppendLine("## Improvements");
            foreach (var i in cmp.Improvements)
                sb.AppendLine($"- {i}");
            sb.AppendLine();
        }

        if (cmp.Regressions.Count > 0)
        {
            sb.AppendLine("## Regressions");
            foreach (var r in cmp.Regressions)
                sb.AppendLine($"- **{r}**");
            sb.AppendLine();
        }

        if (cmp.RequiresHumanReview.Count > 0)
        {
            sb.AppendLine("## Requires Human Review");
            foreach (var r in cmp.RequiresHumanReview)
                sb.AppendLine($"- {r}");
            sb.AppendLine();
        }

        if (cmp.UrlsAdded.Count > 0)
        {
            sb.AppendLine($"## URLs Added ({cmp.UrlsAdded.Count})");
            foreach (var u in cmp.UrlsAdded.Take(30))
                sb.AppendLine($"- {u}");
            if (cmp.UrlsAdded.Count > 30) sb.AppendLine($"- _(and {cmp.UrlsAdded.Count - 30} more)_");
            sb.AppendLine();
        }

        if (cmp.UrlsRemoved.Count > 0)
        {
            sb.AppendLine($"## URLs Removed ({cmp.UrlsRemoved.Count})");
            foreach (var u in cmp.UrlsRemoved.Take(30))
                sb.AppendLine($"- {u}");
            if (cmp.UrlsRemoved.Count > 30) sb.AppendLine($"- _(and {cmp.UrlsRemoved.Count - 30} more)_");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatDelta(object? delta)
    {
        if (delta == null) return "—";
        if (delta is int i) return i > 0 ? $"+{i}" : i.ToString();
        if (delta is long l) return l > 0 ? $"+{l}" : l.ToString();
        if (delta is double d) return d > 0 ? $"+{d:F1}" : $"{d:F1}";
        return delta.ToString() ?? "—";
    }

    private static int ParseDepthKey(string key)
        => int.TryParse(key, out var i) ? i : int.MaxValue;

    private static int DeltaMagnitude(MetricDelta delta)
    {
        if (delta.Delta is int i) return Math.Abs(i);
        if (int.TryParse(delta.Delta?.ToString(), out var parsed)) return Math.Abs(parsed);
        return 0;
    }
}
