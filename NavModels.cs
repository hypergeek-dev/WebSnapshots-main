using System;
using System.Collections.Generic;
using System.Linq;

namespace WebSnapshots;

public sealed class NavIndex
{
    public string Host { get; set; } = "";
    public string StartUrl { get; set; } = "";
    public DateTime GeneratedUtc { get; set; }
    public string CmsKind { get; set; } = "Unknown";

    // All crawled pages, used for page lookup / page map.
    public List<NavItem> Flat { get; set; } = new();

    // Structural navigation tree only.
    public List<NavNode> Nodes { get; set; } = new();

    // Start-page structural groups.
    public List<NavGroup> NavGroups { get; set; } = new();

    // Start-page visible but not necessarily structural modules.
    public List<VisibleLinkGroup> VisibleGroups { get; set; } = new();

    // Municipality-authored top-level IA sections detected from homepage card/tile clusters.
    // These represent the intended root taxonomy of the site (e.g. "Omsorg och stöd",
    // "Förskola, skola och utbildning") and are used to anchor the viewer hierarchy.
    [System.Text.Json.Serialization.JsonPropertyName("homepageSections")]
    public List<HomepageSection> HomepageSections { get; set; } = new();
}

public sealed class NavGroup
{
    public string Id { get; set; } = "";
    public int LinkCount { get; set; }
    public int Rank { get; set; } = -1;
    public List<NavItem> Flat { get; set; } = new();
    public List<NavNode> Nodes { get; set; } = new();
}

public sealed class VisibleLinkGroup
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Role { get; set; } = "";
    public int Order { get; set; }
    public List<NavItem> Flat { get; set; } = new();
}

// A municipality-authored top-level IA anchor detected from homepage card/tile clusters.
public sealed class HomepageSection
{
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string Title { get; set; } = "";

    // Position in the DOM on the homepage (preserves intended IA order).
    [System.Text.Json.Serialization.JsonPropertyName("domOrder")]
    public int DomOrder { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("positiveEvidence")]
    public List<string> PositiveEvidence { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("negativeEvidence")]
    public List<string> NegativeEvidence { get; set; } = new();
}

public sealed class NavigationDecisionEvidence
{
    public string DecisionType { get; set; } = "";
    public string CandidateUrl { get; set; } = "";
    public string CandidateTitle { get; set; } = "";
    public bool Accepted { get; set; }
    public double Confidence { get; set; }
    public double Threshold { get; set; }
    public List<string> PositiveEvidence { get; set; } = new();
    public List<string> NegativeEvidence { get; set; } = new();
    public string FinalReason { get; set; } = "";
}

public sealed class NavItem
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int Depth { get; set; }
    public string ParentUrl { get; set; } = "";

    // True when the URL points to a different host than the crawled site.
    public bool IsExternal { get; set; } = false;

    // True when this item was only reachable via query string parameters
    // (e.g. JS-rendered event lists where all items share the same base URL).
    public bool IsJsDynamic { get; set; } = false;

    // True when this link should be shown in the viewer but NOT enqueued as a
    // structural crawl-expansion root (e.g. individual news articles, alert
    // notices, or external cards visible on the start page).
    // Omitted from JSON when false (the default) to keep nav.json compact.
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDisplayOnly { get; set; } = false;

    // True when this node was not crawled but was synthesised from URL-path topology
    // to fill a missing intermediate parent (e.g. /fritidsaktiviteter was never fetched
    // but its children were discovered and would otherwise fall to root).
    // Omitted from JSON when false to keep nav.json compact.
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSynthetic { get; set; } = false;

    // True when this page matches utility/meta heuristics (Kontakt, Tillgänglighet,
    // Intranät, Press, etc.) and should be visually grouped separately from the
    // municipality's primary IA sections in the viewer.
    // Omitted from JSON when false to keep nav.json compact.
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsUtility { get; set; } = false;

    // Root-placement policy result. This keeps demoted archive content visible
    // and searchable while letting the viewer avoid placing it in primary
    // municipal Navigation.
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string MunicipalRootClassification { get; set; } = "";
}

public sealed class NavNode
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public List<NavNode> Children { get; set; } = new();
}

public static class NavTreeBuilder
{
    public static List<NavNode> Build(List<NavItem> flat, string? startUrl = null)
    {
        if (flat == null || flat.Count == 0)
            return new List<NavNode>();

        startUrl = (startUrl ?? "").Trim();
        var hasParents = flat.Any(x => !string.IsNullOrWhiteSpace(x.ParentUrl));

        return hasParents
            ? BuildByParentPointers(flat, startUrl)
            : BuildByDepth(flat);
    }

    private static List<NavNode> BuildByParentPointers(List<NavItem> flat, string startUrl)
    {
        // Title lookup: one canonical title per URL (first non-empty wins).
        var titleByUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Insertion order of URLs (for stable child ordering later).
        var order = new List<string>(flat.Count);
        var orderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var it in flat)
        {
            var url = (it.Url ?? "").Trim();
            if (url.Length == 0) continue;

            if (!titleByUrl.TryGetValue(url, out var existing) || string.IsNullOrWhiteSpace(existing))
                titleByUrl[url] = (it.Title ?? "").Trim();

            if (orderSet.Add(url))
                order.Add(url);
        }

