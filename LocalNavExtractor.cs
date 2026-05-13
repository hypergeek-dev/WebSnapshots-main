using System.Text.Json;
using Microsoft.Playwright;

namespace WebSnapshots;

public static class LocalNavExtractor
{
    public static async Task<List<PageNavLink>> ExtractAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxLinks = 80)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        if (string.IsNullOrWhiteSpace(pageUrl)) return new List<PageNavLink>();

        var raw = await page.EvaluateAsync<JsonElement>(@"
(pageUrl, host, maxLinks) => {
  const normalizeHost = (h) => {
    h = (h || '').toLowerCase().trim();
    if (h.startsWith('www.')) h = h.substring(4);
    return h;
  };

  const sameSiteHost = (a, b) => normalizeHost(a) === normalizeHost(b);

  const norm = (s) => (s || '').replace(/\s+/g, ' ').trim();

  const isVisible = (el) => {
    try {
      if (!el) return false;
      const cs = window.getComputedStyle(el);
      if (!cs) return false;
      if (cs.display === 'none' || cs.visibility === 'hidden') return false;
      const r = el.getBoundingClientRect();
      if (!r) return false;
      if (r.width < 1 || r.height < 1) return false;
      return true;
    } catch {
      return false;
    }
  };

  const addLink = (out, seen, hrefRaw, text, kind, sourceType, displayRole, groupLabel, confidence, isStructural) => {
    if (!hrefRaw) return;
    const href = hrefRaw.trim();
    if (!href) return;
    if (href.startsWith('#')) return;
    if (href.startsWith('javascript:')) return;
    if (href.startsWith('mailto:')) return;
    if (href.startsWith('tel:')) return;

    let abs = '';
    try { abs = new URL(href, pageUrl).toString(); } catch { return; }

    let h = '';
    try { h = new URL(abs).host; } catch { return; }
    if (!sameSiteHost(h, host)) return;
    if (seen.has(abs)) return;

    seen.add(abs);

    out.push({
      url: abs,
      text: norm(text) || abs,
      kind: kind || '',
      sourceType: sourceType || 'Generic',
      displayRole: displayRole || 'VisibleContent',
      groupLabel: groupLabel || 'Visible content',
      confidence: confidence || 0,
      isStructural: !!isStructural
    });
  };

  const results = [];
  const seen = new Set();

  // Tree-like page listing
  for (const a of document.querySelectorAll('nav.sol-page-listing a[href], .sol-page-listing a[href]')) {
    let text = '';
    const h = a.querySelector('h1,h2,h3,h4,h5,h6');
    if (h) text = h.textContent || '';
    if (!text) text = a.textContent || '';
    addLink(results, seen, a.getAttribute('href'), text, 'page-listing', 'PageListing', 'Navigation', 'Main navigation', 0.95, true);
  }

  // Generic nav / header nav
  for (const a of document.querySelectorAll('nav a[href], [role=""navigation""] a[href], header nav a[href]')) {
    addLink(results, seen, a.getAttribute('href'), a.textContent || a.getAttribute('title') || '', 'nav', 'MainNav', 'Navigation', 'Main navigation', 0.90, true);
  }

  // Local nav / side nav
  for (const a of document.querySelectorAll('aside a[href], .sidebar a[href], .sidenav a[href]')) {
    addLink(results, seen, a.getAttribute('href'), a.textContent || a.getAttribute('title') || '', 'aside', 'LocalNav', 'Navigation', 'Local navigation', 0.85, true);
  }

  // Breadcrumbs
  for (const a of document.querySelectorAll('.breadcrumb a[href], .breadcrumbs a[href], nav[aria-label*=""breadcrumb"" i] a[href], [aria-label*=""breadcrumb"" i] a[href]')) {
    addLink(results, seen, a.getAttribute('href'), a.textContent || a.getAttribute('title') || '', 'breadcrumb', 'Breadcrumb', 'Navigation', 'Breadcrumb', 0.93, true);
  }

  // Fallback if nothing structural was found
  if (results.length === 0) {
    for (const a of document.querySelectorAll('a[href]')) {
      if (!isVisible(a)) continue;
      addLink(results, seen, a.getAttribute('href'), a.textContent || a.getAttribute('title') || '', 'content', 'Generic', 'VisibleContent', 'Visible content', 0.10, false);
      if (results.length >= maxLinks) break;
    }
  }

  return results.slice(0, Math.max(1, maxLinks || 80));
}
", new object?[] { pageUrl, host, maxLinks });

        return ParsePageNavLinks(raw, host, dropQueryStrings, maxLinks);
    }

    public static async Task<List<VisibleLinkGroup>> ExtractVisibleGroupsAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxPerGroup = 25)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        if (string.IsNullOrWhiteSpace(pageUrl)) return new List<VisibleLinkGroup>();

        var groups = new List<VisibleLinkGroup>();

        var shortcuts = await ExtractGroupBySelectorAsync(
            page, pageUrl, host, dropQueryStrings,
            ".sol-popular-pages a[href]",
            "shortcuts", "Genvägar", "Shortcut", 10, maxPerGroup);
        if (shortcuts.Flat.Count > 0) groups.Add(shortcuts);

        var news = await ExtractGroupBySelectorAsync(
            page, pageUrl, host, dropQueryStrings,
            "#Nyheter a.sol-article-item-heading[href], .sv-archive-portlet a.sol-article-item-heading[href]",
            "news", "Nyheter", "News", 20, maxPerGroup);
        if (news.Flat.Count > 0) groups.Add(news);

        var alerts = await ExtractSiteVisionAlertsAsync(page, pageUrl, host, dropQueryStrings, maxPerGroup);
        if (alerts.Flat.Count > 0) groups.Add(alerts);

        var events = await ExtractSiteVisionEventsAsync(page, pageUrl, host, dropQueryStrings, maxPerGroup);
        if (events.Flat.Count > 0) groups.Add(events);

        var promoCards = await ExtractSiteVisionPromoCardsAsync(page, pageUrl, host, dropQueryStrings, maxPerGroup);
        if (promoCards.Flat.Count > 0) groups.Add(promoCards);

        var utility = await ExtractGroupBySelectorAsync(
            page, pageUrl, host, dropQueryStrings,
            ".sol-tool-nav a[href]",
            "utility", "Verktygsfält", "Utility", 50, maxPerGroup);
        if (utility.Flat.Count > 0) groups.Add(utility);

        var footerLinks = await ExtractGroupBySelectorAsync(
            page, pageUrl, host, dropQueryStrings,
            ".sol-footer-links a[href]",
            "footerlinks", "Om webbplatsen", "FooterLinks", 60, maxPerGroup);
        if (footerLinks.Flat.Count > 0) groups.Add(footerLinks);

        var footerContact = await ExtractGroupBySelectorAsync(
            page, pageUrl, host, dropQueryStrings,
            ".sol-footer-contact a[href]",
            "footercontact", "Kontakt", "FooterContact", 70, maxPerGroup);
        if (footerContact.Flat.Count > 0) groups.Add(footerContact);

        return groups
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<VisibleLinkGroup> ExtractGroupBySelectorAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        string selector,
        string id,
        string label,
        string role,
        int order,
        int maxPerGroup)
    {
        var raw = await page.EvaluateAsync<JsonElement>(@"
(selector) => {
  const out = [];
  const seen = new Set();

  const norm = (s) => (s || '').replace(/\s+/g, ' ').trim();

  for (const a of document.querySelectorAll(selector)) {
    const href = (a.getAttribute('href') || '').trim();
    if (!href) continue;

    let abs = '';
    try { abs = new URL(href, document.location.href).toString(); } catch { continue; }
    if (seen.has(abs)) continue;
    seen.add(abs);

    let text = '';
    const h = a.querySelector('h1,h2,h3,h4,h5,h6');
    if (h) text = norm(h.textContent || '');
    if (!text) text = norm(a.textContent || '');
    if (!text) text = norm(a.getAttribute('title') || '');
    if (!text) text = href;

    out.push({ url: abs, text });
  }

  return out;
}
", selector);

        var flat = ParseNavItems(raw, host, dropQueryStrings, maxPerGroup, pageUrl);

        return new VisibleLinkGroup
        {
            Id = id,
            Label = label,
            Role = role,
            Order = order,
            Flat = flat
        };
    }

    private static async Task<VisibleLinkGroup> ExtractSiteVisionPromoCardsAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxPerGroup)
    {
        var raw = await page.EvaluateAsync<JsonElement>(@"
() => {
  const out = [];
  const seen = new Set();

  const roots = document.querySelectorAll('.sol-startpage-widgets .sol-widget-decoration-wrapper');
  for (const root of roots) {
    let a = root.querySelector('h1 a[href], h2 a[href], h3 a[href], h4 a[href]');
    if (!a) a = root.querySelector('a[href]');
    if (!a) continue;

    const href = (a.getAttribute('href') || '').trim();
    if (!href) continue;

    let abs = '';
    try { abs = new URL(href, document.location.href).toString(); } catch { continue; }
    if (seen.has(abs)) continue;
    seen.add(abs);

    let text = (a.textContent || '').replace(/\s+/g, ' ').trim();
    if (!text) text = href;

    out.push({ url: abs, text });
  }

  return out;
}
");

        return new VisibleLinkGroup
        {
            Id = "promocards",
            Label = "Startsidans puffar",
            Role = "PromoCard",
            Order = 40,
            Flat = ParseNavItems(raw, host, dropQueryStrings, maxPerGroup, pageUrl)
        };
    }

    private static async Task<VisibleLinkGroup> ExtractSiteVisionEventsAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxPerGroup)
    {
        var group = new VisibleLinkGroup
        {
            Id = "events",
            Label = "Evenemang",
            Role = "Events",
            Order = 30,
            Flat = new List<NavItem>()
        };

        // Try rendered DOM first
        var raw = await page.EvaluateAsync<JsonElement>(@"
() => {
  const out = [];
  const seen = new Set();
  const norm = (s) => (s || '').replace(/\s+/g, ' ').trim();

  const selectors = [
    '.sv-custom-module.sv-se-soleilit-eventslist a[href]',
    '.se-soleilit-eventslist a[href]',
    'a[href*=""/arkiv/evenemang""]'
  ];

  for (const sel of selectors) {
    for (const a of document.querySelectorAll(sel)) {
      const href = (a.getAttribute('href') || '').trim();
      if (!href) continue;

      let abs = '';
      try { abs = new URL(href, document.location.href).toString(); } catch { continue; }
      if (seen.has(abs)) continue;

      const text = norm(a.textContent || a.getAttribute('title') || '');
      if (!text) continue;

      const block = a.closest('.sv-custom-module, section, article, div');
      const blockText = norm((block && block.textContent) || '').toLowerCase();

      const looksEventish =
        blockText.includes('evenemang') ||
        blockText.includes('aktivitet') ||
        blockText.includes('bibliotek') ||
        /\b\d{4}-\d{2}-\d{2}\b/.test(blockText);

      if (!looksEventish && !abs.toLowerCase().includes('/arkiv/evenemang'))
        continue;

      seen.add(abs);
      out.push({ url: abs, text });
    }
  }

  return out;
}
");

        group.Flat = ParseNavItems(raw, host, dropQueryStrings, maxPerGroup, pageUrl);

        if (group.Flat.Count == 0)
        {
            var html = await page.ContentAsync();
            const string marker = "\"eventPageUrl\":\"";
            var idx = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                idx += marker.Length;
                var end = html.IndexOf('"', idx);
                if (end > idx)
                {
                    var urlRaw = html.Substring(idx, end - idx)
                        .Replace("\\/", "/", StringComparison.Ordinal);

                    var url = NormalizeInternalUrl(urlRaw, pageUrl, host, dropQueryStrings);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        group.Flat.Add(new NavItem
                        {
                            Url = url,
                            Title = "Visa fler evenemang",
                            Depth = 1,
                            ParentUrl = pageUrl
                        });
                    }
                }
            }
        }

        return group;
    }

    private static async Task<VisibleLinkGroup> ExtractSiteVisionAlertsAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxPerGroup)
    {
        var group = new VisibleLinkGroup
        {
            Id = "alerts",
            Label = "Störningar",
            Role = "Alert",
            Order = 15,
            Flat = new List<NavItem>()
        };

        var html = await page.ContentAsync();
        var marker = "\"messages\":[";
        var idx = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return group;

        var arrStart = html.IndexOf('[', idx);
        if (arrStart < 0)
            return group;

        var depth = 0;
        var arrEnd = -1;

        for (var i = arrStart; i < html.Length; i++)
        {
            var ch = html[i];
            if (ch == '[') depth++;
            else if (ch == ']')
            {
                depth--;
                if (depth == 0)
                {
                    arrEnd = i;
                    break;
                }
            }
        }

        if (arrEnd < 0)
            return group;

        var arrText = html.Substring(arrStart, arrEnd - arrStart + 1);

        try
        {
            using var doc = JsonDocument.Parse(arrText);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var urlRaw = item.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
                var title = item.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";

                var url = NormalizeInternalUrl(urlRaw, pageUrl, host, dropQueryStrings);
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (string.IsNullOrWhiteSpace(title)) title = url;

                if (group.Flat.Any(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
                    continue;

                group.Flat.Add(new NavItem
                {
                    Url = url,
                    Title = title.Trim(),
                    Depth = 1,
                    ParentUrl = pageUrl
                });

                if (group.Flat.Count >= maxPerGroup)
                    break;
            }
        }
        catch
        {
            return group;
        }

        return group;
    }

    private static List<PageNavLink> ParsePageNavLinks(
        JsonElement raw,
        string host,
        bool dropQueryStrings,
        int maxLinks)
    {
        var list = new List<PageNavLink>();
        if (raw.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in raw.EnumerateArray())
        {
            var urlRaw = item.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
            var text = item.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
            var kind = item.TryGetProperty("kind", out var k) ? (k.GetString() ?? "") : "";
            var sourceType = item.TryGetProperty("sourceType", out var st) ? (st.GetString() ?? "") : "";
            var displayRole = item.TryGetProperty("displayRole", out var dr) ? (dr.GetString() ?? "") : "";
            var groupLabel = item.TryGetProperty("groupLabel", out var gl) ? (gl.GetString() ?? "") : "";
            var confidence = item.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var d) ? d : 0.0;
            var isStructural = item.TryGetProperty("isStructural", out var s) &&
                               (s.ValueKind == JsonValueKind.True || s.ValueKind == JsonValueKind.False)
                               ? s.GetBoolean()
                               : false;

            var url = NormalizeInternalUrl(urlRaw, "", host, dropQueryStrings);
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (string.IsNullOrWhiteSpace(text)) text = url;

            list.Add(new PageNavLink
            {
                Url = url,
                Text = text.Trim(),
                Kind = kind ?? "",
                SourceType = string.IsNullOrWhiteSpace(sourceType) ? "Generic" : sourceType.Trim(),
                DisplayRole = string.IsNullOrWhiteSpace(displayRole) ? "VisibleContent" : displayRole.Trim(),
                GroupLabel = string.IsNullOrWhiteSpace(groupLabel) ? "Visible content" : groupLabel.Trim(),
                Confidence = confidence,
                IsStructural = isStructural
            });

            if (list.Count >= maxLinks)
                break;
        }

        return list;
    }

    private static List<NavItem> ParseNavItems(
        JsonElement raw,
        string host,
        bool dropQueryStrings,
        int maxLinks,
        string pageUrl)
    {
        var list = new List<NavItem>();
        if (raw.ValueKind != JsonValueKind.Array)
            return list;

        // Track base URLs (without query strings) to detect JS-dynamic items
        var baseUrlsSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in raw.EnumerateArray())
        {
            var urlRaw = item.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
            var text = item.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";

            if (string.IsNullOrWhiteSpace(urlRaw)) continue;

            // Resolve to absolute
            string abs;
            try
            {
                abs = string.IsNullOrWhiteSpace(pageUrl)
                    ? urlRaw
                    : (Utils.ToAbsoluteUrl(pageUrl, urlRaw) ?? urlRaw);
            }
            catch { continue; }

            if (string.IsNullOrWhiteSpace(abs)) continue;
            if (!Utils.IsHttpUrl(abs)) continue;

            bool isExternal = false;
            bool isJsDynamic = false;

            try
            {
                var uri = new Uri(abs);
                isExternal = !SameSiteHost(uri.Host, host);

                // Normalise (strips query string if dropQueryStrings)
                var norm = Utils.NormalizeUrl(abs, dropQueryStrings);

                // Detect JS-dynamic: original URL had a query string but after
                // stripping it matches a base URL we've already seen
                if (dropQueryStrings && uri.Query.Length > 0)
                {
                    var withoutQuery = Utils.NormalizeUrl(abs, true);
                    if (baseUrlsSeen.ContainsKey(withoutQuery))
                    {
                        // Already have this base URL — this is a JS-dynamic duplicate
                        // Keep it with the original title but mark it
                        isJsDynamic = true;
                    }
                    else
                    {
                        baseUrlsSeen[withoutQuery] = list.Count;
                        // Also mark the first occurrence as JS-dynamic if there's a query string
                        isJsDynamic = true;
                    }
                }

                if (Utils.IsProbablyBinaryAsset(uri.AbsolutePath)) continue;

                var finalUrl = isExternal ? norm : norm;
                if (string.IsNullOrWhiteSpace(finalUrl)) continue;
                if (string.IsNullOrWhiteSpace(text)) text = finalUrl;

                // Deduplicate by normalised URL
                if (list.Any(x => x.Url.Equals(finalUrl, StringComparison.OrdinalIgnoreCase)))
                    continue;

                list.Add(new NavItem
                {
                    Url = finalUrl,
                    Title = text.Trim(),
                    Depth = 1,
                    ParentUrl = pageUrl,
                    IsExternal = isExternal,
                    IsJsDynamic = isJsDynamic
                });
            }
            catch { continue; }

            if (list.Count >= maxLinks)
                break;
        }

        return list;
    }

    private static string? NormalizeInternalUrl(
        string? raw,
        string currentUrl,
        string host,
        bool dropQueryStrings)
    {
        string? abs;

        if (string.IsNullOrWhiteSpace(currentUrl))
        {
            abs = raw;
        }
        else
        {
            abs = Utils.ToAbsoluteUrl(currentUrl, raw ?? "");
        }

        if (string.IsNullOrWhiteSpace(abs)) return null;

        abs = Utils.NormalizeUrl(abs, dropQueryStrings);
        if (!Utils.IsHttpUrl(abs)) return null;

        try
        {
            var u = new Uri(abs);

            if (!SameSiteHost(u.Host, host))
                return null;

            if (Utils.IsProbablyBinaryAsset(u.AbsolutePath))
                return null;

            return abs;
        }
        catch
        {
            return null;
        }
    }

    private static bool SameSiteHost(string a, string b)
    {
        static string Norm(string h)
        {
            h ??= "";
            h = h.Trim().ToLowerInvariant();
            if (h.StartsWith("www.", StringComparison.Ordinal))
                h = h[4..];
            return h;
        }

        return Norm(a) == Norm(b);
    }
}