using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebSnapshots.Diagnostic;

public sealed class CmsTarget
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("cms")]
    public string Cms { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = "";
}

public sealed class CmsTargetConfig
{
    [JsonPropertyName("targets")]
    public List<CmsTarget> Targets { get; set; } = new();

    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<CmsTargetConfig> LoadAsync(string? configPath = null)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(configPath))
            candidates.Add(configPath);

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "config", "cms-targets.json"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "config", "cms-targets.json"));

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<CmsTargetConfig>(json, _opts);
                if (cfg != null)
                {
                    Console.WriteLine($"[TARGETS] Loaded {cfg.Targets.Count} targets from {path}");
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TARGETS] Failed to load {path}: {ex.Message}");
            }
        }

        Console.Error.WriteLine("[TARGETS] No cms-targets.json found in expected locations.");
        return new CmsTargetConfig();
    }

    public CmsTarget? FindById(string id)
        => Targets.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}
