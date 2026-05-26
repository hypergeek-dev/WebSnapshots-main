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

    // Root-child quality metrics — a high deepRootChildCount indicates
    // that leaf pages are being dumped at root due to missing intermediate parents.
    [JsonPropertyName("rootChildCount")]
    public int RootChildCount { get; set; }

    [JsonPropertyName("deepRootChildCount")]
    public int DeepRootChildCount { get; set; }

    [JsonPropertyName("syntheticParentCount")]
    public int SyntheticParentCount { get; set; }

    // Phase 7: homepage IA quality metrics ────────────────────────────────────

    // Number of homepage card/tile sections detected (from HomepageSections).
    [JsonPropertyName("homepageAnchoredSections")]
    public int HomepageAnchoredSections { get; set; }

    // Root children whose URL matches a detected homepage section.
    [JsonPropertyName("structuralRootChildren")]
    public int StructuralRootChildren { get; set; }

    // Root children tagged as utility/meta pages (Kontakt, Tillgänglighet, etc.).
    [JsonPropertyName("utilityRootChildren")]
    public int UtilityRootChildren { get; set; }

    [JsonPropertyName("rawUrlTitleRootChildren")]
    public int RawUrlTitleRootChildren { get; set; }

    [JsonPropertyName("syntheticRootChildren")]
    public int SyntheticRootChildren { get; set; }

    [JsonPropertyName("rootTopologyVerdict")]
    public string RootTopologyVerdict { get; set; } = "";

    // Root children that are neither structural (homepage section) nor utility.
    // These are "discovered" pages that could not be confidently attached to the IA.
    [JsonPropertyName("discoveredRootChildren")]
    public int DiscoveredRootChildren { get; set; }

    // Root children whose URL has path depth ≥ 2 AND no structural/utility classification
    // — these are leaf/article pages that leaked to root (a sub-category of discovered).
    [JsonPropertyName("deepLeafRootChildren")]
    public int DeepLeafRootChildren { get; set; }

    // Root children that have no children of their own (singletons at root).
    [JsonPropertyName("singletonRootChildren")]
    public int SingletonRootChildren { get; set; }
}
