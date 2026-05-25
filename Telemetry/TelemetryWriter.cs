using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebSnapshots.Telemetry;

/// <summary>
/// Thread-safe JSONL writer for structured diagnostic telemetry.
/// Separate from the human-readable Logger — this output is for analysis tools.
/// </summary>
public sealed class TelemetryWriter : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string RunId { get; }
    public string TargetId { get; }
    public string Municipality { get; }
    public string Host { get; }
    public string FilePath { get; }

    public TelemetryWriter(
        string filePath,
        string runId,
        string targetId,
        string municipality,
        string host)
    {
        FilePath = filePath;
        RunId = runId;
        TargetId = targetId;
        Municipality = municipality;
        Host = host;

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        _writer = new StreamWriter(
            File.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
            NewLine = "\n"
        };
    }

    public void Emit(
        TelemetryPhase phase,
        string eventName,
        TelemetrySeverity severity = TelemetrySeverity.Info,
        string? url = null,
        Dictionary<string, object?>? fields = null)
    {
        var evt = new TelemetryEvent
        {
            TimestampUtc = DateTime.UtcNow.ToString("o"),
            RunId = RunId,
            TargetId = TargetId,
            Municipality = Municipality,
            Host = Host,
            Phase = PhaseToString(phase),
            EventName = eventName,
            Severity = SeverityToString(severity),
            Url = url,
            Fields = fields
        };

        var line = JsonSerializer.Serialize(evt, _opts);

        lock (_lock)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }

    internal static string PhaseToString(TelemetryPhase phase) => phase switch
    {
        TelemetryPhase.Run => "run",
        TelemetryPhase.CmsDetection => "cms_detection",
        TelemetryPhase.NavStartExtraction => "nav_start_extraction",
        TelemetryPhase.Crawl => "crawl",
        TelemetryPhase.LinkFiltering => "link_filtering",
        TelemetryPhase.TreeBuilding => "tree_building",
        TelemetryPhase.Snapshot => "snapshot",
        TelemetryPhase.TextExtract => "text_extract",
        TelemetryPhase.ViewerBuild => "viewer_build",
        TelemetryPhase.QualityAnalysis => "quality_analysis",
        TelemetryPhase.Comparison => "comparison",
        _ => phase.ToString().ToLowerInvariant()
    };

    internal static string SeverityToString(TelemetrySeverity sev) => sev switch
    {
        TelemetrySeverity.Debug => "debug",
        TelemetrySeverity.Info => "info",
        TelemetrySeverity.Warning => "warning",
        TelemetrySeverity.Error => "error",
        TelemetrySeverity.Critical => "critical",
        _ => "info"
    };
}
