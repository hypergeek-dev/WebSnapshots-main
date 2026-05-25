using System.Text.Json.Serialization;

namespace WebSnapshots.Analysis;

public sealed class QualityWarning
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "warning";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("value")]
    public object? Value { get; set; }

    [JsonPropertyName("threshold")]
    public object? Threshold { get; set; }

    public static QualityWarning Warn(string code, string message, object? value = null, object? threshold = null)
        => new() { Code = code, Severity = "warning", Message = message, Value = value, Threshold = threshold };

    public static QualityWarning Error(string code, string message, object? value = null, object? threshold = null)
        => new() { Code = code, Severity = "error", Message = message, Value = value, Threshold = threshold };

    public static QualityWarning Info(string code, string message, object? value = null, object? threshold = null)
        => new() { Code = code, Severity = "info", Message = message, Value = value, Threshold = threshold };
}