        // Collect ALL (parent -> child) edges, allowing a child to have multiple parents.
        // Each edge is stored once. We use a set to avoid duplicate edges from repeated
        // NavItem entries with the same (parent, child) pair.
        var edges = new List<(string Parent, string Child)>();
        var edgeSet = new HashSet<(string, string)>(
            EqualityComparer<(string, string)>.Create(
                (a, b) => string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase),
                x => StringComparer.OrdinalIgnoreCase.GetHashCode(x.Item1)
                   ^ StringComparer.OrdinalIgnoreCase.GetHashCode(x.Item2)));

        // childrenOf[parentUrl] = ordered list of child URLs under that parent.
        var childrenOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // For cycle detection we need to know which parents each URL currently has
        // (transitively). We track this per-parent as we add edges.
        // ancestorsOf[url] = all URLs that are ancestors of url across ALL parents.
        var ancestorsOf = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var it in flat)
        {
            var child = (it.Url ?? "").Trim();
            if (child.Length == 0) continue;

            var parent = (it.ParentUrl ?? "").Trim();
            if (parent.Length == 0) continue;
            if (parent.Equals(child, StringComparison.OrdinalIgnoreCase)) continue;

            // Parent must be a known URL.
            if (!titleByUrl.ContainsKey(parent)) continue;

            // Skip duplicate edges.
            if (!edgeSet.Add((parent, child))) continue;

            // Cycle check: would adding parent->child create a cycle?
            // A cycle exists if 'parent' is already a descendant of 'child'.
            if (ancestorsOf.TryGetValue(parent, out var parentsAncestors)
                && parentsAncestors.Contains(child))
                continue;

            // Safe to add edge. Record it.
            edges.Add((parent, child));

            if (!childrenOf.TryGetValue(parent, out var list))
            {
                list = new List<string>();
                childrenOf[parent] = list;
            }
            list.Add(child);

            // Update ancestor sets: child's ancestors = parent + parent's ancestors.
            if (!ancestorsOf.TryGetValue(child, out var childAncestors))
            {
                childAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ancestorsOf[child] = childAncestors;
            }
            childAncestors.Add(parent);
            if (ancestorsOf.TryGetValue(parent, out var pa))
                childAncestors.UnionWith(pa);
        }

        // Determine which URLs appear as a child in at least one edge.
        // A URL with NO parent edges becomes a root (or the start page).
        var urlsWithAnyParent = new HashSet<string>(
            edges.Select(e => e.Child),
            StringComparer.OrdinalIgnoreCase);

        // Factory: create a fresh NavNode for a given URL.
        NavNode MakeNode(string url) => new NavNode
        {
            Url = url,
            Title = titleByUrl.TryGetValue(url, out var t) ? t : url
        };

        // Recursively build the subtree rooted at 'url'.
        // 'ancestors' tracks the current path to detect cycles in the
        // constructed tree (guards against rare cases where multi-parent
        // logic could still produce an infinite expansion).
        NavNode BuildSubtree(string url, HashSet<string> ancestors)
        {
            var node = MakeNode(url);

            if (!childrenOf.TryGetValue(url, out var kids))
                return node;

            ancestors.Add(url);

            foreach (var childUrl in order)
            {
                if (!kids.Contains(childUrl, StringComparer.OrdinalIgnoreCase))
                    continue;

                // Skip if this child is already an ancestor on the current path
                // (should not happen given our edge-level cycle check, but safe).
                if (ancestors.Contains(childUrl))
                    continue;

                node.Children.Add(BuildSubtree(childUrl, new HashSet<string>(ancestors, StringComparer.OrdinalIgnoreCase)));
            }

            return node;
        }

        var roots = new List<NavNode>();

        // Start page always comes first as the root node.
        if (!string.IsNullOrWhiteSpace(startUrl) && titleByUrl.ContainsKey(startUrl))
            roots.Add(BuildSubtree(startUrl, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        // All other URLs that have no parent in any edge become additional roots.
        foreach (var url in order)
        {
            if (!string.IsNullOrWhiteSpace(startUrl)
                && url.Equals(startUrl, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!urlsWithAnyParent.Contains(url))
                roots.Add(BuildSubtree(url, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        }

        return roots;
    }

    private static List<NavNode> BuildByDepth(List<NavItem> flat)
    {
        var root = new List<NavNode>();
        var stack = new Stack<(int Depth, NavNode Node)>();

        foreach (var it in flat)
        {
            var node = new NavNode
            {
                Title = it.Title,
                Url = it.Url,
                Children = new List<NavNode>()
            };

            while (stack.Count > 0 && stack.Peek().Depth >= it.Depth)
                stack.Pop();

            if (stack.Count == 0) root.Add(node);
            else stack.Peek().Node.Children.Add(node);

            stack.Push((it.Depth, node));
        }

        return root;
    }
}
