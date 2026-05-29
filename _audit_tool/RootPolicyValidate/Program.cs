using System.Text.Json;
using System.Globalization;
using System.Text;
using Microsoft.Playwright;
using WebSnapshots;

var baseDir = @"D:\WebSnapshots-main\output_precaution_run";
var auditDir = Path.Combine(baseDir, "_visual_quality_audit", "policy-validation");
var screenshotDir = Path.Combine(auditDir, "screenshots");
Directory.CreateDirectory(screenshotDir);

var munis = new (string Name, string Run)[]
{
    ("Eslov", "260528"),
    ("Hassleholm", "260529"),
    ("Klippan", "260528"),
    ("Kristianstad", "260528"),
    ("Ystad", "260529")
};

var reportLines = new List<string>
{
    "# Municipal Root Policy Validation",
    "",
    "Source: existing `output_precaution_run` nav.json files; no scraping rerun.",
    ""
};
var validationFailures = new List<string>();

foreach (var muni in munis)
{
    var siteDir = Path.Combine(baseDir, muni.Name, muni.Run);
    var navPath = Path.Combine(siteDir, "nav.json");
    var nav = JsonSerializer.Deserialize<NavIndex>(
        await File.ReadAllTextAsync(navPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    var start = nav.StartUrl;
    var root = nav.Nodes.FirstOrDefault();
    var flatByKey = nav.Flat
        .Where(x => !string.IsNullOrWhiteSpace(x.Url))
        .GroupBy(x => Utils.NormalizeUrl(x.Url, true).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    var homepageKeys = (nav.HomepageSections ?? new List<HomepageSection>())
        .Select(x => Utils.NormalizeUrl(x.Url, true).ToLowerInvariant())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var primary = nav.NavGroups?
        .OrderBy(g => g.Rank)
        .ThenByDescending(g => g.LinkCount)
        .FirstOrDefault();
    var primaryKeys = (primary?.Flat ?? new List<NavItem>())
        .Where(x => !string.IsNullOrWhiteSpace(x.Url))
        .Select(x => Utils.NormalizeUrl(x.Url, true).ToLowerInvariant())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var before = root?.Children?.Select(x => x.Title).ToList() ?? new List<string>();
    var after = new List<string>();
    var demoted = new List<string>();

    foreach (var child in root?.Children ?? new List<NavNode>())
    {
        var key = Utils.NormalizeUrl(child.Url, true).ToLowerInvariant();
        flatByKey.TryGetValue(key, out var flatItem);
        var item = flatItem ?? new NavItem { Url = child.Url, Title = child.Title };
        var decision = MunicipalRootClassifier.Classify(item, new MunicipalRootContext
        {
            StartUrl = start,
            WasPrimaryNav = primaryKeys.Contains(key),
            WasAcceptedHomepageAnchor = homepageKeys.Contains(key),
            HadChildren = child.Children.Count > 0,
            SourceGroup = "validation_existing_nav",
            BeforeRootCount = before.Count
        });

        if (decision.IsEligible)
            after.Add(child.Title);
        else
            demoted.Add($"{child.Title} [{decision.KindName}: {string.Join(", ", decision.Reasons)}]");
    }

    reportLines.Add($"## {muni.Name}");
    reportLines.Add("");
    reportLines.Add("Before root NAVIGATION:");
    reportLines.AddRange(before.Select(x => "- " + x));
    reportLines.Add("");
    reportLines.Add("After root NAVIGATION:");
    reportLines.AddRange(after.Select(x => "- " + x));
    reportLines.Add("");
    reportLines.Add("Demoted:");
    reportLines.AddRange(demoted.Count == 0 ? new[] { "- none" } : demoted.Select(x => "- " + x));
    reportLines.Add("");

    var fixtureFailures = ValidateFixture(muni.Name, after);
    if (fixtureFailures.Count > 0)
    {
        validationFailures.AddRange(fixtureFailures);
        reportLines.Add("Fixture failures:");
        reportLines.AddRange(fixtureFailures.Select(x => "- " + x));
        reportLines.Add("");
    }

    var builder = new SiteViewerBuilder(new SnapshotConfig { DropQueryStrings = true });
    await builder.BuildAsync(siteDir, nav.Host, nav.StartUrl, "viewer_policy_validation.htm");
}

if (validationFailures.Count > 0)
{
    reportLines.Add("## Validation failures");
    reportLines.Add("");
    reportLines.AddRange(validationFailures.Select(x => "- " + x));
    reportLines.Add("");
}

await File.WriteAllLinesAsync(Path.Combine(auditDir, "policy-validation-report.md"), reportLines);

var chromium = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_SHELL");
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new()
{
    Headless = true,
    ExecutablePath = string.IsNullOrWhiteSpace(chromium) ? null : chromium
});

foreach (var muni in munis)
{
    var siteDir = Path.Combine(baseDir, muni.Name, muni.Run);
    var outDir = Path.Combine(screenshotDir, muni.Name);
    Directory.CreateDirectory(outDir);
    var viewer = Path.Combine(siteDir, "viewer_policy_validation.htm");
    var page = await browser.NewPageAsync(new() { ViewportSize = new ViewportSize { Width = 1800, Height = 1100 } });
    await page.GotoAsync("file:///" + viewer.Replace('\\', '/'));
    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
    await page.WaitForTimeoutAsync(700);
    await page.ScreenshotAsync(new() { Path = Path.Combine(outDir, "root-collapsed.png"), FullPage = false });
    await page.Locator("#expandAll").ClickAsync();
    await page.WaitForTimeoutAsync(300);
    await page.ScreenshotAsync(new() { Path = Path.Combine(outDir, "root-expanded-top.png"), FullPage = false });
    await page.Locator("#navPane").EvaluateAsync("el => el.scrollTop = el.scrollHeight");
    await page.WaitForTimeoutAsync(300);
    await page.ScreenshotAsync(new() { Path = Path.Combine(outDir, "root-expanded-bottom.png"), FullPage = false });
    await page.CloseAsync();
}

return validationFailures.Count == 0 ? 0 : 1;

static List<string> ValidateFixture(string municipality, IReadOnlyList<string> afterRoots)
{
    var expectations = new Dictionary<string, FixtureExpectation>(StringComparer.OrdinalIgnoreCase)
    {
        ["Eslov"] = new(
            Required: new[] { "forskola", "omsorg", "uppleva", "bygga", "trafik", "arbete", "kommun" },
            Forbidden: new[] { "for elever", "sidan hittades inte", "psidata", "subvention", "anslag", "driftsinformation" }),
        ["Hassleholm"] = new(
            Required: new[] { "utbildning", "omsorg", "bygga", "uppleva", "trafik", "kommun", "naringsliv", "anslagstavla", "jobb" },
            Forbidden: new[] { "home" }),
        ["Klippan"] = new(
            Required: new[] { "utbildning", "omsorg", "bygga", "uppleva", "trafik", "naringsliv", "kommun" },
            Forbidden: Array.Empty<string>()),
        ["Kristianstad"] = new(
            Required: new[] { "barn", "omsorg", "uppleva", "bygga", "trafik", "jobb", "kommun" },
            Forbidden: new[] { "badrike" }),
        ["Ystad"] = new(
            Required: new[] { "forskola", "omsorg", "samhallsutveckling", "bygga", "kommun", "uppleva", "naringsliv" },
            Forbidden: new[] { "gymnasium", "industrifastigheter" })
    };

    if (!expectations.TryGetValue(municipality, out var expectation))
        return new List<string>();

    var normalized = afterRoots.Select(NormalizeForExpectation).ToList();
    var failures = new List<string>();

    foreach (var required in expectation.Required)
    {
        if (!normalized.Any(x => x.Contains(required, StringComparison.OrdinalIgnoreCase)))
            failures.Add($"{municipality}: expected root containing `{required}`");
    }

    foreach (var forbidden in expectation.Forbidden)
    {
        if (normalized.Any(x => x.Contains(forbidden, StringComparison.OrdinalIgnoreCase)))
            failures.Add($"{municipality}: forbidden root containing `{forbidden}`");
    }

    return failures;
}

static string NormalizeForExpectation(string text)
{
    var normalized = (text ?? "").Normalize(NormalizationForm.FormD);
    var sb = new StringBuilder(normalized.Length);
    foreach (var ch in normalized)
    {
        if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            sb.Append(ch);
    }
    return sb.ToString()
        .Normalize(NormalizationForm.FormC)
        .ToLowerInvariant();
}

sealed record FixtureExpectation(string[] Required, string[] Forbidden);
