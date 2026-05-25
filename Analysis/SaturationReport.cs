using System.Text.Json.Serialization;

namespace WebSnapshots.Analysis;

public sealed class SaturationReport
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("uniqueTemplateFingerprints")]
    public int UniqueTemplateFingerprints { get; set; }

    [JsonPropertyName("uniquePathPatterns")]
    public int UniquePathPatterns { get; set; }

    [JsonPropertyName("uniqueRejectionReasons")]
    public int UniqueRejectionReasons { get; set; }

    [JsonPropertyName("uniqueErrorClasses")]
    public int UniqueErrorClasses { get; set; }

    [JsonPropertyName("uniqueContentTypes")]
    public int UniqueContentTypes { get; set; }

    [JsonPropertyName("newPatternsInLastWindow")]
    public int NewPatternsInLastWindow { get; set; }

    [JsonPropertyName("windowSize")]
    public int WindowSize { get; set; } = 50;

    [JsonPropertyName("saturated")]
    public bool Saturated { get; set; }

    [JsonPropertyName("saturationReason")]
    public string SaturationReason { get; set; } = "";

    /// <summary>
    /// Top template fingerprint hashes (up to 20), ordered by first-seen page.
    /// Used by AI review pack to show template coverage.
    /// </summary>
    [JsonPropertyName("topFingerprints")]
    public List<FingerprintSummary> TopFingerprints { get; set; } = new();
}

public sealed class FingerprintSummary
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("pathShape")]
    public string PathShape { get; set; } = "";

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("exampleUrl")]
    public string ExampleUrl { get; set; } = "";
}
