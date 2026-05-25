using System.Text.Json.Serialization;

namespace WebSnapshots.Telemetry;

public sealed class TelemetryEvent
{
    [JsonPropertyName("timestampUtc")]
    public string TimestampUtc { get; set; } = "";

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = "";

    [JsonPropertyName("municipality")]
    public string Municipality { get; set; } = "";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "";

    [JsonPropertyName("eventName")]
    public string EventName { get; set; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "info";

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("fields")]
    public Dictionary<string, object?>? Fields { get; set; }
}
