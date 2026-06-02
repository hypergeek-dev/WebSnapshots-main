using System.Text.RegularExpressions;

namespace WebSnapshots;

public static class OutputDoctorReporter
{
    private static readonly Regex _hrefRegex = new(
        """href\s*=\s*["']([^"'#]+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Task<int> RunAsync(string outputDir)
    {
        outputDir = Path.GetFullPath(outputDir);
        if (!Directory.Exists(outputDir))
        {
            Console.Error.WriteLine($"[DOCTOR] Output folder not found: {outputDir}");
            return Task.FromResult(2);
        }

        var missingScrapeJson = new List<string>();
        var missingViewer = new List<string>();
        var emptyScrapeFolders = new List<string>();
        var tempFiles = Directory.EnumerateFiles(outputDir, "*.tmp", SearchOption.AllDirectories).ToList();
        var missingRunJson = new List<string>();
        var brokenIndexLinks = new List<string>();

        var runsDir = Path.Combine(outputDir, "_runs");
        if (Directory.Exists(runsDir))
        {
            foreach (var runDir in Directory.EnumerateDirectories(runsDir))
            {
                if (!File.Exists(Path.Combine(runDir, "run.json")))
                    missingRunJson.Add(runDir);
            }
        }

        foreach (var municipalityDir in Directory.EnumerateDirectories(outputDir)
                     .Where(x => !Path.GetFileName(x).StartsWith("_", StringComparison.Ordinal)))
        {
            foreach (var scrapeDir in Directory.EnumerateDirectories(municipalityDir))
            {
                if (!Directory.EnumerateFileSystemEntries(scrapeDir).Any())
                    emptyScrapeFolders.Add(scrapeDir);
                if (!File.Exists(Path.Combine(scrapeDir, "scrape.json")))
                    missingScrapeJson.Add(scrapeDir);
                if (!File.Exists(Path.Combine(scrapeDir, "viewer.htm")))
                    missingViewer.Add(scrapeDir);
            }
        }

        foreach (var indexPath in Directory.EnumerateFiles(outputDir, "index.htm", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(outputDir, "index.html", SearchOption.AllDirectories)))
        {
            CheckIndexLinks(indexPath, brokenIndexLinks);
        }

        Console.WriteLine("[DOCTOR] WebSnapshots output report");
        Console.WriteLine($"[DOCTOR] Root: {outputDir}");
        Console.WriteLine($"[DOCTOR] Size: {FormatBytes(Utils.GetDirectorySizeBytes(outputDir))}");
        Console.WriteLine();
        Print("Scrape folders missing scrape.json", missingScrapeJson);
        Print("Scrape folders missing viewer.htm", missingViewer);
        Print("Empty scrape folders", emptyScrapeFolders);
        Print("Temporary .tmp leftovers", tempFiles);
        Print("_runs folders missing run.json", missingRunJson);
        Print("Broken local links from index files", brokenIndexLinks);

        var issueCount = missingScrapeJson.Count + missingViewer.Count + emptyScrapeFolders.Count
            + tempFiles.Count + missingRunJson.Count + brokenIndexLinks.Count;
        Console.WriteLine();
        Console.WriteLine($"[DOCTOR] Findings: {issueCount}");
        Console.WriteLine("[DOCTOR] Report only. No files were changed or deleted.");
        return Task.FromResult(issueCount == 0 ? 0 : 1);
    }

    private static void CheckIndexLinks(string indexPath, List<string> broken)
    {
        try
        {
            var html = File.ReadAllText(indexPath);
            foreach (Match match in _hrefRegex.Matches(html))
            {
                var href = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
                if (href.Length == 0
                    || href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                    || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    continue;

                var pathPart = href.Split('#', '?')[0].Replace('/', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(indexPath) ?? ".", pathPart));
                if (!File.Exists(target) && !Directory.Exists(target))
                    broken.Add($"{indexPath} -> {href}");
            }
        }
        catch (Exception ex)
        {
            broken.Add($"{indexPath} -> could not inspect links: {ex.Message}");
        }
    }

    private static void Print(string title, List<string> items)
    {
        Console.WriteLine($"[DOCTOR] {title}: {items.Count}");
        foreach (var item in items.Take(100))
            Console.WriteLine("  - " + item);
        if (items.Count > 100)
            Console.WriteLine($"  ... {items.Count - 100} more");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
