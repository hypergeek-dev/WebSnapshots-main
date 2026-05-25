using System.Text.Json.Serialization;

namespace WebSnapshots.Analysis;

public sealed class QualityReport
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = "";

    [JsonPropertyName("municipality")]
    public string Municipality { get; set; } = "";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("startUrl")]
    public string StartUrl { get; set; } = "";

    [JsonPropertyName("expectedCms")]
    public string ExpectedCms { get; set; } = "";

    [JsonPropertyName("detectedCms")]
    public string DetectedCms { get; set; } = "";

    [JsonPropertyName("cmsConfidence")]
    public string CmsConfidence { get; set; } = "";

    [JsonPropertyName("cmsMismatch")]
    public bool CmsMismatch { get; set; }

    [JsonPropertyName("generatedUtc")]
    public string GeneratedUtc { get; set; } = "";

    [JsonPropertyName("diagnosticProfile")]
    public string DiagnosticProfile { get; set; } = "";

    [JsonPropertyName("config")]
    public Dictionary<string, object?> Config { get; set; } = new();

    [JsonPropertyName("metrics")]
    public QualityMetrics Metrics { get; set; } = new();

    [JsonPropertyName("saturation")]
    public SaturationReport Saturation { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<QualityWarning> Warnings { get; set; } = new();

    [JsonPropertyName("suspectedFailureModes")]
    public List<string> SuspectedFailureModes { get; set; } = new();

    [JsonPropertyName("recommendedInvestigationAreas")]
    public List<string> RecommendedInvestigationAreas { get; set; } = new();
}
