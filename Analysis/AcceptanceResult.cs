using System.Text.Json.Serialization;

namespace WebSnapshots.Analysis;

public sealed class AcceptanceResult
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("decision")]
    public string Decision { get; set; } = "requires_human_review";

    [JsonPropertyName("reasons")]
    public List<string> Reasons { get; set; } = new();

    [JsonPropertyName("improvements")]
    public List<string> Improvements { get; set; } = new();

    [JsonPropertyName("regressions")]
    public List<string> Regressions { get; set; } = new();
}
