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
    public static async Task<CmsDetectionResult> DetectAsync(IPage page)
    {
        var html = await page.ContentAsync();
        var lower = html.ToLowerInvariant();

        var signals = new List<string>();

        bool Has(string s) => lower.Contains(s, StringComparison.Ordinal);

        if (Has("/sitevision/")) signals.Add("/sitevision/");
        if (Has("svdocready")) signals.Add("svDocReady");
        if (Has("appregistry")) signals.Add("AppRegistry");
        if (Has("sv-template-")) signals.Add("sv-template-*");
        if (Has("sv-portlet")) signals.Add("sv-portlet");
        if (Has("class=\"sv-") || Has(" class=\"sv-")) signals.Add("sv-* classes");

        if (signals.Count >= 2)
        {
            return new CmsDetectionResult
            {
                Kind = CmsKind.SiteVision,
                Confidence = signals.Count >= 4 ? "high" : "medium",
                Signals = signals
            };
        }

        signals = new List<string>();
        if (Has("/wp-content/")) signals.Add("/wp-content/");
        if (Has("/wp-includes/")) signals.Add("/wp-includes/");
        if (Has("wp-json")) signals.Add("wp-json");
        if (Has("wp-")) signals.Add("wp-*");

        if (signals.Count >= 2)
        {
            return new CmsDetectionResult
            {
                Kind = CmsKind.WordPress,
                Confidence = signals.Count >= 3 ? "high" : "medium",
                Signals = signals
            };
        }

        signals = new List<string>();
        if (Has("drupalsettings")) signals.Add("drupalSettings");
        if (Has("drupal.settings")) signals.Add("Drupal.settings");
        if (Has("/sites/default/files/")) signals.Add("/sites/default/files/");
        if (Has("block-")) signals.Add("block-*");
        if (Has("region-")) signals.Add("region-*");

        if (signals.Count >= 2)
        {
            return new CmsDetectionResult
            {
                Kind = CmsKind.Drupal,
                Confidence = signals.Count >= 3 ? "high" : "medium",
                Signals = signals
            };
        }

        signals = new List<string>();
        if (Has("/umbraco/")) signals.Add("/umbraco/");
        if (Has("umbraco")) signals.Add("umbraco");
        if (Has("uSkinned".ToLowerInvariant())) signals.Add("uSkinned");
        if (Has("umb-")) signals.Add("umb-*");

        if (signals.Count >= 2)
        {
            return new CmsDetectionResult
            {
                Kind = CmsKind.Umbraco,
                Confidence = signals.Count >= 3 ? "high" : "medium",
                Signals = signals
            };
        }

        signals = new List<string>();
        if (Has("episerver")) signals.Add("episerver");
        if (Has("optimizely")) signals.Add("optimizely");
        if (Has("data-epi")) signals.Add("data-epi*");
        if (Has(" epi-")) signals.Add("epi-*");

        if (signals.Count >= 2)
        {
            return new CmsDetectionResult
            {
                Kind = CmsKind.Optimizely,
                Confidence = signals.Count >= 3 ? "high" : "medium",
                Signals = signals
            };
        }

        signals = new List<string>();
        if (Has("/sitecore/")) signals.Add("/sitecore/");
        if (Has("sitecore")) signals.Add("sitecore");
        if (Has(" sc-")) signals.Add("sc-*");

        if (signals.Count >= 2)
        {
            return new CmsDetectionResult
            {
                Kind = CmsKind.Sitecore,
                Confidence = signals.Count >= 3 ? "high" : "medium",
                Signals = signals
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