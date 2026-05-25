using System.Text.Json.Serialization;

namespace WebSnapshots.Diagnostic;

public sealed class PageTemplateFingerprint
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("pathShape")]
    public string PathShape { get; set; } = "";

    [JsonPropertyName("cmsKind")]
    public string CmsKind { get; set; } = "";

    [JsonPropertyName("bodyClassHints")]
    public List<string> BodyClassHints { get; set; } = new();

    [JsonPropertyName("hasMain")]
    public bool HasMain { get; set; }

    [JsonPropertyName("hasBreadcrumb")]
    public bool HasBreadcrumb { get; set; }

    [JsonPropertyName("hasLocalNav")]
    public bool HasLocalNav { get; set; }

    [JsonPropertyName("hasHeaderNav")]
    public bool HasHeaderNav { get; set; }

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = "normal_page";

    [JsonPropertyName("headingShape")]
    public string HeadingShape { get; set; } = "";

    [JsonPropertyName("sectionBucket")]
    public string SectionBucket { get; set; } = "none";

    [JsonPropertyName("fingerprintHash")]
    public string FingerprintHash { get; set; } = "";

    // Not included in fingerprint hash — diagnostic metadata only.
    [JsonPropertyName("evidence")]
    public ContentTypeEvidence? Evidence { get; set; }
}

/// <summary>
/// Explains why a content type was chosen. All signal lists are sorted for determinism.
/// Not part of the fingerprint hash.
/// </summary>
public sealed class ContentTypeEvidence
{
    [JsonPropertyName("urlSignals")]
    public List<string> UrlSignals { get; init; } = new();

    [JsonPropertyName("headingSignals")]
    public List<string> HeadingSignals { get; init; } = new();

    [JsonPropertyName("selectorSignals")]
    public List<string> SelectorSignals { get; init; } = new();

    [JsonPropertyName("confidence")]
    public string Confidence { get; init; } = "low";
}

