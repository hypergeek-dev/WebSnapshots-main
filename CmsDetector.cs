using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace WebSnapshots;

public enum CmsKind
{
    Unknown = 0,
    SiteVision,
    WordPress,
    Drupal,
    Umbraco,
    Optimizely,
    Sitecore
}

public sealed class CmsDetectionResult
{
    public CmsKind Kind { get; set; } = CmsKind.Unknown;
    public string Confidence { get; set; } = "low";
    public List<string> Signals { get; set; } = new();
}

public static class CmsDetector
{
    private static readonly Regex _generatorRx = new(
        @"<meta\s[^>]*name=[""']generator[""'][^>]*content=[""']([^""']+)[""']|" +
        @"<meta\s[^>]*content=[""']([^""']+)[""'][^>]*name=[""']generator[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<CmsDetectionResult> DetectAsync(IPage page)
    {
        var html = await page.ContentAsync();
        var lower = html.ToLowerInvariant();

        bool Has(string s) => lower.Contains(s, StringComparison.Ordinal);

        // Meta generator tag gives a reliable single-signal identification
        var generatorContent = "";
        var genMatch = _generatorRx.Match(html);
        if (genMatch.Success)
            generatorContent = (genMatch.Groups[1].Value + genMatch.Groups[2].Value).ToLowerInvariant();

        // ── WordPress ─────────────────────────────────────────────────────────
        // Require path-based signals or generator tag — never fire on "wp-" alone.
        var wpSignals = new List<string>();
        if (Has("/wp-content/"))  wpSignals.Add("/wp-content/");
        if (Has("/wp-includes/")) wpSignals.Add("/wp-includes/");
        if (Has("wp-json"))       wpSignals.Add("wp-json");
        if (generatorContent.Contains("wordpress")) wpSignals.Add("meta:generator=WordPress");

        if (wpSignals.Count >= 1)
        {
            return new CmsDetectionResult
            {
                Kind = CmsKind.WordPress,
                Confidence = wpSignals.Count >= 2 ? "high" : "medium",
                Signals = wpSignals
            };
        }

        // ── SiteVision ────────────────────────────────────────────────────────
        var svSignals = new List<string>();
        if (Has("/sitevision/"))                           svSignals.Add("/sitevision/");
        if (Has("svdocready"))                             svSignals.Add("svDocReady");
        if (Has("appregistry"))                            svSignals.Add("AppRegistry");
        if (Has("sv-template-"))                           svSignals.Add("sv-template-*");
        if (Has("sv-portlet"))                             svSignals.Add("sv-portlet");
        if (Has("class=\"sv-") || Has(" class=\"sv-"))    svSignals.Add("sv-* classes");

        if (svSignals.Count >= 2)
        {
            return new CmsDetectionResult
            {
                Kind = CmsKind.SiteVision,
                Confidence = svSignals.Count >= 4 ? "high" : "medium",
                Signals = svSignals
            };
        }

        // ── Drupal ────────────────────────────────────────────────────────────
        // Only use signals specific enough not to appear on non-Drupal sites.
        var drupalSignals = new List<string>();
        if (Has("drupalsettings"))          drupalSignals.Add("drupalSettings");
        if (Has("drupal.settings"))         drupalSignals.Add("Drupal.settings");
        if (Has("/sites/default/files/"))   drupalSignals.Add("/sites/default/files/");
        if (Has("data-drupal-"))            drupalSignals.Add("data-drupal-*");
        if (generatorContent.Contains("drupal")) drupalSignals.Add("meta:generator=Drupal");

        if (drupalSignals.Count >= 1)
        {
            return new CmsDetectionResult
            {
                Kind = CmsKind.Drupal,
                Confidence = drupalSignals.Count >= 2 ? "high" : "medium",
                Signals = drupalSignals
            };
        }

        // ── Umbraco ───────────────────────────────────────────────────────────
        var umbracoSignals = new List<string>();
        if (Has("/umbraco/"))                       umbracoSignals.Add("/umbraco/");
        if (Has("umbraco"))                         umbracoSignals.Add("umbraco");
        if (Has("uskinned"))                        umbracoSignals.Add("uSkinned");
        if (Has("umb-"))                            umbracoSignals.Add("umb-*");
        if (generatorContent.Contains("umbraco"))   umbracoSignals.Add("meta:generator=Umbraco");

        if (umbracoSignals.Count >= 2)
        {
            return new CmsDetectionResult
            {
                Kind = CmsKind.Umbraco,
                Confidence = umbracoSignals.Count >= 3 ? "high" : "medium",
                Signals = umbracoSignals
            };
        }

        // ── Optimizely (EPiServer) ────────────────────────────────────────────
        var epiSignals = new List<string>();
        if (Has("episerver"))   epiSignals.Add("episerver");
        if (Has("optimizely"))  epiSignals.Add("optimizely");
        if (Has("data-epi"))    epiSignals.Add("data-epi*");
        if (Has(" epi-"))       epiSignals.Add("epi-*");

        if (epiSignals.Count >= 2)
        {
            return new CmsDetectionResult
            {
                Kind = CmsKind.Optimizely,
                Confidence = epiSignals.Count >= 3 ? "high" : "medium",
                Signals = epiSignals
            };
        }

        // ── Sitecore ──────────────────────────────────────────────────────────
        var scSignals = new List<string>();
        if (Has("/sitecore/"))  scSignals.Add("/sitecore/");
        if (Has("sitecore"))    scSignals.Add("sitecore");
        if (Has(" sc-"))        scSignals.Add("sc-*");

        if (scSignals.Count >= 2)
        {
            return new CmsDetectionResult
            {
                Kind = CmsKind.Sitecore,
                Confidence = scSignals.Count >= 3 ? "high" : "medium",
                Signals = scSignals
            };
        }

        return new CmsDetectionResult
        {
            Kind = CmsKind.Unknown,
            Confidence = "low",
            Signals = new List<string>()
        };
    }
}
