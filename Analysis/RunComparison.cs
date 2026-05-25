using System.Text.Json.Serialization;

namespace WebSnapshots.Analysis;

public sealed class RunComparison
{
    [JsonPropertyName("previousRunId")]
    public string PreviousRunId { get; set; } = "";

    [JsonPropertyName("currentRunId")]
    public string CurrentRunId { get; set; } = "";

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = "";

    [JsonPropertyName("municipality")]
    public string Municipality { get; set; } = "";

    [JsonPropertyName("generatedUtc")]
    public string GeneratedUtc { get; set; } = "";

    [JsonPropertyName("metricDeltas")]
    public List<MetricDelta> MetricDeltas { get; set; } = new();

    [JsonPropertyName("urlsAdded")]
    public List<string> UrlsAdded { get; set; } = new();

    [JsonPropertyName("urlsRemoved")]
    public List<string> UrlsRemoved { get; set; } = new();

    [JsonPropertyName("cmsChanged")]
    public bool CmsChanged { get; set; }

    [JsonPropertyName("previousCms")]
    public string PreviousCms { get; set; } = "";

    [JsonPropertyName("currentCms")]
    public string CurrentCms { get; set; } = "";

    [JsonPropertyName("primaryNavGroupChanged")]
    public bool PrimaryNavGroupChanged { get; set; }

    [JsonPropertyName("previousPrimaryNavGroup")]
    public string PreviousPrimaryNavGroup { get; set; } = "";

    [JsonPropertyName("currentPrimaryNavGroup")]
    public string CurrentPrimaryNavGroup { get; set; } = "";

    [JsonPropertyName("diagnosticProfile")]
    public string DiagnosticProfile { get; set; } = "";

    [JsonPropertyName("previousSaturated")]
    public bool PreviousSaturated { get; set; }

    [JsonPropertyName("currentSaturated")]
    public bool CurrentSaturated { get; set; }

    [JsonPropertyName("previousUniqueTemplateFingerprints")]
    public int PreviousUniqueTemplateFingerprints { get; set; }

    [JsonPropertyName("currentUniqueTemplateFingerprints")]
    public int CurrentUniqueTemplateFingerprints { get; set; }

    [JsonPropertyName("previousNewPatternsInLastWindow")]
    public int PreviousNewPatternsInLastWindow { get; set; }

    [JsonPropertyName("currentNewPatternsInLastWindow")]
    public int CurrentNewPatternsInLastWindow { get; set; }

    [JsonPropertyName("newPatternCount")]
    public int NewPatternCount { get; set; }

    [JsonPropertyName("removedPatternCount")]
    public int RemovedPatternCount { get; set; }

    [JsonPropertyName("changedPatternCount")]
    public int ChangedPatternCount { get; set; }

    [JsonPropertyName("saturationNote")]
    public string SaturationNote { get; set; } = "";

    [JsonPropertyName("pagesByDepthDelta")]
    public Dictionary<string, MetricDelta> PagesByDepthDelta { get; set; } = new();

    [JsonPropertyName("rejectedLinksByReasonDelta")]
    public Dictionary<string, MetricDelta> RejectedLinksByReasonDelta { get; set; } = new();

    [JsonPropertyName("changedParentRelationships")]
    public List<ParentRelationshipChange> ChangedParentRelationships { get; set; } = new();

    [JsonPropertyName("changedParentRelationshipCount")]
    public int ChangedParentRelationshipCount { get; set; }

    [JsonPropertyName("previousQualityErrors")]
    public List<string> PreviousQualityErrors { get; set; } = new();

    [JsonPropertyName("currentQualityErrors")]
    public List<string> CurrentQualityErrors { get; set; } = new();

    [JsonPropertyName("previousQualityWarnings")]
    public List<string> PreviousQualityWarnings { get; set; } = new();

    [JsonPropertyName("currentQualityWarnings")]
    public List<string> CurrentQualityWarnings { get; set; } = new();

    [JsonPropertyName("overallVerdict")]
    public string OverallVerdict { get; set; } = "unchanged";

    [JsonPropertyName("improvements")]
    public List<string> Improvements { get; set; } = new();

    [JsonPropertyName("regressions")]
    public List<string> Regressions { get; set; } = new();

    [JsonPropertyName("requiresHumanReview")]
    public List<string> RequiresHumanReview { get; set; } = new();
}

public sealed class MetricDelta
{
    [JsonPropertyName("metric")]
    public string Metric { get; set; } = "";

    [JsonPropertyName("previous")]
    public object? Previous { get; set; }

    [JsonPropertyName("current")]
    public object? Current { get; set; }

    [JsonPropertyName("delta")]
    public object? Delta { get; set; }

    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = "unchanged";
}

public sealed class ParentRelationshipChange
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("previousParentUrl")]
    public string PreviousParentUrl { get; set; } = "";

    [JsonPropertyName("currentParentUrl")]
    public string CurrentParentUrl { get; set; } = "";
}