/// <summary>
/// Classifies page content type from evidence signals.
/// Priority order: document > search > event > news > archive > contact > listing > normal_page.
///
/// Rules:
///   Strong signals (sufficient alone):    URL path keywords, h1 text keywords.
///   Medium signals (require combination): time[datetime], PDF links, forms in main.
///   Weak signals (never sufficient alone): CSS class substrings inside main.
///
/// CSS class substrings are tracked in evidence but do NOT drive classification
/// without a URL or heading keyword to confirm intent.
/// </summary>
public static class ContentTypeDetector
{
    public static (string ContentType, ContentTypeEvidence Evidence) Detect(
        string path,
        string h1Text,
        bool hasSearchInMain,
        bool hasContactForm,
        bool hasTimeEvent,
        bool hasPdfLinks,
        bool hasListItems,
        bool hasNewsClass,
        bool hasEventClass,
        bool hasArchiveClass,
        bool hasDocClass,
        bool hasContactClass,
        bool hasListClass)
    {
        var p = path.ToLowerInvariant();
        var h = h1Text.ToLowerInvariant();

        var urlSig = new List<string>(4);
        var hdgSig = new List<string>(4);
        var selSig = new List<string>(8);

        // ── URL keyword flags ────────────────────────────────────────────────
        bool urlDoc     = p.Contains("dokument") || p.Contains("blankett") || p.Contains("/pdf");
        bool urlSearch  = p.Contains("search") || p.Contains("/sok") || p.Contains("/s%C3%B6k") || p.Contains("hitta");
        bool urlEvent   = p.Contains("event") || p.Contains("evenemang") || p.Contains("kalender") || p.Contains("aktivitet");
        bool urlNews    = p.Contains("nyhet") || p.Contains("nyheter") || p.Contains("news") || p.Contains("pressrelease") || p.Contains("artikel");
        bool urlArchive = p.Contains("arkiv") || p.Contains("archive");
        bool urlContact = p.Contains("kontakt") || p.Contains("contact");
        bool urlList    = p.Contains("lista") || p.Contains("listing");

        if (urlDoc)     urlSig.Add("doc");
        if (urlSearch)  urlSig.Add("search");
        if (urlEvent)   urlSig.Add("event");
        if (urlNews)    urlSig.Add("news");
        if (urlArchive) urlSig.Add("archive");
        if (urlContact) urlSig.Add("contact");
        if (urlList)    urlSig.Add("listing");

        // ── Heading keyword flags ────────────────────────────────────────────
        bool h1Doc     = h.Contains("dokument") || h.Contains("blankett");
        bool h1Search  = h.Contains("sök") || h.Contains("sok") || h.Contains("search") || h.Contains("hitta");
        bool h1Event   = h.Contains("event") || h.Contains("evenemang") || h.Contains("kalender");
        bool h1News    = h.Contains("nyhet") || h.Contains("nyheter") || h.Contains("news");
        bool h1Archive = h.Contains("arkiv") || h.Contains("archive");
        bool h1Contact = h.Contains("kontakt") || h.Contains("contact");

        if (h1Doc)     hdgSig.Add("doc");
        if (h1Search)  hdgSig.Add("search");
        if (h1Event)   hdgSig.Add("event");
        if (h1News)    hdgSig.Add("news");
        if (h1Archive) hdgSig.Add("archive");
        if (h1Contact) hdgSig.Add("contact");

        // ── Structural / class signals ───────────────────────────────────────
        if (hasPdfLinks)     selSig.Add("pdf-links");
        if (hasTimeEvent)    selSig.Add("time-datetime");
        if (hasSearchInMain) selSig.Add("search-in-main");
        if (hasContactForm)  selSig.Add("contact-form");
        if (hasListItems)    selSig.Add("list-items");
        if (hasDocClass)     selSig.Add("doc-class");
        if (hasNewsClass)    selSig.Add("news-class");
        if (hasEventClass)   selSig.Add("event-class");
        if (hasArchiveClass) selSig.Add("archive-class");
        if (hasContactClass) selSig.Add("contact-class");
        if (hasListClass)    selSig.Add("list-class");

        ContentTypeEvidence Ev(string conf) => new()
        {
            UrlSignals     = urlSig,
            HeadingSignals = hdgSig,
            SelectorSignals = selSig,
            Confidence     = conf
        };

        // 1. Document — PDF links in main (structural, strong), or URL/heading keyword
        if (hasPdfLinks || urlDoc || h1Doc)
            return ("document", Ev(hasPdfLinks || (urlDoc && h1Doc) ? "high" : "medium"));

        // 2. Search — input in main (structural), or URL/heading keyword
        if (hasSearchInMain || urlSearch || h1Search)
            return ("search", Ev(hasSearchInMain && (urlSearch || h1Search) ? "high" : "medium"));

        // 3. Event — URL or heading keyword (strong); time element + scoped event class = medium
        if (urlEvent || h1Event || (hasTimeEvent && hasEventClass))
            return ("event", Ev(urlEvent || h1Event ? "high" : "medium"));

        // 4. News — URL or heading keyword (archive in URL overrides: /nyheter/arkiv → archive)
        if ((urlNews && !urlArchive) || (h1News && !h1Archive) ||
            (hasTimeEvent && hasNewsClass && !hasEventClass && !urlArchive))
            return ("news", Ev(urlNews || h1News ? "high" : "medium"));

        // 5. Archive — URL or heading keyword REQUIRED; CSS class alone is not sufficient
        if (urlArchive || h1Archive)
            return ("archive", Ev("high"));

        // 6. Contact — URL or heading keyword (strong); contact form + class = medium
        if (urlContact || h1Contact || (hasContactForm && hasContactClass))
            return ("contact", Ev(urlContact || h1Contact ? "high" : "medium"));

        // 7. Listing — URL keyword AND list items present (both required)
        if (urlList && hasListItems)
            return ("listing", Ev("medium"));

        // 8. Normal page
        return ("normal_page", Ev("high"));
    }
}
