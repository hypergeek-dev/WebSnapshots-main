// SiteViewerBuilder.cs
using System.Text;
using System.Text.Json;

namespace WebSnapshots;

public sealed class SiteViewerBuilder
{
    private readonly SnapshotConfig _cfg;
    private readonly Logger? _log;

    public SiteViewerBuilder(SnapshotConfig cfg, Logger? log = null)
    {
        _cfg = cfg;
        _log = log;
    }

    public async Task BuildAsync(string siteDir, string host, string startUrl, string outputFileName = "index.htm")
    {
        var navPath = Path.Combine(siteDir, "nav.json");
        var viewerPath = Path.Combine(siteDir, outputFileName);

        NavIndex nav = new NavIndex
        {
            Host = host,
            StartUrl = Utils.NormalizeUrl(Utils.EnsureScheme(startUrl), _cfg.DropQueryStrings),
            GeneratedUtc = DateTime.UtcNow,
            Flat = new List<NavItem>(),
            Nodes = new List<NavNode>(),
            NavGroups = new List<NavGroup>(),
            VisibleGroups = new List<VisibleLinkGroup>()
        };

        if (File.Exists(navPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(navPath, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<NavIndex>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (parsed != null)
                    nav = parsed;
            }
            catch (JsonException ex)
            {
                _log?.Error($"Failed to parse nav.json for {host}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _log?.Error($"Unexpected error reading nav.json for {host}: {ex.Message}");
            }
        }

        var startAbs = Utils.NormalizeUrl(Utils.EnsureScheme(startUrl), _cfg.DropQueryStrings);

        if (string.IsNullOrWhiteSpace(nav.StartUrl))
            nav.StartUrl = startAbs;

        nav.Flat ??= new List<NavItem>();
        nav.Nodes ??= new List<NavNode>();
        nav.NavGroups ??= new List<NavGroup>();
        nav.VisibleGroups ??= new List<VisibleLinkGroup>();

        if (nav.Nodes.Count == 0 && nav.Flat.Count > 0)
            nav.Nodes = NavTreeBuilder.Build(nav.Flat, nav.StartUrl);

        foreach (var g in nav.NavGroups)
        {
            g.Flat ??= new List<NavItem>();
            g.Nodes ??= new List<NavNode>();

            if (g.Nodes.Count == 0 && g.Flat.Count > 0)
                g.Nodes = NavTreeBuilder.Build(g.Flat, nav.StartUrl);
        }

        foreach (var vg in nav.VisibleGroups)
            vg.Flat ??= new List<NavItem>();

        foreach (var it in nav.Flat.Take(5000))
        {
            var resolved = GetDisplayTitle(it.Title, it.Url);
            var reason = DisplayTitleResolutionReason(it.Title, it.Url, resolved);
            _log?.Event(reason == "url_fallback" ? "DISPLAY_TITLE_FALLBACK_USED" : "DISPLAY_TITLE_RESOLVED",
                ("url", it.Url),
                ("title", it.Title),
                ("resolvedTitle", resolved),
                ("reason", reason));
        }

        var shotsDir = Path.Combine(siteDir, "shots");
        var hasScreenshots = Directory.Exists(shotsDir)
            && Directory.EnumerateFiles(shotsDir, "*.webp", SearchOption.TopDirectoryOnly).Any();

        var viewerHtml = BuildViewerHtml(host, startUrl, nav, _cfg.DropQueryStrings, hasScreenshots);
        await File.WriteAllTextAsync(viewerPath, viewerHtml, Encoding.UTF8);
    }

    // ── Phase 6: Display title helpers ───────────────────────────────────────

    private static string GetDisplayTitle(string? title, string? url)
    {
        var t = (title ?? "").Trim();
        if (t.Length > 0
            && !t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !t.StartsWith("/", StringComparison.Ordinal))
            return t;

        return HumanizeUrlSlug(url ?? "");
    }

    private static string DisplayTitleResolutionReason(string? title, string? url, string resolved)
    {
        var t = (title ?? "").Trim();
        if (t.Length > 0
            && !t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !t.StartsWith("/", StringComparison.Ordinal))
            return "trusted_non_url_title";

        if (!string.IsNullOrWhiteSpace(resolved)
            && !resolved.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !resolved.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolved, url, StringComparison.OrdinalIgnoreCase))
            return "humanized_slug";

        return "url_fallback";
    }

    private static string HumanizeUrlSlug(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        try
        {
            var segments = new Uri(url).AbsolutePath
                .TrimEnd('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return url;
            var slug = segments[^1];
            if (string.IsNullOrWhiteSpace(slug)) return url;
            var spaced = slug.Replace('-', ' ');
            spaced = ApplySwedishRepairs(spaced);
            return spaced.Length > 0
                ? char.ToUpperInvariant(spaced[0]) + spaced[1..]
                : url;
        }
        catch { return url; }
    }

    private static string ApplySwedishRepairs(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\bstod\b", "stöd", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\bgora\b", "göra", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\bmiljo\b", "miljö", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\bhallbar\b", "hållbar", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\bnaring\b", "näring", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\bomsorg\b", "omsorg", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return s;
    }

    private static string BuildViewerHtml(string host, string startUrl, NavIndex nav, bool dropQueryStrings, bool hasScreenshots = true)
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        var flat = nav.Flat ?? new List<NavItem>();
        var nodes = nav.Nodes ?? new List<NavNode>();
        var visibleGroups = nav.VisibleGroups ?? new List<VisibleLinkGroup>();

        var pageMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var it in flat)
        {
            if (string.IsNullOrWhiteSpace(it.Url)) continue;

            var norm = Utils.NormalizeUrl(it.Url, dropQueryStrings);
            if (pageMap.ContainsKey(norm)) continue;

            var fileBase = Utils.SafeFileBaseFromUrl(norm);
            pageMap[norm] = "pages/" + fileBase + ".htm";

            // Also register a www-stripped alias so that visible group items
            // stored as bare astorp.se/... can resolve against a www.astorp.se flat list
            try
            {
                var u = new Uri(norm);
                if (u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                {
                    var bareBuilder = new UriBuilder(norm) { Host = u.Host[4..] };
                    var bare = bareBuilder.Uri.ToString().TrimEnd('/');
                    if (!pageMap.ContainsKey(bare))
                        pageMap[bare] = "pages/" + fileBase + ".htm";
                }
            }
            catch { }
        }

        var startAbs = Utils.NormalizeUrl(Utils.EnsureScheme(startUrl), dropQueryStrings);
        pageMap.TryGetValue(startAbs, out var initialSrc);

        // Build parent/child lookup from flat crawl data
        var itemsByUrl = new Dictionary<string, NavItem>(StringComparer.OrdinalIgnoreCase);
        var childrenByParent = new Dictionary<string, List<NavItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (var it in flat)
        {
            if (string.IsNullOrWhiteSpace(it.Url)) continue;

            var normUrl = Utils.NormalizeUrl(it.Url, dropQueryStrings);
            if (!itemsByUrl.ContainsKey(normUrl))
            {
                itemsByUrl[normUrl] = new NavItem
                {
                    Url = normUrl,
                    Title = it.Title,
                    Depth = it.Depth,
                    ParentUrl = string.IsNullOrWhiteSpace(it.ParentUrl)
                        ? ""
                        : Utils.NormalizeUrl(it.ParentUrl, dropQueryStrings)
                };
            }
        }

        foreach (var it in itemsByUrl.Values)
        {
            if (string.IsNullOrWhiteSpace(it.ParentUrl)) continue;

            if (!childrenByParent.TryGetValue(it.ParentUrl, out var list))
            {
                list = new List<NavItem>();
                childrenByParent[it.ParentUrl] = list;
            }

            if (!list.Any(x => x.Url.Equals(it.Url, StringComparison.OrdinalIgnoreCase)))
                list.Add(it);
        }

        object ToTreeDto(NavNode n)
        {
            var norm = Utils.NormalizeUrl(n.Url, dropQueryStrings);
            var title = GetDisplayTitle(n.Title, n.Url);

            return new
            {
                title,
                url = norm,
                href = pageMap.TryGetValue(norm, out var href) ? href : "",
                children = (n.Children ?? new List<NavNode>()).Select(ToTreeDto).ToList()
            };
        }

        object BuildVisibleSeedTreeDto(NavItem seed, HashSet<string> visited)
        {
            var norm = Utils.NormalizeUrl(seed.Url, dropQueryStrings);
            if (!visited.Add(norm))
            {
                return new
                {
                    title = GetDisplayTitle(seed.Title, seed.Url),
                    url = norm,
                    href = pageMap.TryGetValue(norm, out var href0) ? href0 : "",
                    isExternal = seed.IsExternal,
                    isJsDynamic = seed.IsJsDynamic,
                    children = new List<object>()
                };
            }

            var title = GetDisplayTitle(seed.Title, seed.Url);
            var children = new List<object>();

            if (childrenByParent.TryGetValue(norm, out var kids))
            {
                foreach (var child in kids
                    .OrderBy(x => x.Depth)
                    .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
                {
                    children.Add(BuildVisibleSeedTreeDto(child, visited));
                }
            }

            return new
            {
                title,
                url = norm,
                href = pageMap.TryGetValue(norm, out var href) ? href : "",
                isExternal = seed.IsExternal,
                isJsDynamic = seed.IsJsDynamic,
                children
            };
        }

        object ToGroupDto(VisibleLinkGroup g)
        {
            var roots = new List<object>();
            var hasJsDynamic = false;

            // Track (parentUrl, childUrl) pairs rather than just childUrl so that the
            // same URL can appear under multiple parents — each entry in the visible group
            // is a distinct nav position that should be shown, even if the underlying
            // captured file is the same. Truly identical duplicate entries (same URL
            // appearing twice with the same parent context) are still suppressed.
            //
            // For root-level items (no parent context), we use "" as the parent key
            // but still allow the same URL to appear multiple times if the group's
            // flat list contains it more than once at root level (i.e. the site
            // genuinely surfaces the same link twice at the top of that nav group).
            // To keep it sane we deduplicate exact consecutive repeats only.
            var usedEdges = new HashSet<(string Parent, string Child)>(
                EqualityComparer<(string, string)>.Create(
                    (a, b) => string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase),
                    x => StringComparer.OrdinalIgnoreCase.GetHashCode(x.Item1)
                       ^ StringComparer.OrdinalIgnoreCase.GetHashCode(x.Item2)));

            foreach (var x in (g.Flat ?? new List<NavItem>()))
            {
                var norm = Utils.NormalizeUrl(x.Url, dropQueryStrings);
                if (string.IsNullOrWhiteSpace(norm)) continue;

                // External items: include even if not in pageMap
                if (!x.IsExternal && !pageMap.ContainsKey(norm)) continue;

                // Deduplicate by (parent, self) edge — same URL under same parent only once.
                var parentKey = string.IsNullOrWhiteSpace(x.ParentUrl)
                    ? ""
                    : Utils.NormalizeUrl(x.ParentUrl, dropQueryStrings);

                if (!usedEdges.Add((parentKey, norm))) continue;

                if (x.IsJsDynamic) hasJsDynamic = true;

                NavItem seed;
                if (itemsByUrl.TryGetValue(norm, out var existing))
                {
                    // Clone so we don't mutate the shared itemsByUrl entry when
                    // the same URL appears under multiple parents.
                    seed = new NavItem
                    {
                        Url = existing.Url,
                        Title = string.IsNullOrWhiteSpace(existing.Title) ? x.Title : existing.Title,
                        Depth = existing.Depth,
                        ParentUrl = existing.ParentUrl,
                        IsExternal = x.IsExternal,
                        IsJsDynamic = x.IsJsDynamic
                    };
                }
                else
                {
                    seed = new NavItem
                    {
                        Url = norm,
                        Title = string.IsNullOrWhiteSpace(x.Title) ? x.Url : x.Title,
                        Depth = 1,
                        ParentUrl = "",
                        IsExternal = x.IsExternal,
                        IsJsDynamic = x.IsJsDynamic
                    };
                }

                roots.Add(BuildVisibleSeedTreeDto(seed, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            }

            return new
            {
                id = g.Id,
                label = g.Label,
                role = g.Role,
                order = g.Order,
                hasJsDynamic,
                roots
            };
        }

        var treeDtos = nodes.Select(ToTreeDto).ToList();
        var visibleDtos = visibleGroups
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Select(ToGroupDto)
            .ToList();

        var treeJson = JsonSerializer.Serialize(treeDtos);
        var visibleJson = JsonSerializer.Serialize(visibleDtos);

        // Phase 2+4+5+8: build URL classification sets for viewer section grouping.
        var homepageSections = nav.HomepageSections ?? new List<HomepageSection>();

        var utilityUrlsLower = flat
            .Where(x => x.IsUtility && !string.IsNullOrWhiteSpace(x.Url))
            .Select(x => Utils.NormalizeUrl(x.Url, dropQueryStrings).ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Homepage sections are positive ordering evidence, not the structural
        // allow-list. Primary/root nav children stay structural unless they are
        // explicitly marked as utility/noise.
        var homepageSectionUrlsLower = homepageSections
            .Where(hs => !string.IsNullOrWhiteSpace(hs.Url))
            .Select(hs => Utils.NormalizeUrl(hs.Url, dropQueryStrings).ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Discovered/root helper buckets are reserved for explicit synthetic
        // roots such as "Ovrigt". Do not demote legitimate primary nav roots
        // merely because homepage card extraction accepted few or no anchors.
        var discoveredUrlsLower = flat
            .Where(x => !string.IsNullOrWhiteSpace(x.Url)
                     && !string.IsNullOrWhiteSpace(x.ParentUrl)
                     && x.ParentUrl.Equals(startAbs, StringComparison.OrdinalIgnoreCase)
                     && !x.Url.Equals(startAbs, StringComparison.OrdinalIgnoreCase)
                     && x.IsSynthetic
                     && !x.IsUtility)
            .Select(x => Utils.NormalizeUrl(x.Url, dropQueryStrings).ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Homepage sections ordered by DOM position for Phase 2 viewer ordering.
        var homepageSectionsJs = homepageSections
            .OrderBy(hs => hs.DomOrder)
            .Select(hs => new
            {
                url = Utils.NormalizeUrl(hs.Url, dropQueryStrings).ToLowerInvariant(),
                title = hs.Title
            })
            .ToList();

        var utilityUrlsJson      = JsonSerializer.Serialize(utilityUrlsLower.ToArray());
        var discoveredUrlsJson   = JsonSerializer.Serialize(discoveredUrlsLower.ToArray());
        var homepageSectionsJson = JsonSerializer.Serialize(homepageSectionsJs);

        var sb = new StringBuilder();

        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"sv\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("  <title>").Append(E(host)).AppendLine("</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    :root { --bg:#1f1f24; --bg2:#2a2a33; --bg3:#333340; --fg:#fff; --muted:rgba(255,255,255,.7); --accent:#7a003c; --border:rgba(255,255,255,.12); --util:#1c2030; --disc:#1a1f1a; }");
        sb.AppendLine("    html,body{height:100%;}");
        sb.AppendLine("    body{margin:0;overflow:hidden;font-family:system-ui,Segoe UI,Arial,sans-serif;background:#111;color:var(--fg);}");
        sb.AppendLine("    .layout{display:flex;height:100vh;width:100vw;}");
        sb.AppendLine("    aside{width:360px;background:var(--bg);border-right:1px solid var(--border);display:flex;flex-direction:column;min-width:280px;max-width:520px;}");
        sb.AppendLine("    header{padding:12px 14px;border-bottom:1px solid var(--border);}");
        sb.AppendLine("    header .row{display:flex;align-items:center;gap:10px;}");
        sb.AppendLine("    .homebtn{display:inline-flex;align-items:center;justify-content:center;width:34px;height:34px;border-radius:10px;border:1px solid var(--border);background:transparent;color:var(--fg);text-decoration:none;flex:0 0 auto;}");
        sb.AppendLine("    .homebtn:hover{background:var(--bg2);}");
        sb.AppendLine("    .host{font-weight:700;font-size:.95rem;line-height:1.1;}");
        sb.AppendLine("    .url{font-size:.8rem;color:var(--muted);word-break:break-all;}");
        sb.AppendLine("    .url a{color:var(--muted);text-decoration:none;}");
        sb.AppendLine("    .url a:hover{text-decoration:underline;}");
        sb.AppendLine("    .tools{padding:10px 14px;border-bottom:1px solid var(--border);display:flex;gap:8px;flex-wrap:wrap;}");
        sb.AppendLine("    .btn{display:inline-block;padding:6px 10px;border-radius:10px;border:1px solid var(--border);background:transparent;color:var(--fg);font-size:.82rem;text-decoration:none;cursor:pointer;}");
        sb.AppendLine("    .btn:hover{background:var(--bg2);}");
        sb.AppendLine("    .search{padding:10px 14px;border-bottom:1px solid var(--border);}");
        sb.AppendLine("    .search input{width:100%;padding:9px 10px;border-radius:10px;border:1px solid var(--border);background:rgba(0,0,0,.2);color:var(--fg);outline:none;box-sizing:border-box;}");
        sb.AppendLine("    .search input::placeholder{color:rgba(255,255,255,.55);}");
        sb.AppendLine("    .treeTools{padding:8px 14px;border-bottom:1px solid var(--border);display:flex;gap:8px;flex-wrap:wrap;}");
        sb.AppendLine("    nav{flex:1;overflow:auto;padding:0 0 16px 0;}");
        sb.AppendLine("    .muted{color:var(--muted);font-size:.82rem;padding:8px 12px;}");
        // Site header (municipality root node)
        sb.AppendLine("    .siteHeader{padding:8px 12px 6px;border-bottom:1px solid var(--border);}");
        sb.AppendLine("    .siteHeader .tree-link,.siteHeader .site-name{font-weight:700;font-size:.9rem;color:var(--fg);}");
        // Main section header (plain label — Navigation)
        sb.AppendLine("    .sectionTitle{padding:8px 12px 4px;color:var(--muted);font-size:.75rem;font-weight:700;text-transform:uppercase;letter-spacing:.05em;}");
        // Collapsible section toggles (Utility / Discovered)
        sb.AppendLine("    .sectionToggle{width:100%;text-align:left;padding:6px 12px;background:transparent;border:none;border-top:1px solid var(--border);color:var(--muted);font-size:.75rem;font-weight:700;text-transform:uppercase;letter-spacing:.05em;cursor:pointer;display:flex;align-items:center;gap:6px;font-family:inherit;}");
        sb.AppendLine("    .sectionToggle:hover{background:var(--bg2);color:var(--fg);}");
        sb.AppendLine("    .sectionArrow{font-size:.85rem;opacity:.7;flex:0 0 auto;}");
        sb.AppendLine("    .sectionCount{opacity:.55;font-weight:400;text-transform:none;letter-spacing:0;margin-left:auto;}");
        sb.AppendLine("    .sectionBody--hidden{display:none;}");
        sb.AppendLine("    .sectionBody--util{background:rgba(0,30,80,.15);}");
        sb.AppendLine("    .sectionBody--disc{background:rgba(0,40,0,.1);}");
        // Tree styles
        sb.AppendLine("    .tree{font-size:.88rem;line-height:1.3;padding:0 4px;}");
        sb.AppendLine("    .tree ul{list-style:none;margin:0;padding-left:18px;}");
        sb.AppendLine("    .tree-root{padding-left:4px !important;}");
        sb.AppendLine("    .tree-item{margin:1px 0;}");
        sb.AppendLine("    .tree-row{display:flex;align-items:center;gap:4px;min-height:24px;}");
        sb.AppendLine("    .toggle{width:18px;height:18px;display:inline-flex;align-items:center;justify-content:center;border:1px solid var(--border);border-radius:3px;background:var(--bg2);color:#fff;font-size:12px;cursor:pointer;user-select:none;flex:0 0 18px;}");
        sb.AppendLine("    .toggle.empty{visibility:hidden;cursor:default;}");
        sb.AppendLine("    .folder{font-size:14px;opacity:.95;flex:0 0 auto;}");
        sb.AppendLine("    .tree-link,.group-link{display:inline-block;padding:3px 6px;border-radius:6px;color:var(--fg);text-decoration:none;word-break:break-word;}");
        sb.AppendLine("    .tree-link:hover,.group-link:hover{background:var(--bg2);}");
        sb.AppendLine("    .tree-link.active,.group-link.active{background:var(--bg3);outline:1px solid var(--accent);}");
        sb.AppendLine("    .children.hidden{display:none;}");
        sb.AppendLine("    .visibleGroup{margin:0 0 8px 0;padding:0 0 8px 0;border-bottom:1px solid rgba(255,255,255,.06);}");
        sb.AppendLine("    .visibleGroup:last-child{border-bottom:0;}");
        sb.AppendLine("    .visibleGroupLabel{padding:6px 8px;font-size:.83rem;font-weight:700;color:#fff;}");
        sb.AppendLine("    .visibleGroup .tree{padding-left:0;}");
        sb.AppendLine("    .badge-ext{font-size:.68rem;vertical-align:middle;background:rgba(255,180,0,.18);color:#ffb400;border-radius:3px;padding:0 3px;margin-left:4px;white-space:nowrap;}");
        sb.AppendLine("    .badge-js{font-size:.68rem;vertical-align:middle;background:rgba(100,180,255,.15);color:#64b4ff;border-radius:3px;padding:0 3px;margin-left:4px;white-space:nowrap;}");
        sb.AppendLine("    .js-note{font-size:.75rem;color:rgba(255,255,255,.4);padding:2px 8px 4px 8px;font-style:italic;}");
        sb.AppendLine("    main{flex:1;background:#f5f5f5;display:flex;flex-direction:column;}");
        sb.AppendLine("    iframe{flex:1;width:100%;border:0;background:#f5f5f5;min-height:0;}");
        sb.AppendLine("    .text-only-banner{background:#5c3100;color:#ffd06f;padding:8px 14px;font-size:.84rem;flex:0 0 auto;border-bottom:1px solid rgba(0,0,0,.25);}");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"layout\">");
        sb.AppendLine("    <aside>");
        sb.AppendLine("      <header>");
        sb.AppendLine("        <div class=\"row\">");
        sb.AppendLine("          <a class=\"homebtn\" href=\"../../index.htm\" title=\"Back to main index\" aria-label=\"Home\">🏠</a>");
        sb.AppendLine("          <div>");
        sb.Append("            <div class=\"host\">").Append(E(host)).AppendLine("</div>");
        sb.Append("            <div class=\"url\"><a href=\"").Append(E(startUrl)).AppendLine("\" target=\"_blank\" rel=\"noreferrer\">Start URL</a></div>");
        sb.AppendLine("          </div>");
        sb.AppendLine("        </div>");
        sb.AppendLine("      </header>");

        sb.AppendLine("      <div class=\"tools\">");
        sb.AppendLine("        <a class=\"btn\" href=\"#\" id=\"openStart\">Open start URL</a>");
        sb.AppendLine("      </div>");

        sb.AppendLine("      <div class=\"search\">");
        sb.AppendLine("        <input id=\"q\" placeholder=\"Search navigation...\" autocomplete=\"off\">");
        sb.AppendLine("      </div>");

        sb.AppendLine("      <div class=\"treeTools\">");
        sb.AppendLine("        <a class=\"btn\" href=\"#\" id=\"expandAll\">Expand all</a>");
        sb.AppendLine("        <a class=\"btn\" href=\"#\" id=\"collapseAll\">Collapse all</a>");
        sb.AppendLine("      </div>");

        sb.AppendLine("      <nav id=\"navPane\"></nav>");
        sb.AppendLine("    </aside>");
        sb.AppendLine("    <main>");
        if (!hasScreenshots)
            sb.AppendLine("      <div class=\"text-only-banner\">&#9888; Text-only mode &mdash; no screenshots captured. Navigation structure is complete; click a page to view extracted text.</div>");
        sb.Append("      <iframe id=\"view\" name=\"view\" src=\"").Append(E(initialSrc ?? "about:blank")).AppendLine("\"></iframe>");
        sb.AppendLine("    </main>");
        sb.AppendLine("  </div>");

        sb.AppendLine("  <script>");
        sb.AppendLine("    (function(){");
        sb.Append("      const treeData = ").Append(treeJson).AppendLine(";");
        sb.Append("      const visibleGroups = ").Append(visibleJson).AppendLine(";");
        sb.Append("      const utilityUrls = new Set(").Append(utilityUrlsJson).AppendLine(");");
        sb.Append("      const discoveredUrls = new Set(").Append(discoveredUrlsJson).AppendLine(");");
        sb.Append("      const homepageSections = ").Append(homepageSectionsJson).AppendLine(";");
        sb.AppendLine("      const navPane = document.getElementById('navPane');");
        sb.AppendLine("      const iframe = document.getElementById('view');");
        sb.AppendLine("      const q = document.getElementById('q');");
        sb.AppendLine("      const btnExpandAll = document.getElementById('expandAll');");
        sb.AppendLine("      const btnCollapseAll = document.getElementById('collapseAll');");
        sb.AppendLine("      const btnOpenStart = document.getElementById('openStart');");

        sb.AppendLine("      function norm(s){ return (s || '').trim().toLowerCase(); }");
        sb.AppendLine("      function normUrl(u){ return (u || '').toLowerCase().replace(/\\/$/, ''); }");
        sb.AppendLine("      function escapeHtml(s){");
        sb.AppendLine("        return (s || '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/\\\"/g,'&quot;').replace(/'/g,'&#39;');");
        sb.AppendLine("      }");

        sb.AppendLine("      function setActiveLinkByHref(href){");
        sb.AppendLine("        document.querySelectorAll('.tree-link.active,.group-link.active').forEach(el => el.classList.remove('active'));");
        sb.AppendLine("        if (!href) return;");
        sb.AppendLine("        document.querySelectorAll('[data-href]').forEach(el => {");
        sb.AppendLine("          if ((el.getAttribute('data-href') || '') === href) el.classList.add('active');");
        sb.AppendLine("        });");
        sb.AppendLine("      }");

        sb.AppendLine("      function buildTreeNode(node, level, filter, linkClass){");
        sb.AppendLine("        const title = node.title || node.url || '(untitled)';");
        sb.AppendLine("        const url = node.url || '';");
        sb.AppendLine("        const href = node.href || '';");
        sb.AppendLine("        const kids = Array.isArray(node.children) ? node.children : [];");
        sb.AppendLine("        const f = norm(filter);");
        sb.AppendLine("        const selfMatch = !f || norm(title).includes(f) || norm(url).includes(f);");
        sb.AppendLine("        const renderedChildren = kids.map(k => buildTreeNode(k, level + 1, filter, linkClass)).filter(x => x.visible);");
        sb.AppendLine("        const hasVisibleChildren = renderedChildren.length > 0;");
        sb.AppendLine("        const visible = selfMatch || hasVisibleChildren;");
        sb.AppendLine("        if (!visible) return { visible:false, html:'' };");

        sb.AppendLine("        const hasChildren = kids.length > 0;");
        sb.AppendLine("        const expandedByDefault = !!f;");
        sb.AppendLine("        const toggleText = expandedByDefault ? '−' : '+';");
        sb.AppendLine("        const childClass = expandedByDefault ? 'children' : 'children hidden';");
        sb.AppendLine("        const toggleClass = hasChildren ? 'toggle' : 'toggle empty';");
        sb.AppendLine("        const folderIcon = hasChildren ? '📁' : '📄';");
        sb.AppendLine("        const klass = linkClass || 'tree-link';");

        sb.AppendLine("        let html = '<li class=\"tree-item\">';");
        sb.AppendLine("        html += '<div class=\"tree-row\">';");
        sb.AppendLine("        html += '<button class=\"' + toggleClass + '\" type=\"button\">' + (hasChildren ? toggleText : '') + '</button>'; ");
        sb.AppendLine("        html += '<span class=\"folder\">' + folderIcon + '</span>';");

        sb.AppendLine("        if (href) {");
        sb.AppendLine("          html += '<a class=\"' + klass + '\" target=\"view\" href=\"' + escapeHtml(href) + '\" data-href=\"' + escapeHtml(href) + '\" title=\"' + escapeHtml(url) + '\">' + escapeHtml(title) + '</a>'; ");
        sb.AppendLine("        } else if (node.isExternal) {");
        sb.AppendLine("          html += '<a class=\"' + klass + '\" target=\"_blank\" rel=\"noreferrer\" href=\"' + escapeHtml(url) + '\" title=\"' + escapeHtml(url) + '\">' + escapeHtml(title) + '</a>'; ");
        sb.AppendLine("        } else {");
        sb.AppendLine("          html += '<span class=\"' + klass + '\" title=\"' + escapeHtml(url) + '\">' + escapeHtml(title) + '</span>'; ");
        sb.AppendLine("        }");
        sb.AppendLine("        if (node.isExternal) html += '<span class=\"badge-ext\" title=\"External link\">↗ extern</span>';");
        sb.AppendLine("        if (node.isJsDynamic) html += '<span class=\"badge-js\" title=\"JavaScript-rendered content\">JS</span>';");
        sb.AppendLine("        html += '</div>';");

        sb.AppendLine("        if (hasChildren) {");
        sb.AppendLine("          html += '<ul class=\"' + childClass + '\">';");
        sb.AppendLine("          html += renderedChildren.map(x => x.html).join('');");
        sb.AppendLine("          html += '</ul>';");
        sb.AppendLine("        }");

        sb.AppendLine("        html += '</li>';");
        sb.AppendLine("        return { visible:true, html };");
        sb.AppendLine("      }");

        sb.AppendLine("      function buildVisibleGroup(group, filter){");
        sb.AppendLine("        const roots = Array.isArray(group.roots) ? group.roots : [];");
        sb.AppendLine("        const parts = roots.map(r => buildTreeNode(r, 0, filter, 'group-link')).filter(x => x.visible).map(x => x.html);");
        sb.AppendLine("        if (!parts.length) return '';");

        sb.AppendLine("        let html = '<div class=\"visibleGroup\">';");
        sb.AppendLine("        html += '<div class=\"visibleGroupLabel\">' + escapeHtml(group.label || group.role || 'Visible content') + '</div>'; ");
        sb.AppendLine("        html += '<div class=\"tree\"><ul class=\"tree-root\">' + parts.join('') + '</ul></div>'; ");
        sb.AppendLine("        if (group.hasJsDynamic) html += '<div class=\"js-note\">⚡ Innehåll renderat av JavaScript – kan vara ofullständigt</div>';");
        sb.AppendLine("        html += '</div>';");
        sb.AppendLine("        return html;");
        sb.AppendLine("      }");

        sb.AppendLine("      function renderTree(filter){");
        sb.AppendLine("        const f = norm(filter);");
        sb.AppendLine("        let html = '';");

        // Build homepage section order map for Phase 2 root topology priority
        sb.AppendLine("        const hsOrder = {};");
        sb.AppendLine("        homepageSections.forEach((s, i) => { hsOrder[normUrl(s.url)] = i; });");

        // Root node (municipality)
        sb.AppendLine("        if (treeData.length > 0) {");
        sb.AppendLine("          const rootNode = treeData[0];");
        sb.AppendLine("          const rootHref = rootNode.href || '';");
        sb.AppendLine("          const rootTitle = rootNode.title || rootNode.url || '';");
        sb.AppendLine("          html += '<div class=\"siteHeader\">';");
        sb.AppendLine("          if (rootHref) {");
        sb.AppendLine("            html += '<a class=\"tree-link\" target=\"view\" href=\"' + escapeHtml(rootHref) + '\" data-href=\"' + escapeHtml(rootHref) + '\">' + escapeHtml(rootTitle) + '</a>';");
        sb.AppendLine("          } else {");
        sb.AppendLine("            html += '<span class=\"site-name\">' + escapeHtml(rootTitle) + '</span>';");
        sb.AppendLine("          }");
        sb.AppendLine("          html += '</div>';");

        // Classify root children into structural / utility / discovered
        sb.AppendLine("          const rootKids = Array.isArray(rootNode.children) ? rootNode.children : [];");
        sb.AppendLine("          const structural = [], utility = [], discovered = [];");
        sb.AppendLine("          for (const kid of rootKids) {");
        sb.AppendLine("            const ku = normUrl(kid.url || '');");
        sb.AppendLine("            if (utilityUrls.has(ku)) utility.push(kid);");
        sb.AppendLine("            else if (discoveredUrls.has(ku)) discovered.push(kid);");
        sb.AppendLine("            else structural.push(kid);");
        sb.AppendLine("          }");

        // Sort structural by homepage section order (Phase 2)
        sb.AppendLine("          structural.sort((a, b) => {");
        sb.AppendLine("            const ai = hsOrder[normUrl(a.url)] !== undefined ? hsOrder[normUrl(a.url)] : 9999;");
        sb.AppendLine("            const bi = hsOrder[normUrl(b.url)] !== undefined ? hsOrder[normUrl(b.url)] : 9999;");
        sb.AppendLine("            return ai - bi;");
        sb.AppendLine("          });");

        // Main Navigation section
        sb.AppendLine("          const sParts = structural.map(n => buildTreeNode(n, 0, filter, 'tree-link')).filter(x => x.visible);");
        sb.AppendLine("          if (sParts.length || !f) {");
        sb.AppendLine("            html += '<div class=\"sectionTitle\">Navigation</div>';");
        sb.AppendLine("            if (sParts.length)");
        sb.AppendLine("              html += '<div class=\"tree\"><ul class=\"tree-root\">' + sParts.map(x => x.html).join('') + '</ul></div>';");
        sb.AppendLine("            else");
        sb.AppendLine("              html += '<div class=\"muted\">Inga matchande sidor.</div>';");
        sb.AppendLine("          }");

        // Utility / meta section (Phase 8 — collapsed by default unless searching)
        sb.AppendLine("          const uParts = utility.map(n => buildTreeNode(n, 0, filter, 'tree-link')).filter(x => x.visible);");
        sb.AppendLine("          if (uParts.length) {");
        sb.AppendLine("            const uOpen = !!f;");
        sb.AppendLine("            html += '<button class=\"sectionToggle\" data-target=\"utilBody\" type=\"button\">';");
        sb.AppendLine("            html += '<span class=\"sectionArrow\">' + (uOpen ? '\\u25BE' : '\\u25B8') + '</span>';");
        sb.AppendLine("            html += ' Verktyg &amp; information';");
        sb.AppendLine("            html += '<span class=\"sectionCount\">(' + utility.length + ')</span>';");
        sb.AppendLine("            html += '</button>';");
        sb.AppendLine("            html += '<div id=\"utilBody\" class=\"sectionBody sectionBody--util' + (uOpen ? '' : ' sectionBody--hidden') + '\">';");
        sb.AppendLine("            html += '<div class=\"tree\"><ul class=\"tree-root\">' + uParts.map(x => x.html).join('') + '</ul></div>';");
        sb.AppendLine("            html += '</div>';");
        sb.AppendLine("          }");

        // Discovered content section (Phase 8 — collapsed by default unless searching)
        sb.AppendLine("          const dParts = discovered.map(n => buildTreeNode(n, 0, filter, 'tree-link')).filter(x => x.visible);");
        sb.AppendLine("          if (dParts.length) {");
        sb.AppendLine("            const dOpen = !!f;");
        sb.AppendLine("            html += '<button class=\"sectionToggle\" data-target=\"discBody\" type=\"button\">';");
        sb.AppendLine("            html += '<span class=\"sectionArrow\">' + (dOpen ? '\\u25BE' : '\\u25B8') + '</span>';");
        sb.AppendLine("            html += ' \\u00D6vrigt inneh\\u00E5ll';");
        sb.AppendLine("            html += '<span class=\"sectionCount\">(' + discovered.length + ')</span>';");
        sb.AppendLine("            html += '</button>';");
        sb.AppendLine("            html += '<div id=\"discBody\" class=\"sectionBody sectionBody--disc' + (dOpen ? '' : ' sectionBody--hidden') + '\">';");
        sb.AppendLine("            html += '<div class=\"tree\"><ul class=\"tree-root\">' + dParts.map(x => x.html).join('') + '</ul></div>';");
        sb.AppendLine("            html += '</div>';");
        sb.AppendLine("          }");

        sb.AppendLine("        } else {");
        // No root node — fall back to rendering all treeData nodes flat
        sb.AppendLine("          const parts = treeData.map(n => buildTreeNode(n, 0, filter, 'tree-link')).filter(x => x.visible);");
        sb.AppendLine("          html += '<div class=\"sectionTitle\">Navigation</div>';");
        sb.AppendLine("          if (parts.length) html += '<div class=\"tree\"><ul class=\"tree-root\">' + parts.map(x => x.html).join('') + '</ul></div>';");
        sb.AppendLine("          else html += '<div class=\"muted\">No matching tree nodes.</div>';");
        sb.AppendLine("        }");

        // Visible groups: collapsed by default unless searching, same as utility/discovered.
        sb.AppendLine("        const visibleParts = visibleGroups.map(g => buildVisibleGroup(g, filter)).filter(Boolean);");
        sb.AppendLine("        if (visibleParts.length) {");
        sb.AppendLine("          const vOpen = !!f;");
        sb.AppendLine("          html += '<button class=\"sectionToggle\" data-target=\"visibleBody\" type=\"button\">';");
        sb.AppendLine("          html += '<span class=\"sectionArrow\">' + (vOpen ? '\\u25BE' : '\\u25B8') + '</span>';");
        sb.AppendLine("          html += ' Visible on start page';");
        sb.AppendLine("          html += '<span class=\"sectionCount\">(' + visibleGroups.length + ')</span>';");
        sb.AppendLine("          html += '</button>';");
        sb.AppendLine("          html += '<div id=\"visibleBody\" class=\"sectionBody' + (vOpen ? '' : ' sectionBody--hidden') + '\">';");
        sb.AppendLine("          html += visibleParts.join('');");
        sb.AppendLine("          html += '</div>';");
        sb.AppendLine("        }");

        sb.AppendLine("        navPane.innerHTML = html;");

        // Section toggle listeners (utility / discovered)
        sb.AppendLine("        navPane.querySelectorAll('.sectionToggle').forEach(btn => {");
        sb.AppendLine("          btn.addEventListener('click', function(){");
        sb.AppendLine("            const targetId = this.getAttribute('data-target');");
        sb.AppendLine("            const body = document.getElementById(targetId);");
        sb.AppendLine("            if (!body) return;");
        sb.AppendLine("            const isHidden = body.classList.contains('sectionBody--hidden');");
        sb.AppendLine("            body.classList.toggle('sectionBody--hidden', !isHidden);");
        sb.AppendLine("            const arrow = this.querySelector('.sectionArrow');");
        sb.AppendLine("            if (arrow) arrow.textContent = isHidden ? '\\u25BE' : '\\u25B8';");
        sb.AppendLine("          });");
        sb.AppendLine("        });");

        // Tree node toggle listeners (existing)
        sb.AppendLine("        navPane.querySelectorAll('.toggle').forEach(btn => {");
        sb.AppendLine("          if (btn.classList.contains('empty')) return;");
        sb.AppendLine("          btn.addEventListener('click', function(e){");
        sb.AppendLine("            e.preventDefault();");
        sb.AppendLine("            e.stopPropagation();");
        sb.AppendLine("            const li = btn.closest('.tree-item');");
        sb.AppendLine("            if (!li) return;");
        sb.AppendLine("            const children = li.querySelector(':scope > ul.children');");
        sb.AppendLine("            if (!children) return;");
        sb.AppendLine("            const hidden = children.classList.toggle('hidden');");
        sb.AppendLine("            btn.textContent = hidden ? '+' : '\\u2212';");
        sb.AppendLine("          });");
        sb.AppendLine("        });");
        sb.AppendLine("      }");

        sb.AppendLine("      function expandCollapseAll(expand){");
        sb.AppendLine("        navPane.querySelectorAll('ul.children').forEach(el => el.classList.toggle('hidden', !expand));");
        sb.AppendLine("        navPane.querySelectorAll('.toggle').forEach(el => {");
        sb.AppendLine("          if (!el.classList.contains('empty')) el.textContent = expand ? '\\u2212' : '+';");
        sb.AppendLine("        });");
        sb.AppendLine("        navPane.querySelectorAll('.sectionBody').forEach(el => el.classList.toggle('sectionBody--hidden', !expand));");
        sb.AppendLine("        navPane.querySelectorAll('.sectionArrow').forEach(el => { el.textContent = expand ? '\\u25BE' : '\\u25B8'; });");
        sb.AppendLine("      }");

        sb.AppendLine("      function refresh(){");
        sb.AppendLine("        const filter = q.value || '';");
        sb.AppendLine("        renderTree(filter);");
        sb.AppendLine("        setActiveLinkByHref(iframe.getAttribute('src') || '');");
        sb.AppendLine("      }");

        sb.AppendLine("      btnExpandAll.addEventListener('click', function(e){ e.preventDefault(); expandCollapseAll(true); });");
        sb.AppendLine("      btnCollapseAll.addEventListener('click', function(e){ e.preventDefault(); expandCollapseAll(false); });");
        sb.AppendLine("      btnOpenStart.addEventListener('click', function(e){ e.preventDefault(); window.open(").Append(JsonSerializer.Serialize(startUrl)).AppendLine(", '_blank', 'noopener,noreferrer'); });");

        sb.AppendLine("      q.addEventListener('input', function(){ refresh(); });");

        sb.AppendLine("      navPane.addEventListener('click', function(e){");
        sb.AppendLine("        const a = e.target.closest('a[data-href]');");
        sb.AppendLine("        if (!a) return;");
        sb.AppendLine("        setActiveLinkByHref(a.getAttribute('data-href') || '');");
        sb.AppendLine("      });");

        sb.AppendLine("      iframe.addEventListener('load', function(){");
        sb.AppendLine("        const src = iframe.getAttribute('src') || '';");
        sb.AppendLine("        setActiveLinkByHref(src);");
        sb.AppendLine("      });");

        sb.AppendLine("      refresh();");
        sb.AppendLine("    })();");
        sb.AppendLine("  </script>");

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }
}
