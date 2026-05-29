using System.Text.Json;
using Microsoft.Playwright;

var options = AuditOptions.Parse(args);
Directory.CreateDirectory(options.AuditDir);

var targets = DiscoverTargets(options).ToList();
if (targets.Count == 0)
{
    Console.Error.WriteLine("No viewer targets found.");
    return 2;
}

var chromium = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_SHELL");
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new()
{
    Headless = true,
    ExecutablePath = string.IsNullOrWhiteSpace(chromium) ? null : chromium
});

var indexRows = new List<object>();

foreach (var target in targets)
{
    var outDir = Path.Combine(options.AuditDir, target.Municipality);
    Directory.CreateDirectory(outDir);

    var page = await browser.NewPageAsync(new()
    {
        ViewportSize = new ViewportSize { Width = options.ViewportWidth, Height = options.ViewportHeight }
    });

    Console.WriteLine($"{target.Municipality}: {target.ViewerPath}");
    await page.GotoAsync("file:///" + target.ViewerPath.Replace('\\', '/'));
    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
    await page.WaitForTimeoutAsync(options.SettleMs);

    await ExpandEverything(page, options.SettleMs);

    var frame = page.Frame("view");
    if (frame is null)
    {
        await File.WriteAllTextAsync(Path.Combine(outDir, "ERROR.txt"), "Viewer iframe named 'view' was not found.");
        await page.CloseAsync();
        continue;
    }

    var navPositions = await GetNavScrollPositions(page, options.MaxNavSlices);
    var mainPositions = await GetMainScrollPositions(frame, options.MaxMainSlices);
    var rootTexts = await GetRootNavigationTexts(page);
    var allNavTexts = await GetAllVisibleNavTexts(page);

    await File.WriteAllLinesAsync(Path.Combine(outDir, "rendered-root-navigation.txt"), rootTexts);
    await File.WriteAllLinesAsync(Path.Combine(outDir, "expanded-visible-nav-text.txt"), allNavTexts);
    await File.WriteAllTextAsync(
        Path.Combine(outDir, "scroll-manifest.json"),
        JsonSerializer.Serialize(new
        {
            target.Municipality,
            target.Run,
            target.ViewerPath,
            options.ViewportWidth,
            options.ViewportHeight,
            options.MaxNavSlices,
            options.MaxMainSlices,
            navPositions,
            mainPositions,
            note = "Every screenshot is taken after expanding all nav tree nodes and helper sections. Filenames encode nav and main iframe scroll offsets."
        }, new JsonSerializerOptions { WriteIndented = true }));

    foreach (var main in mainPositions)
    {
        await ScrollMain(frame, main.Offset);
        await page.WaitForTimeoutAsync(options.SettleMs);

        foreach (var nav in navPositions)
        {
            await ScrollNav(page, nav.Offset);
            await page.WaitForTimeoutAsync(options.SettleMs);

            var fileName = $"nav-{nav.Index:D2}-y{nav.Offset}_main-{main.Index:D2}-y{main.Offset}.png";
            await page.ScreenshotAsync(new() { Path = Path.Combine(outDir, fileName), FullPage = false });
            Console.WriteLine($"{target.Municipality}: {fileName}");
        }
    }

    indexRows.Add(new
    {
        target.Municipality,
        target.Run,
        target.ViewerPath,
        ScreenshotCount = navPositions.Count * mainPositions.Count,
        RootCount = rootTexts.Count,
        NavSlices = navPositions.Count,
        MainSlices = mainPositions.Count
    });

    await page.CloseAsync();
}

await File.WriteAllTextAsync(
    Path.Combine(options.AuditDir, "capture-index.json"),
    JsonSerializer.Serialize(indexRows, new JsonSerializerOptions { WriteIndented = true }));

return 0;

static IEnumerable<Target> DiscoverTargets(AuditOptions options)
{
    var requested = options.Municipalities.Count == 0
        ? Directory.EnumerateDirectories(options.OutputDir)
            .Select(Path.GetFileName)
            .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("_", StringComparison.Ordinal))
            .ToList()!
        : options.Municipalities;

    foreach (var muni in requested)
    {
        var muniDir = Path.Combine(options.OutputDir, muni);
        if (!Directory.Exists(muniDir)) continue;

        var runDir = string.IsNullOrWhiteSpace(options.Run)
            ? Directory.EnumerateDirectories(muniDir)
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : Path.Combine(muniDir, options.Run);

        if (string.IsNullOrWhiteSpace(runDir) || !Directory.Exists(runDir)) continue;

        var viewer = Path.Combine(runDir, options.ViewerFile);
        if (!File.Exists(viewer)) continue;

        yield return new Target(muni, Path.GetFileName(runDir) ?? "", viewer);
    }
}

