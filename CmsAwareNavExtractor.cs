using System.Text.Json;
using Microsoft.Playwright;

namespace WebSnapshots;

public sealed class CmsAwareNavExtractor
{
    private readonly CmsDetectionResult _cms;
    private readonly Logger? _log;
    private readonly string _startUrl;

    // Track which pages have already had human-like preparation run so we
    // don't repeat the click/hover/scroll pass on the same IPage instance.
    private readonly HashSet<IPage> _preparedPages = new(ReferenceEqualityComparer.Instance);

    public CmsAwareNavExtractor(CmsDetectionResult cms, Logger? log = null, string startUrl = "")
    {
        _cms = cms;
        _log = log;
        _startUrl = startUrl;
    }

    public async Task<List<NavGroup>> ExtractStartPageNavGroupsAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxLinks)
    {
        await PreparePageForHumanLikeExtractionAsync(page);

        var groups = new List<NavGroup>();

        if (_cms.Kind == CmsKind.WordPress)
        {
            var wpNav = await ExtractWordPressNavAsync(page, pageUrl, host, dropQueryStrings, maxLinks);
            if (wpNav.Count > 0)
            {
                groups.Add(new NavGroup
                {
                    Id = "startpage_wp_nav",
                    Rank = 0,
                    LinkCount = wpNav.Count,
                    Flat = wpNav.Select(x => new NavItem
                    {
                        Url = x.Url,
                        Title = x.Text,
                        Depth = 1,
                        ParentUrl = pageUrl
                    }).ToList()
                });
            }
        }

        if (_cms.Kind == CmsKind.SiteVision)
        {
            var treeMenu = await ExtractSiteVisionTreeMenuAsync(page, pageUrl, host, dropQueryStrings, maxLinks);
            if (treeMenu.Count > 0)
            {
                groups.Add(new NavGroup
                {
                    Id = "startpage_treemenu",
                    Rank = 0,
                    LinkCount = treeMenu.Count,
                    Flat = treeMenu.Select(x => new NavItem
                    {
                        Url = x.Url,
                        Title = x.Text,
                        Depth = 1,
                        ParentUrl = pageUrl
                    }).ToList()
                });
            }

            var listing = await ExtractSiteVisionPageListingAsync(page, pageUrl, host, dropQueryStrings, maxLinks);
            if (listing.Count > 0)
            {
                groups.Add(new NavGroup
                {
                    Id = "startpage_page_listing",
                    Rank = 1,
                    LinkCount = listing.Count,
                    Flat = listing.Select(x => new NavItem
                    {
                        Url = x.Url,
                        Title = x.Text,
                        Depth = 1,
                        ParentUrl = pageUrl
                    }).ToList()
                });
            }
        }

        var topHeader = await ExtractTopHeaderStructuralAsync(page, pageUrl, host, dropQueryStrings, maxLinks);
        if (topHeader.Count > 0)
        {
            groups.Add(new NavGroup
            {
                Id = "startpage_top_header_nav",
                Rank = groups.Count == 0 ? 0 : 2,
                LinkCount = topHeader.Count,
                Flat = topHeader.Select(x => new NavItem
                {
                    Url = x.Url,
                    Title = x.Text,
                    Depth = 1,
                    ParentUrl = pageUrl
                }).ToList()
            });
        }

        var generic = await ExtractGenericStructuralAsync(page, pageUrl, host, dropQueryStrings, maxLinks);
        if (generic.Count > 0)
        {
            groups.Add(new NavGroup
            {
                Id = "startpage_best_nav",
                Rank = groups.Count == 0 ? 0 : 3,
                LinkCount = generic.Count,
                Flat = generic.Select(x => new NavItem
                {
                    Url = x.Url,
                    Title = x.Text,
                    Depth = 1,
                    ParentUrl = pageUrl
                }).ToList()
            });
        }

        foreach (var g in groups.OrderBy(x => x.Rank).ThenByDescending(x => x.LinkCount))
        {
            _log?.Event("NAV_GROUP_CANDIDATE",
                ("id", g.Id),
                ("rank", g.Rank),
                ("linkCount", g.LinkCount));
        }

        return groups;
    }

    public async Task<List<VisibleLinkGroup>> ExtractVisibleGroupsAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxPerGroup)
    {
        await PreparePageForHumanLikeExtractionAsync(page);

        return _cms.Kind switch
        {
            CmsKind.SiteVision => await ExtractSiteVisionVisibleGroupsAsync(page, pageUrl, host, dropQueryStrings, maxPerGroup),
            CmsKind.WordPress  => await ExtractWordPressVisibleGroupsAsync(page, pageUrl, host, dropQueryStrings, maxPerGroup),
            _ => await ExtractGenericVisibleGroupsAsync(page, pageUrl, host, dropQueryStrings, maxPerGroup)
        };
    }

    public async Task<List<PageNavLink>> ExtractChildLinksAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxLinks)
    {
        await PreparePageForHumanLikeExtractionAsync(page);

        var result = new List<PageNavLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool isStartPage = IsStartPage(pageUrl);

        _log?.Event("NAV_CHILDREN_STAGE",
            ("url", pageUrl),
            ("stage", "begin"),
            ("isStartPage", isStartPage),
            ("cms", _cms.Kind.ToString()),
            ("maxLinks", maxLinks));

        if (isStartPage)
        {
            _log?.Event("NAV_CHILDREN_STAGE", ("url", pageUrl), ("stage", "before_top_header"));

            var topHeader = await ExtractTopHeaderStructuralAsync(page, pageUrl, host, dropQueryStrings, maxLinks);

            _log?.Event("NAV_CHILDREN_STAGE",
                ("url", pageUrl),
                ("stage", "after_top_header"),
                ("count", topHeader.Count));

            foreach (var x in topHeader.OrderByDescending(x => x.Confidence))
            {
                if (seen.Add(x.Url))
                    result.Add(x);

                if (result.Count >= maxLinks)
                {
                    _log?.Event("NAV_CHILDREN_STAGE",
                        ("url", pageUrl),
                        ("stage", "return_after_top_header"),
                        ("count", result.Count));
                    return result;
                }
            }
        }
        else
        {
            _log?.Event("NAV_CHILDREN_STAGE", ("url", pageUrl), ("stage", "skip_top_header_inner_page"));
        }

        _log?.Event("NAV_CHILDREN_STAGE", ("url", pageUrl), ("stage", "before_generic_structural"));

        var structural = await ExtractGenericStructuralAsync(page, pageUrl, host, dropQueryStrings, maxLinks);

        _log?.Event("NAV_CHILDREN_STAGE",
            ("url", pageUrl),
            ("stage", "after_generic_structural"),
            ("count", structural.Count));

        foreach (var x in structural.OrderByDescending(x => x.Confidence))
        {
            if (seen.Add(x.Url))
                result.Add(x);

            if (result.Count >= maxLinks)
            {
                _log?.Event("NAV_CHILDREN_STAGE",
                    ("url", pageUrl),
                    ("stage", "return_after_generic_structural"),
                    ("count", result.Count));
                return result;
            }
        }

        if (!isStartPage)
        {
            _log?.Event("NAV_CHILDREN_STAGE",
                ("url", pageUrl),
                ("stage", "return_inner_structural_only"),
                ("count", result.Count),
                ("structural", result.Count(x => x.IsStructural)));

            return result;
        }

        _log?.Event("NAV_CHILDREN_STAGE", ("url", pageUrl), ("stage", "before_all_anchors"));

        var allAnchors = await ExtractGenericAllAnchorsAsync(page, pageUrl, host, dropQueryStrings, maxLinks * 2);

        _log?.Event("NAV_CHILDREN_STAGE",
            ("url", pageUrl),
            ("stage", "after_all_anchors"),
            ("count", allAnchors.Count));

        var exploratoryBudget = Math.Min(
            Math.Max(4, maxLinks / 6),
            Math.Max(0, maxLinks - result.Count));

        _log?.Event("NAV_CHILDREN_STAGE",
            ("url", pageUrl),
            ("stage", "exploratory_budget"),
            ("budget", exploratoryBudget),
            ("currentCount", result.Count));

        if (exploratoryBudget > 0)
        {
            foreach (var e in allAnchors
                .Where(x => !x.IsStructural)
                .OrderByDescending(x => x.Confidence)
                .ThenBy(x => x.Text, StringComparer.OrdinalIgnoreCase))
            {
                if (!seen.Add(e.Url))
                    continue;

                result.Add(e);

                if (result.Count >= maxLinks || exploratoryBudget-- <= 1)
                    break;
            }
        }

        _log?.Event("NAV_CHILDREN_STAGE",
            ("url", pageUrl),
            ("stage", "return_final"),
            ("count", result.Count),
            ("structural", result.Count(x => x.IsStructural)));

        return result;
    }

    private bool IsStartPage(string pageUrl)
    {
        // Match the configured start URL regardless of path depth.
        if (!string.IsNullOrEmpty(_startUrl) &&
            pageUrl.Equals(_startUrl, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var u = new Uri(pageUrl);
            var path = (u.AbsolutePath ?? "").Trim('/');
            // Also treat /index.html and /index.php as start-page equivalents.
            return string.IsNullOrWhiteSpace(path)
                || path.Equals("index.html", StringComparison.OrdinalIgnoreCase)
                || path.Equals("index.php", StringComparison.OrdinalIgnoreCase)
                || path.Equals("index.htm", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task PreparePageForHumanLikeExtractionAsync(IPage page)
    {
        // Skip if this page instance has already been prepared in this extraction session.
        if (!_preparedPages.Add(page))
            return;

        try
        {
            await page.EvaluateAsync(@"
async () => {
  const sleep = (ms) => new Promise(r => setTimeout(r, ms));

  const isVisible = (el) => {
    try {
      if (!el) return false;
      const cs = window.getComputedStyle(el);
      if (!cs) return false;
      if (cs.display === 'none' || cs.visibility === 'hidden' || cs.opacity === '0') return false;
      const r = el.getBoundingClientRect();
      if (!r) return false;
      if (r.width < 2 || r.height < 2) return false;
      return true;
    } catch {
      return false;
    }
  };

  try {
    document.querySelectorAll('details:not([open])').forEach(d => {
      try { d.open = true; } catch {}
    });
  } catch {}

  const expandableSelectors = [
    'button[aria-expanded=""false""]',
    '[role=""button""][aria-expanded=""false""]',
    '.accordion button',
    '.accordion [role=""button""]',
    '.menu button',
    '.submenu-toggle',
    '.nav-toggle',
    '.hamburger',
    '.menu-toggle',
    '[data-toggle=""collapse""]',
    '[data-bs-toggle=""collapse""]',
    '[aria-controls]',
    '[aria-haspopup=""true""]',
    '[aria-expanded]'
  ];

  const clicked = new Set();

  for (const sel of expandableSelectors) {
    let nodes = [];
    try { nodes = Array.from(document.querySelectorAll(sel)); } catch {}
    for (const el of nodes) {
      try {
        if (!isVisible(el)) continue;

        const key =
          (el.tagName || '') + '|' +
          (el.id || '') + '|' +
          ((typeof el.className === 'string' ? el.className : '') || '') + '|' +
          (el.getAttribute('aria-controls') || '');

        if (clicked.has(key)) continue;
        clicked.add(key);

        el.click();
        await sleep(40);
      } catch {}
    }
  }

  const hoverSelectors = [
    'header nav a[href]',
    'header [role=""navigation""] a[href]',
    '.menu a[href]',
    '.nav a[href]',
    '.navbar a[href]',
    '[aria-expanded=""true""]'
  ];

  for (const sel of hoverSelectors) {
    let nodes = [];
    try { nodes = Array.from(document.querySelectorAll(sel)).slice(0, 30); } catch {}
    for (const el of nodes) {
      try {
        if (!isVisible(el)) continue;
        el.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
        el.dispatchEvent(new MouseEvent('mouseenter', { bubbles: true }));
        await sleep(20);
      } catch {}
    }
  }

  const scrollPoints = [0, 250, 600, 1000, 1500];
  for (const y of scrollPoints) {
    try { window.scrollTo(0, y); } catch {}
    await sleep(55);
  }

  try { window.scrollTo(0, 0); } catch {}
  await sleep(90);
}
");
        }
        catch (Exception ex)
        {
            _log?.Warn($"NAV_PREPARE_WARN {ex.Message}");
        }
    }

    private async Task<List<PageNavLink>> ExtractSiteVisionTreeMenuAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxLinks)
    {
        var raw = await page.EvaluateAsync<JsonElement>(@"
() => {
  const results = [];
  const seen = new Set();

  const scripts = Array.from(document.querySelectorAll('script'));
  for (const s of scripts) {
    const txt = s.textContent || '';
    if (!txt.includes('TreeMenu')) continue;
    const itemsPos = txt.indexOf('""items""');
    if (itemsPos < 0) continue;

    const arrStart = txt.indexOf('[', itemsPos);
    if (arrStart < 0) continue;

    let depth = 0;
    let arrEnd = -1;
    for (let i = arrStart; i < txt.length; i++) {
      const ch = txt[i];
      if (ch === '[') depth++;
      else if (ch === ']') {
        depth--;
        if (depth === 0) {
          arrEnd = i;
          break;
        }
      }
    }

    if (arrEnd < 0) continue;

    const arrText = txt.slice(arrStart, arrEnd + 1);

    try {
      const items = JSON.parse(arrText);
      for (const it of items) {
        if (!it || !it.uri) continue;
        const abs = new URL(it.uri, document.location.href).toString();
        if (seen.has(abs)) continue;
        seen.add(abs);
        results.push({
          url: abs,
          text: it.displayName || it.uri,
          kind: 'treemenu',
          sourceType: 'TreeMenu',
          displayRole: 'Navigation',
          groupLabel: 'Main navigation',
          confidence: 0.99,
          isStructural: true
        });
      }
    } catch {}
  }

  return results;
}
");

        return ParsePageNavLinks(raw, host, dropQueryStrings, maxLinks);
    }

    private async Task<List<PageNavLink>> ExtractSiteVisionPageListingAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxLinks)
    {
        var raw = await page.EvaluateAsync<JsonElement>(@"
() => {
  const results = [];
  const seen = new Set();

  const roots = Array.from(document.querySelectorAll('nav.sol-page-listing, .sol-page-listing'));
  for (const root of roots) {
    const links = root.querySelectorAll('li.sol-page-listing-item > a[href], a.sol-page-listing-item__link[href]');
    for (const a of links) {
      const href = (a.getAttribute('href') || '').trim();
      if (!href) continue;

      const abs = new URL(href, document.location.href).toString();
      if (seen.has(abs)) continue;
      seen.add(abs);

      let text = '';
      const h = a.querySelector('h1,h2,h3,h4,h5,h6');
      if (h) text = (h.textContent || '').trim();
      if (!text) text = (a.textContent || '').trim();

      results.push({
        url: abs,
        text,
        kind: 'page-listing',
        sourceType: 'PageListing',
        displayRole: 'Navigation',
        groupLabel: 'Main navigation',
        confidence: 0.95,
        isStructural: true
      });
    }
  }

  return results;
}
");

        return ParsePageNavLinks(raw, host, dropQueryStrings, maxLinks);
    }

    private async Task<List<PageNavLink>> ExtractTopHeaderStructuralAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxLinks)
    {
        var raw = await page.EvaluateAsync<JsonElement>(@"
() => {
  const results = [];
  const seen = new Map();

  const norm = (s) => (s || '').replace(/\s+/g, ' ').trim();

  const isBadHref = (href) => {
    const h = (href || '').trim().toLowerCase();
    if (!h) return true;
    if (h === '#') return true;
    if (h.startsWith('#')) return true;
    if (h.startsWith('javascript:')) return true;
    if (h.startsWith('mailto:')) return true;
    if (h.startsWith('tel:')) return true;
    return false;
  };

  const isVisible = (el) => {
    try {
      if (!el) return false;
      const cs = window.getComputedStyle(el);
      if (!cs) return false;
      if (cs.display === 'none' || cs.visibility === 'hidden' || cs.opacity === '0') return false;
      const r = el.getBoundingClientRect();
      if (!r) return false;
      if (r.width < 2 || r.height < 2) return false;
      return true;
    } catch {
      return false;
    }
  };

  const pickText = (a) => {
    let text = norm(a.innerText || '');
    if (!text) text = norm(a.textContent || '');
    if (!text) text = norm(a.getAttribute('title') || '');
    if (!text) text = norm(a.getAttribute('aria-label') || '');
    if (!text) text = norm(a.getAttribute('href') || '');
    return text;
  };

  const scoreLink = (a) => {
    let score = 0.80;

    try {
      const r = a.getBoundingClientRect();
      if (r.top >= 0 && r.top < 260) score += 0.10;
      else if (r.top < 500) score += 0.04;

      if (r.left >= 0 && r.left < 1600) score += 0.03;
    } catch {}

    if (a.closest('header')) score += 0.08;
    if (a.closest('nav')) score += 0.06;
    if (a.closest('[role=""navigation""]')) score += 0.06;
    if (a.closest('.menu, .nav, .navbar, .main-nav, .top-nav, .site-nav')) score += 0.06;

    if (a.closest('.sol-tool-nav, .tools, .utility, .service-nav')) score -= 0.18;
    if (a.closest('footer')) score -= 0.30;
    if (a.closest('.breadcrumb, .breadcrumbs')) score -= 0.20;

    const txt = pickText(a);
    if (txt.length >= 4) score += 0.03;
    if (txt.length >= 9) score += 0.03;
    if (txt.length > 45) score -= 0.05;

    if (score > 0.999) score = 0.999;
    if (score < 0.05) score = 0.05;
    return score;
  };

  const roots = [
    ...document.querySelectorAll('header nav, header [role=""navigation""], header'),
    ...document.querySelectorAll('.main-nav, .top-nav, .site-nav, .navbar, .menu, nav[aria-label], [role=""navigation""]')
  ];

  const rootSeen = new Set();
  for (const root of roots) {
    if (!root || rootSeen.has(root)) continue;
    rootSeen.add(root);

    const links = Array.from(root.querySelectorAll('a[href]'));
    let visibleCount = 0;

    for (const a of links) {
      if (!isVisible(a)) continue;
      const href = (a.getAttribute('href') || '').trim();
      if (isBadHref(href)) continue;

      let abs = '';
      try { abs = new URL(href, document.location.href).toString(); } catch { continue; }

      const text = pickText(a);
      if (!text) continue;

      visibleCount++;

      const candidate = {
        url: abs,
        text,
        kind: 'top-header-nav',
        sourceType: 'TopHeaderNav',
        displayRole: 'Navigation',
        groupLabel: 'Main navigation',
        confidence: scoreLink(a),
        isStructural: true
      };

      const existing = seen.get(abs);
      if (!existing || candidate.confidence > existing.confidence)
        seen.set(abs, candidate);
    }

    if (visibleCount < 3)
      continue;
  }

  return Array.from(seen.values())
    .sort((a, b) => b.confidence - a.confidence);
}
");

        return ParsePageNavLinks(raw, host, dropQueryStrings, maxLinks);
    }

    private async Task<List<VisibleLinkGroup>> ExtractSiteVisionVisibleGroupsAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxPerGroup)
    {
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

        var promos = await ExtractSiteVisionPromoCardsAsync(page, pageUrl, host, dropQueryStrings, maxPerGroup);
        if (promos.Flat.Count > 0) groups.Add(promos);

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

        // Generic heading-based fallback: fills any group that the narrower
        // selector-based extractors above missed (e.g. Nyheter / Driftinformation
        // on municipalities that use non-standard sol-* CSS classes).
        var existingIds = new HashSet<string>(groups.Select(g => g.Id), StringComparer.OrdinalIgnoreCase);
        var fallback    = await ExtractSiteVisionVisibleModuleFallbackAsync(page, pageUrl, host, dropQueryStrings, maxPerGroup);
        var addedCount  = 0;

        foreach (var fb in fallback)
        {
            if (existingIds.Contains(fb.Id)) continue;   // already covered by a specialised extractor
            groups.Add(fb);
            existingIds.Add(fb.Id);
            addedCount++;
        }

        if (addedCount > 0)
            _log?.Event("VISIBLE_MODULE_FALLBACK_USED",
                ("host",        host),
                ("groupsAdded", addedCount));

        return groups.OrderBy(x => x.Order).ToList();
    }

    private async Task<VisibleLinkGroup> ExtractSiteVisionAlertsAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxPerGroup)
    {
        var html = await page.ContentAsync();
        var group = new VisibleLinkGroup
        {
            Id = "alerts",
            Label = "Störningar",
            Role = "Alert",
            Order = 15,
            Flat = new List<NavItem>()
        };

        var marker = "Soleil.webapps['InfoMessages'].render";
        var idx = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return group;

        var messagesKey = @"""messages"":";
        var mIdx = html.IndexOf(messagesKey, idx, StringComparison.OrdinalIgnoreCase);
        if (mIdx < 0) return group;

        var arrStart = html.IndexOf('[', mIdx);
        if (arrStart < 0) return group;

        int depth = 0;
        int arrEnd = -1;
        for (int i = arrStart; i < html.Length; i++)
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

        if (arrEnd < 0) return group;

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
                    ParentUrl = pageUrl,
                    IsDisplayOnly = IsDisplayOnlyUrl(url, host)
                });

                if (group.Flat.Count >= maxPerGroup)
                    break;
            }
        }
        catch (Exception ex)
        {
            _log?.Warn($"SITEVISION_ALERT_PARSE_FAIL {ex.Message}");
        }

        return group;
    }

    private async Task<VisibleLinkGroup> ExtractSiteVisionEventsAsync(
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

        var raw = await page.EvaluateAsync<JsonElement>(@"
() => {
  const results = [];
  const seen = new Set();

  const candidates = Array.from(document.querySelectorAll(
    '.sol-event-item a[href], .eventslist a[href], [data-portlet-id] a[href], .sv-custom-module a[href]'
  ));

  for (const a of candidates) {
    const href = (a.getAttribute('href') || '').trim();
    if (!href) continue;
    const abs = new URL(href, document.location.href).toString();
    if (seen.has(abs)) continue;

    const text = (a.textContent || '').replace(/\s+/g,' ').trim();
    if (!text) continue;

    const root = a.closest('[id], section, article, div');
    const rootText = ((root && root.textContent) || '').toLowerCase();

    const looksEventish =
      rootText.includes('evenemang') ||
      rootText.includes('aktivitet') ||
      rootText.includes('bibliotek') ||
      /\b\d{4}-\d{2}-\d{2}\b/.test(rootText);

    if (!looksEventish) continue;

    seen.add(abs);
    results.push({ url: abs, text });
  }

  return results;
}
");

        var links = ParseFlatLinks(raw, host, dropQueryStrings, maxPerGroup);
        foreach (var x in links)
        {
            group.Flat.Add(new NavItem
            {
                Url = x.Url,
                Title = x.Text,
                Depth = 1,
                ParentUrl = pageUrl,
                IsDisplayOnly = IsDisplayOnlyUrl(x.Url, host)
            });
        }

        if (group.Flat.Count == 0)
        {
            var more = await ExtractGroupBySelectorAsync(
                page, pageUrl, host, dropQueryStrings,
                "#h-Evenemang ~ * a[href], a[href*='/arkiv/evenemang']",
                "events", "Evenemang", "Events", 30, maxPerGroup);

            foreach (var x in more.Flat)
            {
                if (!group.Flat.Any(y => y.Url.Equals(x.Url, StringComparison.OrdinalIgnoreCase)))
                    group.Flat.Add(x);
            }
        }

        return group;
    }

    private async Task<VisibleLinkGroup> ExtractSiteVisionPromoCardsAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxPerGroup)
    {
        var group = new VisibleLinkGroup
        {
            Id = "promocards",
            Label = "Startsidans puffar",
            Role = "PromoCard",
            Order = 40,
            Flat = new List<NavItem>()
        };

        var raw = await page.EvaluateAsync<JsonElement>(@"
() => {
  const results = [];
  const seen = new Set();

  const roots = Array.from(document.querySelectorAll('.sol-startpage-widgets .sol-widget-decoration-wrapper'));
  for (const root of roots) {
    let a = root.querySelector('h1 a[href], h2 a[href], h3 a[href], h4 a[href]');
    if (!a) a = root.querySelector('a[href]');
    if (!a) continue;

    const href = (a.getAttribute('href') || '').trim();
    if (!href) continue;

    const abs = new URL(href, document.location.href).toString();
    if (seen.has(abs)) continue;
    seen.add(abs);

    let text = (a.textContent || '').replace(/\s+/g,' ').trim();
    if (!text) text = href;

    results.push({ url: abs, text });
  }

  return results;
}
");

        var links = ParseFlatLinks(raw, host, dropQueryStrings, maxPerGroup);
        foreach (var x in links)
        {
            group.Flat.Add(new NavItem
            {
                Url = x.Url,
                Title = x.Text,
                Depth = 1,
                ParentUrl = pageUrl,
                IsDisplayOnly = IsDisplayOnlyUrl(x.Url, host)
            });
        }

        return group;
    }

    // Generic DOM-walking fallback for SiteVision start pages.
    //
    // Scans heading elements in the main content area (excluding header, nav,
    // footer, breadcrumbs, cookie banners) and groups nearby links by heading
    // keyword classification. Used to fill gaps not covered by the narrower
    // selector-based extractors above — e.g. when a municipality's Nyheter or
    // Driftinformation section uses different CSS classes than the sol-* defaults.
    private async Task<List<VisibleLinkGroup>> ExtractSiteVisionVisibleModuleFallbackAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxPerGroup)
    {
        JsonElement raw;
        try
        {
            raw = await page.EvaluateAsync<JsonElement>(@"
() => {
  // Heading text → stable group classification.
  const KNOWN = [
    { pat: /^\s*(hitta\s+snabbt|snabba\s+vägar)\s*$/i,                             id: 'quicklinks', label: 'Hitta snabbt',     role: 'HomepageModule', order: 8 },
    { pat: /^\s*(nyheter|aktuellt)\s*$/i,                                           id: 'news',      label: 'Nyheter',          role: 'News',      order: 20 },
    { pat: /^\s*(evenemang|händelser?)\s*$/i,                                       id: 'events',    label: 'Evenemang',        role: 'Events',    order: 30 },
    { pat: /^\s*(driftinformation|driftstörningar?|störningar?)\s*$/i,              id: 'alerts',    label: 'Driftinformation', role: 'Alert',     order: 15 },
    { pat: /^\s*(genvägar|populära\s+sidor|snabblänkar)\s*$/i,                      id: 'shortcuts', label: 'Genvägar',         role: 'Shortcut',  order: 10 },
    { pat: /^\s*(kontakt(a\s+(oss|kommunen))?|kontaktuppgifter)\s*$/i,              id: 'contact',   label: 'Kontakt',          role: 'Contact',   order: 50 }
  ];

  // Zones that are never part of visible start-page content.
  const EXCL_SELS = [
    'header', 'footer', 'nav',
    '[role=""banner""]', '[role=""navigation""]', '[role=""contentinfo""]',
    '.sol-header', '.sol-footer', '.sol-menu', '.sol-breadcrumb',
    '.sol-cookie', '.sol-cookie-banner', '.sol-tool-nav',
    '.sv-nav', '.sv-menu', '#CookieConsent', '.cookie-banner'
  ];

  const excl = [];
  for (const s of EXCL_SELS) {
    try { excl.push(...document.querySelectorAll(s)); } catch {}
  }
  const isExcl = el => excl.some(ex => ex === el || ex.contains(el));

  const badHref = h => {
    const v = (h || '').trim().toLowerCase();
    return !v || v === '#' || v.startsWith('#') ||
           v.startsWith('javascript:') || v.startsWith('mailto:') || v.startsWith('tel:');
  };

  // All content headings, filtered out of excluded zones.
  // h5 is included for sites that use it for portlet-level headings.
  const headings = Array.from(document.querySelectorAll('h2, h3, h4, h5'))
    .filter(h => !isExcl(h));

  const modules = [];

  for (const heading of headings) {
    const headText = (heading.textContent || '').replace(/\s+/g, ' ').trim();
    const cls = KNOWN.find(k => k.pat.test(headText));
    if (!cls) continue;

    // Find the smallest ancestor that contains 1–40 anchor tags.
    // This naturally stops at the column/portlet level in a multi-column
    // layout rather than accidentally expanding to the whole page.
    let block = heading.parentElement;
    let found = false;
    for (let i = 0; i < 6 && block && block !== document.body; i++) {
      const n = block.querySelectorAll('a[href]').length;
      if (n >= 1 && n <= 40) { found = true; break; }
      if (n > 40) break;          // too broad — stop searching up
      block = block.parentElement;
    }
    if (!found) continue;

    const seen = new Set();
    const links = [];
    for (const a of block.querySelectorAll('a[href]')) {
      if (isExcl(a)) continue;
      const href = (a.getAttribute('href') || '').trim();
      if (badHref(href)) continue;
      let abs;
      try { abs = new URL(href, document.location.href).toString(); } catch { continue; }
      if (seen.has(abs)) continue;
      seen.add(abs);
      const text = (a.textContent || '').replace(/\s+/g, ' ').trim() || href;
      links.push({ url: abs, text });
    }

    // HomepageModule groups use the actual heading text so the viewer shows
    // the exact heading (e.g. Hitta snabbt) rather than the generic fallback label.
    const label = cls.role === 'HomepageModule' ? headText : cls.label;
    if (links.length === 0) {
      modules.push({ id: cls.id, label, role: cls.role, order: cls.order, headingText: headText, links: [], rejected: true });
      continue;
    }
    modules.push({ id: cls.id, label, role: cls.role, order: cls.order, headingText: headText, links, rejected: false });
  }

  return modules;
}
");
        }
        catch (Exception ex)
        {
            _log?.Warn($"VISIBLE_MODULE_FALLBACK_JS_FAIL {ex.Message}");
            return new List<VisibleLinkGroup>();
        }

        if (raw.ValueKind != JsonValueKind.Array)
            return new List<VisibleLinkGroup>();

        // Merge all JS-returned modules into per-id groups (multiple headings
        // for the same role are unified; links are deduplicated within each group).
        var byId = new Dictionary<string, VisibleLinkGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in raw.EnumerateArray())
        {
            var id    = mod.TryGetProperty("id",          out var idP)  ? (idP.GetString()   ?? "") : "";
            var label = mod.TryGetProperty("label",        out var lblP) ? (lblP.GetString()  ?? "") : id;
            var role  = mod.TryGetProperty("role",         out var rolP) ? (rolP.GetString()  ?? "") : "";
            var order = mod.TryGetProperty("order",        out var ordP)
                        && ordP.ValueKind == JsonValueKind.Number ? ordP.GetInt32() : 99;
            var htxt  = mod.TryGetProperty("headingText",  out var htP)  ? (htP.GetString()   ?? "") : "";

            if (string.IsNullOrWhiteSpace(id)) continue;

            var isRejected = mod.TryGetProperty("rejected", out var rejP) && rejP.ValueKind == JsonValueKind.True;

            _log?.Event("HOMEPAGE_MODULE_CANDIDATE",
                ("headingText", htxt),
                ("groupId",     id),
                ("role",        role),
                ("decision",    isRejected ? "rejected" : "accepted"),
                ("reason",      isRejected ? "no_links_found" : "heading_matched_known_pattern_with_links"));

            if (isRejected)
            {
                _log?.Event("HOMEPAGE_MODULE_REJECTED",
                    ("headingText", htxt),
                    ("groupId",     id),
                    ("role",        role),
                    ("reason",      "no_links_found"),
                    ("positiveEvidence", "heading_matched_known_module_pattern"),
                    ("negativeEvidence", "zero_links_in_block"));
                continue;
            }

            if (!byId.TryGetValue(id, out var grp))
            {
                grp = new VisibleLinkGroup { Id = id, Label = label, Role = role, Order = order, Flat = new List<NavItem>() };
                byId[id] = grp;

                if (!string.IsNullOrWhiteSpace(role))
                    _log?.Event("HOMEPAGE_CANDIDATE_ROLE_ASSIGNED",
                        ("headingText",    htxt),
                        ("groupId",        id),
                        ("role",           role),
                        ("confidence",     "1.0"),
                        ("positiveEvidence", "heading_matched_known_module_pattern"),
                        ("negativeEvidence", ""),
                        ("reason",         "heading_text_matches_known_homepage_module_pattern"));
            }

            if (!mod.TryGetProperty("links", out var linksEl) || linksEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var lnk in linksEl.EnumerateArray())
            {
                if (grp.Flat.Count >= maxPerGroup) break;

                var urlRaw = lnk.TryGetProperty("url",  out var uP) ? (uP.GetString() ?? "") : "";
                var text   = lnk.TryGetProperty("text", out var tP) ? (tP.GetString() ?? "") : "";

                var url = NormalizeInternalUrl(urlRaw, pageUrl, host, dropQueryStrings);
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (grp.Flat.Any(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase))) continue;

                grp.Flat.Add(new NavItem
                {
                    Url           = url,
                    Title         = string.IsNullOrWhiteSpace(text) ? url : text.Trim(),
                    Depth         = 1,
                    ParentUrl     = pageUrl,
                    IsDisplayOnly = IsDisplayOnlyUrl(url, host)
                });
            }

            _log?.Event("HOMEPAGE_MODULE_ACCEPTED",
                ("headingText", htxt),
                ("groupId",     id),
                ("role",        role),
                ("linksAdded",  grp.Flat.Count),
                ("positiveEvidence", "heading_matched_known_module_pattern|has_links"),
                ("negativeEvidence", ""));

            _log?.Event("VISIBLE_MODULE_FALLBACK_GROUP_FOUND",
                ("headingText", htxt),
                ("groupId",     id),
                ("linksFound",  grp.Flat.Count));
        }

        return byId.Values
            .Where(g => g.Flat.Count > 0)
            .OrderBy(g => g.Order)
            .ToList();
    }

    private async Task<List<VisibleLinkGroup>> ExtractGenericVisibleGroupsAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxPerGroup)
    {
        var groups = new List<VisibleLinkGroup>();

        var utility = await ExtractGroupBySelectorAsync(
            page, pageUrl, host, dropQueryStrings,
            "header a[href], nav[aria-label] a[href]",
            "utility", "Verktyg", "Utility", 50, maxPerGroup);
        if (utility.Flat.Count > 0) groups.Add(utility);

        var footer = await ExtractGroupBySelectorAsync(
            page, pageUrl, host, dropQueryStrings,
            "footer a[href]",
            "footer", "Footer", "FooterLinks", 60, maxPerGroup);
        if (footer.Flat.Count > 0) groups.Add(footer);

        var existingIds = new HashSet<string>(groups.Select(g => g.Id), StringComparer.OrdinalIgnoreCase);
        var modules = await ExtractGenericQuickLinkModulesAsync(page, pageUrl, host, dropQueryStrings, maxPerGroup);
        foreach (var m in modules)
        {
            if (!existingIds.Contains(m.Id))
                groups.Add(m);
        }

        return groups;
    }

    private async Task<VisibleLinkGroup> ExtractGroupBySelectorAsync(
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

  const els = Array.from(document.querySelectorAll(selector));
  for (const a of els) {
    const href = (a.getAttribute('href') || '').trim();
    if (!href) continue;
    let abs = '';
    try { abs = new URL(href, document.location.href).toString(); } catch { continue; }
    if (seen.has(abs)) continue;
    seen.add(abs);

    let text = (a.textContent || '').replace(/\s+/g,' ').trim();
    if (!text) {
      text = (a.getAttribute('title') || '').trim();
    }
    if (!text) text = href;

    out.push({ url: abs, text });
  }

  return out;
}
", selector);

        var links = ParseFlatLinks(raw, host, dropQueryStrings, maxPerGroup);

        return new VisibleLinkGroup
        {
            Id = id,
            Label = label,
            Role = role,
            Order = order,
            Flat = links.Select(x => new NavItem
            {
                Url = x.Url,
                Title = x.Text,
                Depth = 1,
                ParentUrl = pageUrl,
                IsDisplayOnly = IsDisplayOnlyUrl(x.Url, host)
            }).ToList()
        };
    }

    private async Task<List<PageNavLink>> ExtractGenericStructuralAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxLinks)
    {
        _log?.Event("NAV_EXTRACTOR_CALL",
            ("url", pageUrl),
            ("extractor", "ExtractGenericStructuralAsync"),
            ("stage", "before_js"));

        var raw = await page.EvaluateAsync<JsonElement>(@"
(pageUrl) => {
  const seen = new Map();

  const urlObj = new URL(pageUrl, document.location.href);
  const isStartPage = !urlObj.pathname || urlObj.pathname === '/' || urlObj.pathname.trim() === '';

  const norm = (s) => (s || '').replace(/\s+/g,' ').trim();

  const isBadHref = (href) => {
    const h = (href || '').trim().toLowerCase();
    if (!h) return true;
    if (h === '#') return true;
    if (h.startsWith('#')) return true;
    if (h.startsWith('javascript:')) return true;
    if (h.startsWith('mailto:')) return true;
    if (h.startsWith('tel:')) return true;
    return false;
  };

  const isVisible = (el) => {
    try {
      if (!el) return false;
      const cs = window.getComputedStyle(el);
      if (!cs) return false;
      if (cs.display === 'none' || cs.visibility === 'hidden' || cs.opacity === '0') return false;
      const r = el.getBoundingClientRect();
      if (!r) return false;
      if (r.width < 2 || r.height < 2) return false;
      return true;
    } catch {
      return false;
    }
  };

  const rectTop = (el) => {
    try { return el.getBoundingClientRect().top; } catch { return 999999; }
  };

  const rectLeft = (el) => {
    try { return el.getBoundingClientRect().left; } catch { return 999999; }
  };

  const pickText = (a) => {
    let text = norm(a.innerText || '');
    if (!text) text = norm(a.textContent || '');
    if (!text) text = norm(a.getAttribute('title') || '');
    if (!text) text = norm(a.getAttribute('aria-label') || '');
    if (!text) text = norm(a.getAttribute('href') || '');
    return text;
  };

  const countVisibleLinks = (root) => {
    try {
      let n = 0;
      for (const a of root.querySelectorAll('a[href]')) {
        if (isVisible(a)) n++;
      }
      return n;
    } catch {
      return 0;
    }
  };

  const closestStructuralContainer = (a) => {
    const selectors = isStartPage
      ? [
          '.sol-page-listing',
          'aside',
          '.sidebar',
          '.sidenav',
          '.breadcrumb',
          '.breadcrumbs',
          'nav',
          '[role=""navigation""]',
          '.menu',
          '.nav',
          '.navbar',
          'header'
        ]
      : [
          '.sol-page-listing',
          'aside',
          '.sidebar',
          '.sidenav',
          '.breadcrumb',
          '.breadcrumbs'
        ];

    for (const sel of selectors) {
      try {
        const c = a.closest(sel);
        if (c && isVisible(c)) return c;
      } catch {}
    }

    return null;
  };

  const isNoisePath = (abs) => {
    try {
      const u = new URL(abs, document.location.href);
      const p = (u.pathname || '').toLowerCase();
      return (
        p === '/webbkarta' ||
        p.startsWith('/webbkarta/') ||
        p.endsWith('/rss') ||
        p.includes('/nyhetsarkiv/') ||
        p.includes('/driftinformation')
      );
    } catch {
      return false;
    }
  };

  const classify = (a, abs) => {
    if (isNoisePath(abs)) {
      return { kind:'noise', sourceType:'Noise', displayRole:'VisibleContent', groupLabel:'Filtered', confidence:0.01, isStructural:false, container:null };
    }

    const container = closestStructuralContainer(a);

    if (a.closest('.breadcrumb, .breadcrumbs, nav[aria-label*=""breadcrumb"" i], [aria-label*=""breadcrumb"" i]')) {
      return { kind:'breadcrumb', sourceType:'Breadcrumb', displayRole:'Navigation', groupLabel:'Breadcrumb', confidence:0.96, isStructural:true, container };
    }

    if (a.closest('nav.sol-page-listing, .sol-page-listing')) {
      return { kind:'page-listing', sourceType:'PageListing', displayRole:'Navigation', groupLabel:'Main navigation', confidence:0.95, isStructural:true, container };
    }

    if (a.closest('aside, .sidebar, .sidenav')) {
      return { kind:'aside', sourceType:'LocalNav', displayRole:'Navigation', groupLabel:'Local navigation', confidence:0.87, isStructural:true, container };
    }

    if (isStartPage && a.closest('header nav, header [role=""navigation""]')) {
      return { kind:'header-nav', sourceType:'HeaderNav', displayRole:'Navigation', groupLabel:'Main navigation', confidence:0.93, isStructural:true, container };
    }

    if (isStartPage && a.closest('nav, [role=""navigation""]')) {
      return { kind:'nav', sourceType:'MainNav', displayRole:'Navigation', groupLabel:'Main navigation', confidence:0.91, isStructural:true, container };
    }

    if (!isStartPage) {
      return { kind:'content', sourceType:'Generic', displayRole:'VisibleContent', groupLabel:'Visible content', confidence:0.05, isStructural:false, container:null };
    }

    if (container) {
      const linkCount = countVisibleLinks(container);
      if (linkCount >= 4) {
        return { kind:'grouped', sourceType:'VisualGroup', displayRole:'Navigation', groupLabel:'Visible navigation', confidence:0.70, isStructural:true, container };
      }
    }

    return { kind:'structural', sourceType:'Structural', displayRole:'Navigation', groupLabel:'Visible navigation', confidence:0.55, isStructural:true, container };
  };

  const scoreAdjust = (a, container, seed, text) => {
    let score = seed.confidence || 0.5;

    if (isVisible(a)) score += 0.04;
    if ((text || '').length >= 6) score += 0.02;
    if ((text || '').length >= 14) score += 0.02;

    const top = rectTop(a);
    const left = rectLeft(a);

    if (top >= 0 && top < 320) score += 0.06;
    else if (top < 700) score += 0.04;
    else if (top > 1600) score -= 0.04;

    if (left >= 0 && left < 500) score += 0.02;

    const linkCount = container ? countVisibleLinks(container) : 0;
    if (linkCount >= 4) score += 0.04;
    if (linkCount >= 8) score += 0.04;

    if (a.closest('.sol-tool-nav, .utility, .service-nav, footer')) score -= 0.18;

    if (score > 0.999) score = 0.999;
    if (score < 0.01) score = 0.01;

    return score;
  };

  for (const a of document.querySelectorAll('a[href]')) {
    const href = (a.getAttribute('href') || '').trim();
    if (isBadHref(href)) continue;
    if (!isVisible(a)) continue;

    let abs = '';
    try { abs = new URL(href, document.location.href).toString(); } catch { continue; }

    const text = pickText(a);
    const seed = classify(a, abs);

    if (!seed.isStructural)
      continue;

    const confidence = scoreAdjust(a, seed.container, seed, text);

    const existing = seen.get(abs);
    const candidate = {
      url: abs,
      text,
      kind: seed.kind,
      sourceType: seed.sourceType,
      displayRole: seed.displayRole,
      groupLabel: seed.groupLabel,
      confidence,
      isStructural: true
    };

    if (!existing || candidate.confidence > existing.confidence) {
      seen.set(abs, candidate);
    }
  }

  return Array.from(seen.values())
    .sort((a, b) => {
      if (b.confidence !== a.confidence) return b.confidence - a.confidence;
      return (a.text || '').localeCompare(b.text || '');
    });
}
", pageUrl);

        _log?.Event("NAV_EXTRACTOR_CALL",
            ("url", pageUrl),
            ("extractor", "ExtractGenericStructuralAsync"),
            ("stage", "after_js"),
            ("rawKind", raw.ValueKind.ToString()));

        var parsed = ParsePageNavLinks(raw, host, dropQueryStrings, maxLinks);

        _log?.Event("NAV_EXTRACTOR_CALL",
            ("url", pageUrl),
            ("extractor", "ExtractGenericStructuralAsync"),
            ("stage", "after_parse"),
            ("count", parsed.Count));

        return parsed;
    }

    private async Task<List<PageNavLink>> ExtractGenericAllAnchorsAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxLinks)
    {
        _log?.Event("NAV_EXTRACTOR_CALL",
            ("url", pageUrl),
            ("extractor", "ExtractGenericAllAnchorsAsync"),
            ("stage", "before_js"));

        var raw = await page.EvaluateAsync<JsonElement>(@"
() => {
  const seen = new Map();

  const norm = (s) => (s || '').replace(/\s+/g,' ').trim();

  const isBadHref = (href) => {
    const h = (href || '').trim().toLowerCase();
    if (!h) return true;
    if (h === '#') return true;
    if (h.startsWith('#')) return true;
    if (h.startsWith('javascript:')) return true;
    if (h.startsWith('mailto:')) return true;
    if (h.startsWith('tel:')) return true;
    return false;
  };

  const isVisible = (el) => {
    try {
      if (!el) return false;
      const cs = window.getComputedStyle(el);
      if (!cs) return false;
      if (cs.display === 'none' || cs.visibility === 'hidden' || cs.opacity === '0') return false;
      const r = el.getBoundingClientRect();
      if (!r) return false;
      if (r.width < 2 || r.height < 2) return false;
      return true;
    } catch {
      return false;
    }
  };

  const score = (a, text) => {
    let s = 0.10;
    if (isVisible(a)) s += 0.06;

    try {
      const r = a.getBoundingClientRect();
      if (r.top >= 0 && r.top < 900) s += 0.05;
      if (r.left >= 0 && r.left < 600) s += 0.03;
    } catch {}

    if ((text || '').length >= 8) s += 0.02;
    if ((text || '').length >= 16) s += 0.02;

    if (a.closest('main, article, section')) s += 0.02;
    if (a.closest('footer')) s -= 0.04;

    if (s > 0.999) s = 0.999;
    return s;
  };

  for (const a of document.querySelectorAll('a[href]')) {
    const href = (a.getAttribute('href') || '').trim();
    if (isBadHref(href)) continue;

    let abs = '';
    try { abs = new URL(href, document.location.href).toString(); } catch { continue; }

    let text = norm(a.innerText || '');
    if (!text) text = norm(a.textContent || '');
    if (!text) text = norm(a.getAttribute('title') || '');
    if (!text) text = norm(a.getAttribute('aria-label') || '');
    if (!text) text = href;

    const candidate = {
      url: abs,
      text,
      kind: 'content',
      sourceType: 'Generic',
      displayRole: 'VisibleContent',
      groupLabel: 'Visible content',
      confidence: score(a, text),
      isStructural: false
    };

    const existing = seen.get(abs);
    if (!existing || candidate.confidence > existing.confidence)
      seen.set(abs, candidate);
  }

  return Array.from(seen.values())
    .sort((a, b) => b.confidence - a.confidence);
}
");

        _log?.Event("NAV_EXTRACTOR_CALL",
            ("url", pageUrl),
            ("extractor", "ExtractGenericAllAnchorsAsync"),
            ("stage", "after_js"),
            ("rawKind", raw.ValueKind.ToString()));

        var parsed = ParsePageNavLinks(raw, host, dropQueryStrings, maxLinks);

        _log?.Event("NAV_EXTRACTOR_CALL",
            ("url", pageUrl),
            ("extractor", "ExtractGenericAllAnchorsAsync"),
            ("stage", "after_parse"),
            ("count", parsed.Count));

        return parsed;
    }

    private List<PageNavLink> ParsePageNavLinks(
        JsonElement raw,
        string host,
        bool dropQueryStrings,
        int maxLinks)
    {
        var list = new List<PageNavLink>();
        if (raw.ValueKind != JsonValueKind.Array)
            return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            if (!seen.Add(url)) continue;
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

    private List<PageNavLink> ParseFlatLinks(
        JsonElement raw,
        string host,
        bool dropQueryStrings,
        int maxLinks)
    {
        var list = new List<PageNavLink>();
        if (raw.ValueKind != JsonValueKind.Array)
            return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in raw.EnumerateArray())
        {
            var urlRaw = item.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
            var text = item.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";

            var url = NormalizeInternalUrl(urlRaw, "", host, dropQueryStrings);
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (!seen.Add(url)) continue;
            if (string.IsNullOrWhiteSpace(text)) text = url;

            list.Add(new PageNavLink
            {
                Url = url,
                Text = text.Trim()
            });

            if (list.Count >= maxLinks)
                break;
        }

        return list;
    }

    private static string? NormalizeInternalUrl(
        string? raw,
        string currentUrl,
        string host,
        bool dropQueryStrings,
        bool relaxNoiseFilter = false)
    {
        var abs = string.IsNullOrWhiteSpace(currentUrl)
            ? raw
            : Utils.ToAbsoluteUrl(currentUrl, raw ?? "");

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

            var path = (u.AbsolutePath ?? "").TrimEnd('/');

            // The noise filter is intentionally relaxed for visible-group link
            // collection: news article and driftinformation URLs are exactly the
            // content we want to surface in those start-page modules.
            if (!relaxNoiseFilter && IsNoisePath(path))
                return null;

            if (!string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                var b = new UriBuilder(abs) { Host = host };
                abs = b.Uri.ToString().TrimEnd('/');
            }

            return abs;
        }
        catch
        {
            return null;
        }
    }

    // Single authoritative noise-path filter used by both C# and as a replacement
    // for the previously duplicated inline JS isNoisePath function.
    private static bool IsNoisePath(string path)
    {
        var p = path.TrimEnd('/');

        // SiteVision / Swedish municipality noise
        if (p.Equals("/webbkarta", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("/webbkarta/", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.EndsWith("/rss", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Contains("/nyhetsarkiv/", StringComparison.OrdinalIgnoreCase)) return true;
        // Block individual drift-notice articles (path segment /driftinformation/ with
        // trailing slash) but not the section-index page
        // (e.g. /driftinformation.4.xxx.html or /driftinformation at root level).
        if (p.Contains("/driftinformation/", StringComparison.OrdinalIgnoreCase)) return true;

        // WordPress admin / system paths — never worth archiving
        if (p.Equals("/wp-admin", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("/wp-admin/", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Equals("/wp-login.php", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("/wp-json/", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    // ── WordPress extraction ──────────────────────────────────────────────────

    // Tries the WordPress REST API first (wp-json/wp/v2/pages), then falls back
    // to DOM-based extraction using WordPress-specific nav selectors.
    private async Task<List<PageNavLink>> ExtractWordPressNavAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxLinks)
    {
        // REST API attempt — runs inside the browser so session cookies apply.
        var restResults = await page.EvaluateAsync<JsonElement>(@"
async () => {
  const base = document.location.origin;
  const results = [];
  const seen = new Set();

  const addResult = (url, text, confidence) => {
    if (!url || seen.has(url)) return;
    seen.add(url);
    results.push({ url, text: (text || url).replace(/\s+/g, ' ').trim(), confidence,
      kind: 'wp-nav', sourceType: 'WordPress', displayRole: 'Navigation',
      groupLabel: 'Main navigation', isStructural: true });
  };

  // Try WP Menu API (WP 5.9+ or WP REST API menus plugin)
  try {
    const menusResp = await fetch(base + '/wp-json/menus/v1/menus',
      { headers: { Accept: 'application/json' } });
    if (menusResp.ok) {
      const menus = await menusResp.json();
      if (Array.isArray(menus) && menus.length > 0) {
        const primary = menus.find(m =>
          /primary|main|header|nav/i.test((m.slug || '') + (m.name || ''))
        ) || menus[0];
        const itemsResp = await fetch(
          base + '/wp-json/menus/v1/menus/' + primary.slug + '/items',
          { headers: { Accept: 'application/json' } });
        if (itemsResp.ok) {
          const items = await itemsResp.json();
          if (Array.isArray(items)) {
            for (const item of items.sort((a,b) => (a.menu_order||0)-(b.menu_order||0)))
              addResult(item.url, item.title, 0.99);
          }
        }
        if (results.length > 0) return results;
      }
    }
  } catch {}

  // Try WP pages REST endpoint (always available on self-hosted WP)
  try {
    const resp = await fetch(
      base + '/wp-json/wp/v2/pages?per_page=100&_fields=id,link,parent,title,menu_order,status&status=publish',
      { headers: { Accept: 'application/json' } });
    if (resp.ok) {
      const pages = await resp.json();
      if (Array.isArray(pages) && pages.length > 0) {
        for (const p of pages.sort((a,b) => (a.menu_order||0)-(b.menu_order||0)))
          addResult(p.link, p.title && p.title.rendered, 0.95);
        return results;
      }
    }
  } catch {}

  return results;
}
");

        var restLinks = ParsePageNavLinks(restResults, host, dropQueryStrings, maxLinks);
        if (restLinks.Count > 0)
        {
            _log?.Event("WP_REST_NAV", ("url", pageUrl), ("count", restLinks.Count));
            return restLinks;
        }

        // DOM fallback — WordPress-specific nav selectors scored by specificity.
        var domRaw = await page.EvaluateAsync<JsonElement>(@"
() => {
  const seen = new Map();
  const norm = s => (s || '').replace(/\s+/g, ' ').trim();

  const isBad = href => {
    const h = (href || '').trim().toLowerCase();
    return !h || h === '#' || h.startsWith('#') ||
           h.startsWith('javascript:') || h.startsWith('mailto:') || h.startsWith('tel:');
  };

  const isVisible = el => {
    try {
      const cs = window.getComputedStyle(el);
      if (cs.display === 'none' || cs.visibility === 'hidden' || cs.opacity === '0') return false;
      const r = el.getBoundingClientRect();
      return r.width >= 2 && r.height >= 2;
    } catch { return false; }
  };

  // Ordered from most specific (WordPress theme defaults) to generic fallbacks
  const selectors = [
    { sel: '.wp-block-navigation a[href]',        score: 0.97 },
    { sel: 'nav.main-navigation a[href]',          score: 0.96 },
    { sel: 'nav#site-navigation a[href]',          score: 0.96 },
    { sel: '#main-navigation a[href]',             score: 0.95 },
    { sel: '.primary-navigation a[href]',          score: 0.95 },
    { sel: '#primary-menu a[href]',                score: 0.94 },
    { sel: '.nav-primary a[href]',                 score: 0.94 },
    { sel: 'ul.menu a[href]',                      score: 0.90 },
    { sel: '.menu-main-menu-container a[href]',    score: 0.90 },
    { sel: 'header nav a[href]',                   score: 0.87 },
  ];

  for (const { sel, score } of selectors) {
    let nodes;
    try { nodes = Array.from(document.querySelectorAll(sel)); } catch { continue; }
    let visibleCount = 0;
    for (const a of nodes) {
      const href = (a.getAttribute('href') || '').trim();
      if (isBad(href)) continue;
      if (!isVisible(a)) continue;
      visibleCount++;
      let abs;
      try { abs = new URL(href, document.location.href).toString(); } catch { continue; }
      const text = norm(a.innerText || a.textContent || a.getAttribute('aria-label') || href);
      const existing = seen.get(abs);
      if (!existing || score > existing.confidence)
        seen.set(abs, { url: abs, text, confidence: score,
          kind: 'wp-dom', sourceType: 'WordPress', displayRole: 'Navigation',
          groupLabel: 'Main navigation', isStructural: true });
    }
    // Stop at first selector that yields at least 3 visible links
    if (visibleCount >= 3) break;
  }

  return Array.from(seen.values()).sort((a,b) => b.confidence - a.confidence);
}
");

        var domLinks = ParsePageNavLinks(domRaw, host, dropQueryStrings, maxLinks);
        if (domLinks.Count > 0)
            _log?.Event("WP_DOM_NAV", ("url", pageUrl), ("count", domLinks.Count));

        return domLinks;
    }

    private async Task<List<VisibleLinkGroup>> ExtractWordPressVisibleGroupsAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxPerGroup)
    {
        var groups = new List<VisibleLinkGroup>();

        // Recent posts / news widget (common in sidebars and footers)
        var recentPosts = await ExtractGroupBySelectorAsync(
            page, pageUrl, host, dropQueryStrings,
            ".widget_recent_entries a[href], .wp-block-latest-posts a[href], " +
            ".recent-posts a[href], #recent-posts-widget a[href]",
            "wp_recent_posts", "Recent posts", "News", 20, maxPerGroup);
        if (recentPosts.Flat.Count > 0) groups.Add(recentPosts);

        // Category links
        var categories = await ExtractGroupBySelectorAsync(
            page, pageUrl, host, dropQueryStrings,
            ".widget_categories a[href], .wp-block-categories a[href]",
            "wp_categories", "Categories", "Navigation", 30, maxPerGroup);
        if (categories.Flat.Count > 0) groups.Add(categories);

        // Footer links
        var footer = await ExtractGroupBySelectorAsync(
            page, pageUrl, host, dropQueryStrings,
            "footer a[href]",
            "footer", "Footer", "FooterLinks", 60, maxPerGroup);
        if (footer.Flat.Count > 0) groups.Add(footer);

        var existingIds = new HashSet<string>(groups.Select(g => g.Id), StringComparer.OrdinalIgnoreCase);
        var modules = await ExtractGenericQuickLinkModulesAsync(page, pageUrl, host, dropQueryStrings, maxPerGroup);
        foreach (var m in modules)
        {
            if (!existingIds.Contains(m.Id))
                groups.Add(m);
        }

        return groups.OrderBy(x => x.Order).ToList();
    }

    // Detects visible quick-link / homepage-module groups from heading text on
    // non-SiteVision pages (generic and WordPress CMS paths).  Uses the same
    // heading-pattern approach as ExtractSiteVisionVisibleModuleFallbackAsync
    // but with a smaller KNOWN set relevant to general municipality sites.
    private async Task<List<VisibleLinkGroup>> ExtractGenericQuickLinkModulesAsync(
        IPage page,
        string pageUrl,
        string host,
        bool dropQueryStrings,
        int maxPerGroup)
    {
        JsonElement raw;
        try
        {
            raw = await page.EvaluateAsync<JsonElement>(@"
() => {
  const KNOWN = [
    { pat: /^\s*(hitta\s+snabbt|snabba\s+vägar)\s*$/i,         id: 'quicklinks', label: 'Hitta snabbt', role: 'HomepageModule', order: 8 },
    { pat: /^\s*(genvägar|populära\s+sidor|snabblänkar)\s*$/i,  id: 'shortcuts',  label: 'Genvägar',     role: 'Shortcut',      order: 10 }
  ];

  const EXCL_SELS = ['header', 'footer', 'nav', '[role=""banner""]', '[role=""navigation""]', '[role=""contentinfo""]'];
  const excl = [];
  for (const s of EXCL_SELS) { try { excl.push(...document.querySelectorAll(s)); } catch {} }
  const isExcl = el => excl.some(ex => ex === el || ex.contains(el));
  const badHref = h => { const v = (h || '').trim().toLowerCase(); return !v || v === '#' || v.startsWith('#') || v.startsWith('javascript:') || v.startsWith('mailto:') || v.startsWith('tel:'); };

  const headings = Array.from(document.querySelectorAll('h2, h3, h4, h5')).filter(h => !isExcl(h));
  const modules = [];

  for (const heading of headings) {
    const headText = (heading.textContent || '').replace(/\s+/g, ' ').trim();
    const cls = KNOWN.find(k => k.pat.test(headText));
    if (!cls) continue;

    let block = heading.parentElement;
    let found = false;
    for (let i = 0; i < 6 && block && block !== document.body; i++) {
      const n = block.querySelectorAll('a[href]').length;
      if (n >= 1 && n <= 40) { found = true; break; }
      if (n > 40) break;
      block = block.parentElement;
    }
    if (!found) continue;

    const seen = new Set();
    const links = [];
    for (const a of block.querySelectorAll('a[href]')) {
      if (isExcl(a)) continue;
      const href = (a.getAttribute('href') || '').trim();
      if (badHref(href)) continue;
      let abs;
      try { abs = new URL(href, document.location.href).toString(); } catch { continue; }
      if (seen.has(abs)) continue;
      seen.add(abs);
      const text = (a.textContent || '').replace(/\s+/g, ' ').trim() || href;
      links.push({ url: abs, text });
    }

    const label = cls.role === 'HomepageModule' ? headText : cls.label;
    if (links.length > 0)
      modules.push({ id: cls.id, label, role: cls.role, order: cls.order, headingText: headText, links });
  }

  return modules;
}
");
        }
        catch (Exception ex)
        {
            _log?.Warn($"GENERIC_QUICKLINK_MODULES_JS_FAIL {ex.Message}");
            return new List<VisibleLinkGroup>();
        }

        if (raw.ValueKind != JsonValueKind.Array)
            return new List<VisibleLinkGroup>();

        var byId = new Dictionary<string, VisibleLinkGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in raw.EnumerateArray())
        {
            var id    = mod.TryGetProperty("id",         out var idP)  ? (idP.GetString()  ?? "") : "";
            var label = mod.TryGetProperty("label",       out var lblP) ? (lblP.GetString() ?? "") : id;
            var role  = mod.TryGetProperty("role",        out var rolP) ? (rolP.GetString() ?? "") : "";
            var order = mod.TryGetProperty("order",       out var ordP) && ordP.ValueKind == JsonValueKind.Number ? ordP.GetInt32() : 99;
            var htxt  = mod.TryGetProperty("headingText", out var htP)  ? (htP.GetString()  ?? "") : "";

            if (string.IsNullOrWhiteSpace(id)) continue;

            if (!byId.TryGetValue(id, out var grp))
            {
                grp = new VisibleLinkGroup { Id = id, Label = label, Role = role, Order = order, Flat = new List<NavItem>() };
                byId[id] = grp;
                if (!string.IsNullOrWhiteSpace(role))
                    _log?.Event("HOMEPAGE_CANDIDATE_ROLE_ASSIGNED",
                        ("headingText", htxt), ("groupId", id), ("role", role),
                        ("confidence", "1.0"), ("reason", "heading_matched_known_homepage_module_pattern"));
            }

            if (!mod.TryGetProperty("links", out var linksEl) || linksEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var lnk in linksEl.EnumerateArray())
            {
                if (grp.Flat.Count >= maxPerGroup) break;
                var urlRaw = lnk.TryGetProperty("url",  out var uP) ? (uP.GetString() ?? "") : "";
                var text   = lnk.TryGetProperty("text", out var tP) ? (tP.GetString() ?? "") : "";
                var url = NormalizeInternalUrl(urlRaw, pageUrl, host, dropQueryStrings);
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (grp.Flat.Any(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase))) continue;
                grp.Flat.Add(new NavItem
                {
                    Url = url, Title = string.IsNullOrWhiteSpace(text) ? url : text.Trim(),
                    Depth = 1, ParentUrl = pageUrl, IsDisplayOnly = IsDisplayOnlyUrl(url, host)
                });
            }

            _log?.Event("HOMEPAGE_MODULE_ACCEPTED",
                ("headingText", htxt), ("groupId", id), ("role", role), ("linksAdded", grp.Flat.Count));
        }

        return byId.Values.Where(g => g.Flat.Count > 0).OrderBy(g => g.Order).ToList();
    }

    // Returns true when a URL from a visible group is a display-only link that
    // should appear in the viewer but NOT become a structural crawl-expansion
    // root.  Structural seeds are section-index pages (1-2 path segments, no
    // date prefix).  Article-level pages and external URLs are display-only.
    private static bool IsDisplayOnlyUrl(string url, string host)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;

        // External URLs are display-only by definition (can't be crawled).
        if (!string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase)) return true;

        var segments = u.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Deep paths (≥ 3 segments) are article/leaf pages, not section indexes.
        if (segments.Length >= 3) return true;

        // Date-prefixed segments (YYYY-) signal individual article URLs.
        foreach (var seg in segments)
            if (seg.Length >= 5
                && char.IsDigit(seg[0]) && char.IsDigit(seg[1])
                && char.IsDigit(seg[2]) && char.IsDigit(seg[3])
                && seg[4] == '-')
                return true;

        return false;
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