using System.Xml.Linq;

namespace WebSnapshots;

internal static class SitemapFetcher
{
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    })
    {
        Timeout = TimeSpan.FromSeconds(12),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (compatible; WebSnapshots/1.0)" } }
    };

    // Returns distinct page URLs found in sitemap.xml / sitemap_index.xml / robots.txt
    public static async Task<List<string>> FetchUrlsAsync(string baseUrl, Logger? log = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Uri baseUri;
        try { baseUri = new Uri(baseUrl); }
        catch { return results; }

        // Probe robots.txt for Sitemap: directives first (most reliable)
        var robotsUrl = new Uri(baseUri, "/robots.txt").ToString();
        if (tried.Add(robotsUrl))
        {
            try
            {
                var robotsText = await _http.GetStringAsync(robotsUrl);
                foreach (var line in robotsText.Split('\n'))
                {
                    var l = line.Trim();
                    if (!l.StartsWith("Sitemap:", StringComparison.OrdinalIgnoreCase)) continue;
                    var sitemapUrl = l["Sitemap:".Length..].Trim();
                    if (Uri.IsWellFormedUriString(sitemapUrl, UriKind.Absolute))
                        await TryFetchSitemapAsync(sitemapUrl, tried, seen, results, depth: 0, log);
                }
            }
            catch { }
        }

        // Then probe the standard locations if not yet discovered
        foreach (var path in new[] { "/sitemap.xml", "/sitemap_index.xml", "/wp-sitemap.xml" })
        {
            var url = new Uri(baseUri, path).ToString();
            await TryFetchSitemapAsync(url, tried, seen, results, depth: 0, log);
        }

        if (results.Count > 0)
            log?.Event("SITEMAP_TOTAL", ("baseUrl", baseUrl), ("count", results.Count));

        return results;
    }

    private static async Task TryFetchSitemapAsync(
        string url,
        HashSet<string> tried,
        HashSet<string> seen,
        List<string> results,
        int depth,
        Logger? log)
    {
        if (!tried.Add(url)) return;
        if (depth > 3) return;

        try
        {
            var xml = await _http.GetStringAsync(url);
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var rootName = doc.Root?.Name.LocalName ?? "";

            if (rootName == "sitemapindex")
            {
                // Sitemap index: recurse into each child sitemap
                var childUrls = doc.Descendants(ns + "loc")
                    .Select(e => e.Value?.Trim() ?? "")
                    .Where(u => !string.IsNullOrEmpty(u))
                    .ToList();

                log?.Event("SITEMAP_INDEX", ("url", url), ("children", childUrls.Count));

                foreach (var childUrl in childUrls)
                    await TryFetchSitemapAsync(childUrl, tried, seen, results, depth + 1, log);
            }
            else
            {
                // Regular sitemap: collect <loc> values
                var before = results.Count;
                foreach (var loc in doc.Descendants(ns + "loc"))
                {
                    var pageUrl = loc.Value?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(pageUrl) && seen.Add(pageUrl))
                        results.Add(pageUrl);
                }
                log?.Event("SITEMAP_FETCHED", ("url", url), ("added", results.Count - before));
            }
        }
        catch (Exception ex)
        {
            log?.Warn($"SITEMAP_SKIP {url}: {ex.Message}");
        }
    }
}
