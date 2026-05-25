using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebSnapshots.Diagnostic;

public sealed class DiagnosticProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "nav-diagnostic";

    [JsonPropertyName("maxDepth")]
    public int MaxDepth { get; set; } = 3;

    [JsonPropertyName("maxPages")]
    public int MaxPages { get; set; } = 200;

    [JsonPropertyName("maxChildrenPerPage")]
    public int MaxChildrenPerPage { get; set; } = 20;

    [JsonPropertyName("maxPagesPerDepth")]
    public int MaxPagesPerDepth { get; set; } = 70;

    [JsonPropertyName("captureSnapshots")]
    public bool CaptureSnapshots { get; set; } = false;

    [JsonPropertyName("extractText")]
    public bool ExtractText { get; set; } = true;

    [JsonPropertyName("buildViewer")]
    public bool BuildViewer { get; set; } = true;

    [JsonPropertyName("telemetry")]
    public bool Telemetry { get; set; } = true;

    [JsonPropertyName("qualityReport")]
    public bool QualityReport { get; set; } = true;

    [JsonPropertyName("stopWhenSaturated")]
    public bool StopWhenSaturated { get; set; } = false;

    [JsonPropertyName("saturationWindowSize")]
    public int SaturationWindowSize { get; set; } = 50;

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

public sealed class DiagnosticProfileConfig
{
    [JsonPropertyName("profiles")]
    public List<DiagnosticProfile> Profiles { get; set; } = new();

    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<DiagnosticProfileConfig> LoadAsync(string? configPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configPath)) candidates.Add(configPath);
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "config", "diagnostic-profiles.json"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "config", "diagnostic-profiles.json"));

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<DiagnosticProfileConfig>(json, _opts);
                if (cfg?.Profiles.Count > 0)
                {
                    Console.WriteLine($"[PROFILES] Loaded {cfg.Profiles.Count} profiles from {path}");
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PROFILES] Failed to load {path}: {ex.Message}");
            }
        }

        Console.WriteLine("[PROFILES] Using built-in profile defaults.");
        return BuiltIn;
    }

    public DiagnosticProfile? FindByName(string name)
        => Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public DiagnosticProfile GetOrDefault(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? FindByName("nav-diagnostic") ?? BuiltIn.Profiles[2]
            : FindByName(name) ?? BuiltIn.FindByName(name) ?? BuiltIn.Profiles[2];

    public static readonly DiagnosticProfileConfig BuiltIn = new()
    {
        Profiles = new List<DiagnosticProfile>
        {
            new() {
                Name = "smoke",
                MaxDepth = 0, MaxPages = 1,
                MaxChildrenPerPage = 0, MaxPagesPerDepth = 0,
                CaptureSnapshots = false, ExtractText = false, BuildViewer = false,
                SaturationWindowSize = 10,
                Description = "Verify basic pipeline wiring."
            },
            new() {
                Name = "cms-diagnostic",
                MaxDepth = 1, MaxPages = 50,
                MaxChildrenPerPage = 15, MaxPagesPerDepth = 50,
                CaptureSnapshots = false, ExtractText = true, BuildViewer = true,
                SaturationWindowSize = 20,
                Description = "CMS detection and start-page navigation discovery."
            },
            new() {
                Name = "nav-diagnostic",
                MaxDepth = 3, MaxPages = 200,
                MaxChildrenPerPage = 20, MaxPagesPerDepth = 70,
                CaptureSnapshots = false, ExtractText = true, BuildViewer = true,
                SaturationWindowSize = 50,
                Description = "Main feedback-loop mode for navigation extraction quality."
            },
            new() {
                Name = "snapshot-diagnostic",
                MaxDepth = 2, MaxPages = 40,
                MaxChildrenPerPage = 10, MaxPagesPerDepth = 30,
                CaptureSnapshots = true, ExtractText = true, BuildViewer = true,
                SaturationWindowSize = 20,
                Description = "Small representative run for snapshot and text extraction."
            },
            new() {
                Name = "full-validation",
                MaxDepth = 3, MaxPages = 1500,
                MaxChildrenPerPage = 0, MaxPagesPerDepth = 0,
                CaptureSnapshots = true, ExtractText = true, BuildViewer = true,
                SaturationWindowSize = 100,
                Description = "Final confidence check after candidate improvement looks good."
            }
        }
    };
}
