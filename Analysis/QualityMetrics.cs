using System.Text.Json.Serialization;

namespace WebSnapshots.Analysis;

public sealed class QualityMetrics
{
    [JsonPropertyName("flatPageCount")]
    public int FlatPageCount { get; set; }

    [JsonPropertyName("treeNodeCount")]
    public int TreeNodeCount { get; set; }

    [JsonPropertyName("navGroupCount")]
    public int NavGroupCount { get; set; }

    [JsonPropertyName("visibleGroupCount")]
    public int VisibleGroupCount { get; set; }

    [JsonPropertyName("primaryNavGroupId")]
    public string PrimaryNavGroupId { get; set; } = "";

    [JsonPropertyName("orphanPageCount")]
    public int OrphanPageCount { get; set; }

    [JsonPropertyName("duplicateUrlCount")]
    public int DuplicateUrlCount { get; set; }

    [JsonPropertyName("duplicateTitleCount")]
    public int DuplicateTitleCount { get; set; }

    [JsonPropertyName("emptyTitleCount")]
    public int EmptyTitleCount { get; set; }

    [JsonPropertyName("failedVisitCount")]
    public int FailedVisitCount { get; set; }

    [JsonPropertyName("timeoutCount")]
    public int TimeoutCount { get; set; }

    [JsonPropertyName("snapshotFailCount")]
    public int SnapshotFailCount { get; set; }

    [JsonPropertyName("textExtractFailCount")]
    public int TextExtractFailCount { get; set; }

    [JsonPropertyName("averageOutLinksPerPage")]
    public double AverageOutLinksPerPage { get; set; }

    [JsonPropertyName("maxDepthReached")]
    public int MaxDepthReached { get; set; }

    [JsonPropertyName("maxPagesReached")]
    public bool MaxPagesReached { get; set; }

    [JsonPropertyName("pagesCaptured")]
    public int PagesCaptured { get; set; }

    [JsonPropertyName("pagesByDepth")]
    public Dictionary<string, int> PagesByDepth { get; set; } = new();

    [JsonPropertyName("rejectedLinksByReason")]
    public Dictionary<string, int> RejectedLinksByReason { get; set; } = new();

    [JsonPropertyName("structuralFallbackUsed")]
    public bool StructuralFallbackUsed { get; set; }

    [JsonPropertyName("structuralFallbackReason")]
    public string StructuralFallbackReason { get; set; } = "";
}
