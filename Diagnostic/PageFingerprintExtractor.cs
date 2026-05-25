using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace WebSnapshots.Diagnostic;

/// <summary>
/// Extracts a lightweight page template fingerprint from a loaded IPage.
/// Uses only Playwright JS evaluation — no external dependencies, no ML, no screenshots.
/// The fingerprint is stable (does not include timestamps or dynamic IDs).
/// </summary>
public static class PageFingerprintExtractor
{
    private static readonly Regex _yearSegment = new(@"^\d{4}$", RegexOptions.Compiled);
    private static readonly Regex _numericSegment = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex _guidSegment = new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _slugLike = new(@"^[a-z0-9][a-z0-9\-]{4,}$", RegexOptions.Compiled);

    // Body class prefixes that are CMS-structural, not page-specific
    private static readonly HashSet<string> _structuralPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "sv-", "wp-", "page-template", "umb-", "epi-", "drupal-", "node-type-"
    };

    // JS payload extracted once per page visit — compact and side-effect free.
    // Class-substring signals are scoped to mainEl so site-wide layout classes do not fire.
    private const string ExtractJs = @"
() => {
  try {
    const body = document.body;
    const bodyClasses = body ? (body.className || '').split(/\s+/).filter(Boolean) : [];

    const sel = (q) => !!document.querySelector(q);
    const count = (q) => document.querySelectorAll(q).length;
    const mainEl = document.querySelector('main, [role=""main""]');

    const hasMain       = !!mainEl;
    const hasBreadcrumb = sel('[class*=""breadcrumb""], nav.breadcrumb, .crumbs, [aria-label*=""breadcrumb""], [aria-label*=""Breadcrumb""]');
    const hasLocalNav   = sel('.local-nav, .left-nav, aside nav, [class*=""local-nav""], [class*=""sidenav""], [class*=""sidebar""] nav');
    const hasHeaderNav  = sel('header nav, .header nav, [class*=""header""] nav, [role=""navigation""]');

    const h1 = count('h1');
    const h2 = count('h2');
    const h3 = count('h3');
    const h1El = document.querySelector('h1');
    const h1Text = h1El ? (h1El.textContent || '').trim() : '';

    const hasSearchInMain = mainEl ? !!mainEl.querySelector('[class*=""search""], [class*=""sok""], input[type=""search""], form[action*=""search""]') : false;
    const hasContactForm  = mainEl ? !!mainEl.querySelector('form:not([action*=""search""])') : false;
    const hasTimeEvent    = count('time[datetime]') > 0;
    const hasPdfLinks     = count('a[href$="".pdf""], a[href*="".pdf?""]') > 0;
    const hasListItems    = mainEl ? mainEl.querySelectorAll('ul li, ol li').length > 5 : false;

    const hasNewsClass    = mainEl ? !!mainEl.querySelector('[class*=""news""], [class*=""nyhet""]') : false;
    const hasEventClass   = mainEl ? !!mainEl.querySelector('[class*=""event""], [class*=""kalender""], [class*=""aktivitet""]') : false;
    const hasArchiveClass = mainEl ? !!mainEl.querySelector('[class*=""archive""], [class*=""arkiv""]') : false;
    const hasDocClass     = mainEl ? !!mainEl.querySelector('[class*=""document""], [class*=""dokument""]') : false;
    const hasContactClass = mainEl ? !!mainEl.querySelector('[class*=""contact""], [class*=""kontakt""]') : false;
    const hasListClass    = mainEl ? !!mainEl.querySelector('[class*=""listing""], [class*=""list-view""]') : false;

    const sections = count('main section, main article, main .content-block, main .sv-portlet, main .entry-content > *, main .page-content > *');

    return { bodyClasses, hasMain, hasBreadcrumb, hasLocalNav, hasHeaderNav,
             h1, h2, h3, h1Text,
             hasSearchInMain, hasContactForm, hasTimeEvent, hasPdfLinks, hasListItems,
             hasNewsClass, hasEventClass, hasArchiveClass, hasDocClass, hasContactClass, hasListClass,
             sections };
  } catch (e) {
    return { bodyClasses: [], hasMain: false, hasBreadcrumb: false, hasLocalNav: false, hasHeaderNav: false,
             h1: 0, h2: 0, h3: 0, h1Text: '',
             hasSearchInMain: false, hasContactForm: false, hasTimeEvent: false, hasPdfLinks: false, hasListItems: false,
             hasNewsClass: false, hasEventClass: false, hasArchiveClass: false, hasDocClass: false,
             hasContactClass: false, hasListClass: false, sections: 0 };
  }
}";

    public static async Task<PageTemplateFingerprint?> ExtractAsync(
        IPage page,
        string url,
        string cmsKind)
    {
        try
        {
            var raw = await page.EvaluateAsync<JsonElement>(ExtractJs);

            var bodyClasses    = GetStringList(raw, "bodyClasses");
            var hasMain        = GetBool(raw, "hasMain");
            var hasBreadcrumb  = GetBool(raw, "hasBreadcrumb");
            var hasLocalNav    = GetBool(raw, "hasLocalNav");
            var hasHeaderNav   = GetBool(raw, "hasHeaderNav");
            var h1             = GetInt(raw, "h1");
            var h2             = GetInt(raw, "h2");
            var h3             = GetInt(raw, "h3");
            var h1Text         = GetString(raw, "h1Text");
            var sections       = GetInt(raw, "sections");

            var hasSearchInMain = GetBool(raw, "hasSearchInMain");
            var hasContactForm  = GetBool(raw, "hasContactForm");
            var hasTimeEvent    = GetBool(raw, "hasTimeEvent");
            var hasPdfLinks     = GetBool(raw, "hasPdfLinks");
            var hasListItems    = GetBool(raw, "hasListItems");
            var hasNewsClass    = GetBool(raw, "hasNewsClass");
            var hasEventClass   = GetBool(raw, "hasEventClass");
            var hasArchiveClass = GetBool(raw, "hasArchiveClass");
            var hasDocClass     = GetBool(raw, "hasDocClass");
            var hasContactClass = GetBool(raw, "hasContactClass");
            var hasListClass    = GetBool(raw, "hasListClass");

            var path = "";
            try { path = new Uri(url).AbsolutePath; } catch { }

            var (contentType, evidence) = ContentTypeDetector.Detect(
                path, h1Text,
                hasSearchInMain, hasContactForm, hasTimeEvent, hasPdfLinks, hasListItems,
                hasNewsClass, hasEventClass, hasArchiveClass, hasDocClass, hasContactClass, hasListClass);

            var pathShape    = NormalizePath(path);
            var classHints   = FilterBodyClasses(bodyClasses, cmsKind);
            var headingShape = $"h1:{h1},h2:{h2},h3:{h3}";
            var sectionBucket = sections switch
            {
                0    => "none",
                <= 3 => "small",
                <= 8 => "medium",
                _    => "large"
            };

            var fp = new PageTemplateFingerprint
            {
                Url            = url,
                PathShape      = pathShape,
                CmsKind        = cmsKind,
                BodyClassHints = classHints,
                HasMain        = hasMain,
                HasBreadcrumb  = hasBreadcrumb,
                HasLocalNav    = hasLocalNav,
                HasHeaderNav   = hasHeaderNav,
                ContentType    = contentType,
                HeadingShape   = headingShape,
                SectionBucket  = sectionBucket,
                FingerprintHash = ComputeHash(pathShape, cmsKind, classHints,
                    hasMain, hasBreadcrumb, hasLocalNav, hasHeaderNav,
                    contentType, headingShape, sectionBucket),
                Evidence = evidence
            };

            return fp;
        }
        catch
        {
            return null;
        }
    }

    // ── Path normalization ────────────────────────────────────────────────────

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return "/";

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var shaped = new List<string>(segments.Length);

        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (_guidSegment.IsMatch(seg))
                shaped.Add("{guid}");
            else if (_yearSegment.IsMatch(seg))
                shaped.Add("{year}");
            else if (_numericSegment.IsMatch(seg))
                shaped.Add("{id}");
            else if (i >= 2 && _slugLike.IsMatch(seg))
                // Only replace deep path segments with {slug}; top-level sections keep their name
                shaped.Add("{slug}");
            else
                shaped.Add(seg);
        }

        return "/" + string.Join("/", shaped);
    }

    // ── Body class filtering ──────────────────────────────────────────────────

    private static List<string> FilterBodyClasses(List<string> classes, string cmsKind)
    {
        var hints = new List<string>();
        foreach (var cls in classes)
        {
            // Keep CMS-structural prefixes (tells us CMS template type)
            if (_structuralPrefixes.Any(p => cls.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                hints.Add(cls);
                continue;
            }

            // Keep classes that suggest page type
            var lower = cls.ToLowerInvariant();
            if (lower.Contains("template") || lower.Contains("page-") || lower.Contains("layout") ||
                lower.Contains("content-type") || lower.Contains("post-type"))
            {
                hints.Add(cls);
            }
        }

        // Deduplicate, sort for stability, cap at 6
        return hints.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .Take(6)
            .ToList();
    }

    // ── Stable hash ───────────────────────────────────────────────────────────

    private static string ComputeHash(
        string pathShape, string cmsKind, List<string> classHints,
        bool hasMain, bool hasBreadcrumb, bool hasLocalNav, bool hasHeaderNav,
        string contentType, string headingShape, string sectionBucket)
    {
        // Build a stable, sorted string from all non-URL fields
        var parts = new SortedList<string, string>
        {
            ["pathShape"]    = pathShape,
            ["cms"]          = cmsKind,
            ["classes"]      = string.Join(",", classHints.OrderBy(c => c)),
            ["hasMain"]      = hasMain ? "1" : "0",
            ["hasBreadcrumb"] = hasBreadcrumb ? "1" : "0",
            ["hasLocalNav"]  = hasLocalNav ? "1" : "0",
            ["hasHeaderNav"] = hasHeaderNav ? "1" : "0",
            ["contentType"]  = contentType,
            ["headingShape"] = headingShape,
            ["sectionBucket"] = sectionBucket
        };

        var canonical = string.Join("|", parts.Select(kv => $"{kv.Key}={kv.Value}"));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes)[..16]; // 8-byte prefix is enough for uniqueness here
    }

    // ── JSON element helpers ──────────────────────────────────────────────────

    private static bool GetBool(JsonElement e, string key)
    {
        if (e.TryGetProperty(key, out var v))
            return v.ValueKind == JsonValueKind.True;
        return false;
    }

    private static int GetInt(JsonElement e, string key)
    {
        if (e.TryGetProperty(key, out var v) && v.TryGetInt32(out var i)) return i;
        return 0;
    }

    private static string GetString(JsonElement e, string key)
    {
        if (e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }

    private static List<string> GetStringList(JsonElement e, string key)
    {
        var result = new List<string>();
        if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in v.EnumerateArray())
        {
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
        }
        return result;
    }
}