static async Task ExpandEverything(IPage page, int settleMs)
{
    var expand = page.Locator("#expandAll");
    if (await expand.CountAsync() == 1)
        await expand.ClickAsync();

    await page.WaitForTimeoutAsync(settleMs);

    await page.Locator("#navPane").EvaluateAsync(
        @"nav => {
            nav.querySelectorAll('ul.children').forEach(el => el.classList.remove('hidden'));
            nav.querySelectorAll('.sectionBody').forEach(el => el.classList.remove('sectionBody--hidden'));
            nav.querySelectorAll('.toggle').forEach(el => { if (!el.classList.contains('empty')) el.textContent = '-'; });
            nav.querySelectorAll('.sectionArrow').forEach(el => el.textContent = '\u25BE');
        }");
}

static async Task<List<ScrollPosition>> GetNavScrollPositions(IPage page, int maxSlices)
{
    var metrics = await page.Locator("#navPane").EvaluateAsync<ScrollMetrics>(
        "el => ({ scrollHeight: el.scrollHeight, clientHeight: el.clientHeight })");
    return BuildPositions(metrics.ScrollHeight, metrics.ClientHeight, maxSlices);
}

static async Task<List<ScrollPosition>> GetMainScrollPositions(IFrame frame, int maxSlices)
{
    var metrics = await frame.EvaluateAsync<ScrollMetrics>(
        "() => ({ scrollHeight: document.documentElement.scrollHeight, clientHeight: window.innerHeight })");
    return BuildPositions(metrics.ScrollHeight, metrics.ClientHeight, maxSlices);
}

static List<ScrollPosition> BuildPositions(int scrollHeight, int clientHeight, int maxSlices)
{
    var maxOffset = Math.Max(0, scrollHeight - clientHeight);
    if (maxOffset == 0)
        return new List<ScrollPosition> { new(0, 0) };

    maxSlices = Math.Max(2, maxSlices);
    var step = Math.Max(1, (int)Math.Ceiling(maxOffset / (double)(maxSlices - 1)));
    var offsets = new SortedSet<int> { 0, maxOffset };
    for (var y = step; y < maxOffset; y += step)
        offsets.Add(y);

    return offsets.Select((offset, i) => new ScrollPosition(i, offset)).ToList();
}

static Task ScrollNav(IPage page, int offset)
    => page.Locator("#navPane").EvaluateAsync("(el, y) => el.scrollTop = y", offset);

static Task ScrollMain(IFrame frame, int offset)
    => frame.EvaluateAsync("(y) => window.scrollTo(0, y)", offset);

static async Task<List<string>> GetRootNavigationTexts(IPage page)
{
    var texts = await page.Locator("#navPane > .tree .tree-root > .tree-item > .tree-row .tree-link").AllInnerTextsAsync();
    return texts.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
}

static async Task<List<string>> GetAllVisibleNavTexts(IPage page)
{
    var texts = await page.Locator("#navPane .tree-row .tree-link, #navPane .tree-row .group-link, #navPane .sectionTitle, #navPane .sectionToggle, #navPane .visibleGroupLabel").AllInnerTextsAsync();
    return texts.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
}

sealed record Target(string Municipality, string Run, string ViewerPath);
sealed record ScrollPosition(int Index, int Offset);

sealed class ScrollMetrics
{
    public int ScrollHeight { get; set; }
    public int ClientHeight { get; set; }
}

sealed class AuditOptions
{
    public string OutputDir { get; init; } = @"D:\WebSnapshots-main\output_precaution_run";
    public string AuditDir { get; init; } = "";
    public string ViewerFile { get; init; } = "viewer.htm";
    public string Run { get; init; } = "";
    public int ViewportWidth { get; init; } = 1800;
    public int ViewportHeight { get; init; } = 1100;
    public int MaxNavSlices { get; init; } = 10;
    public int MaxMainSlices { get; init; } = 6;
    public int SettleMs { get; init; } = 250;
    public List<string> Municipalities { get; init; } = new();

    public static AuditOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal)) continue;
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[key[2..]] = "true";
                continue;
            }
            values[key[2..]] = args[++i];
        }

        var outputDir = values.TryGetValue("output", out var output) ? output : @"D:\WebSnapshots-main\output_precaution_run";
        var auditDir = values.TryGetValue("audit", out var audit)
            ? audit
            : Path.Combine(outputDir, "_visual_quality_audit", "scroll-matrix");

        return new AuditOptions
        {
            OutputDir = outputDir,
            AuditDir = auditDir,
            ViewerFile = values.TryGetValue("viewer", out var viewer) ? viewer : "viewer.htm",
            Run = values.TryGetValue("run", out var run) ? run : "",
            ViewportWidth = ReadInt(values, "width", 1800),
            ViewportHeight = ReadInt(values, "height", 1100),
            MaxNavSlices = ReadInt(values, "max-nav-slices", 10),
            MaxMainSlices = ReadInt(values, "max-main-slices", 6),
            SettleMs = ReadInt(values, "settle-ms", 250),
            Municipalities = values.TryGetValue("municipalities", out var munis)
                ? munis.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : new List<string>()
        };
    }

    private static int ReadInt(Dictionary<string, string> values, string key, int fallback)
        => values.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed) ? parsed : fallback;
}
