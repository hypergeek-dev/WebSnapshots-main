# WordPress Navigation Hierarchy Reconstruction

## Problem

WordPress/Municipio municipalities expose their full page tree through the WP REST API (`/wp-json/wp/v2/pages`). The API returns up to 100 pages as a flat list. WebSnapshots feeds all of these into `primaryNavUrls` at depth 1 with `parentUrl = startUrl`.

The result is a navigation wall: every page — section pages, subsection pages, and deeply nested leaf pages — appears as a direct child of the root. For Eslöv this produced ~70 root-level children including pages 4–5 levels deep. The tree was structurally unusable.

Example of what the REST API produces (all at depth 1):
```
eslov.se/                            ← root
  eslov.se/omsorg-stod/              ← should be depth 1
  eslov.se/omsorg-stod/hemtjanst     ← should be depth 2
  eslov.se/omsorg-stod/familj        ← should be depth 2
  eslov.se/utbildning-barnomsorg/gymnasium/carlengstromgymnasiet   ← should be depth 3
  eslov.se/utbildning-barnomsorg/gymnasium/carlengstromgymnasiet/funderar-du-pa-att-soka  ← should be depth 4
```

## Why the WP REST API approach is insufficient for hierarchy

The REST API endpoint `/wp-json/wp/v2/pages` does return a `parent` field, but it carries the WP internal integer ID of the parent page — not the URL. The crawler does not maintain a WP-ID-to-URL map, and building one would require an additional API call per page (or a second pass over the REST response). More importantly, the REST API is rate-limited to 100 pages per request, which covers only a fraction of a real municipality's page tree. Hierarchy reconstruction from URLs is simpler, stateless, and generalises to any CMS with clean slug URLs.

## Solution: URL-path hierarchy reconstruction

After the crawl loop completes, `ReparentOrphansByUrlPath` in `NavCrawler.cs` runs a post-processing step that fixes the flat tree by walking each root-level page's URL path upward until it finds a known ancestor.

### Algorithm

**Input:** `allFlat` (all visited pages), `treeFlat` (structural pages used to build the navigation tree), `startUrl`.

**Step 1 — Identify candidates.** Collect all `allFlat` items where `parentUrl == startUrl` (i.e., root-level orphans). Sort them by ascending URL segment count so that parents are processed before children. This ensures that when a parent is promoted to depth 2, its children subsequently find it at the correct depth and inherit depth 3.

**Step 2 — Strategy 1 (SiteVision-style section prefix match).** For each candidate, look for the longest section page in `treeFlat` whose URL is a prefix of the candidate's URL (using `IsUrlDescendantOf` + `GetSectionPrefix`/`CanonicalizeSectionPrefix`). This strategy handles SiteVision numeric-ID URLs like `/barnochutbildning.2142.html` correctly.

**Step 3 — Strategy 2 (WordPress clean-slug path walk).** If Strategy 1 finds no ancestor, call `FindNearestKnownAncestorUrl`. This helper:
1. Parses the candidate URL into path segments: `/omsorg-stod/hemtjanst` → `["omsorg-stod", "hemtjanst"]`
2. Walks from the deepest ancestor upward: first tries `eslov.se/omsorg-stod` (found → stop), then `eslov.se/` (skip, it's the start URL)
3. Returns the first URL found in `allFlatByUrl` (all visited pages keyed by URL)

**Step 4 — Apply reparenting.** If a best parent is found:
- Update `item.ParentUrl` and `item.Depth` in `allFlat`
- Also update the corresponding `treeFlat` item in-place via `treeFlatByUrl` — this is critical: `NavTreeBuilder.Build` uses `treeFlat`, not `allFlat`. Without this, the displayed tree stays flat even after `allFlat` is corrected.
- Add a deduplication-guarded edge so the page is not double-added to `treeFlat`

### Key invariants

- **Only root-level orphans are candidates.** Pages that already have a non-root `parentUrl` (i.e., pages already correctly parented by SiteVision's structural nav) are never touched.
- **Strategy 1 runs first.** If a SiteVision section-prefix match exists, Strategy 2 is skipped. This preserves SiteVision behavior exactly.
- **URL match only.** No title similarity, no semantic inference, no AI/LLM. A parent is assigned only if its URL is a known ancestor path of the child's URL and it exists in the visited page set.
- **No municipality-specific code.** The algorithm works on any site with clean slug URLs. WordPress/Municipio happens to be the CMS that needs it most, but the logic is CMS-agnostic.

### Safeguards against false hierarchy

- A page is only reparented if its URL path literally contains the parent's path as a prefix. `/omsorg-stod/hemtjanst` → `/omsorg-stod/` is correct. A coincidentally similar name (e.g., `/omsorg/omsorg-stod/`) is not matched because path segments must align exactly.
- The start URL is explicitly excluded: `if (ancestorUrl.Equals(startUrl, ...)) return null`. This prevents every page from collapsing to the root.
- `treeFlat` deduplication: `treeFlatEdges.Add(edgeKey)` uses a `HashSet<string>` to ensure each parent→child edge is added at most once, regardless of how many times reparenting runs.

## Results (Eslöv, 2026-05-25)

| Metric | Before | After |
|---|---|---|
| Root-level pages in allFlat | 70 | 1 |
| Inferred parent relationships | 0 | 69 |
| Max tree depth | 1 | 4 |
| Pages in 40-page diagnostic | 0 (no tree) | 135 crawled |

Example inferred relationships:
```
eslov.se/upleva-gora/fritidsguidning → parent: eslov.se/upleva-gora
eslov.se/omsorg-stod/soc-din-vag-till-stod → parent: eslov.se/omsorg-stod
eslov.se/omsorg-stod/trygg-och-saker/din-krisberedskap → parent: eslov.se/omsorg-stod/trygg-och-saker
eslov.se/utbildning-barnomsorg/gymnasium/carlengstromgymnasiet/funderar-du-pa-att-soka → parent: .../carlengstromgymnasiet
eslov.se/omsorg-stod/soc-din-vag-till-stod/stod-i-foraldraskap/stod-i-foraldrarskap-rad-och-stodsamtal → parent: .../stod-i-foraldraskap
```

## SiteVision regression (Ystad, Kristianstad)

Strategy 2 fires on SiteVision sites only when pages end up as root-level orphans (pages discovered via fallback links whose parent could not be determined by the structural extractor). This is a very small number:

- **Ystad:** 4 orphaned pages moved under correct section ancestors; 16→12 root pages; `maxDepthAfter=3` unchanged.
- **Kristianstad:** similar small number; no structural corruption.

No page that already had a SiteVision-derived parent was touched. The SiteVision structural extractor remains the primary mechanism for SiteVision sites. Strategy 2 acts only as a cleanup pass for genuine orphans.

## Telemetry events

| Event | When emitted |
|---|---|
| `URL_PARENT_INFERRED` | Once per reparented page, with `url` and `parent` |
| `URL_HIERARCHY_RECONSTRUCTION_USED` | If any reparenting occurred, with `inferred`, `rootLevelBefore`, `rootLevelAfter` |
| `ROOT_LEVEL_PAGE_REDUCED` | Alongside `URL_HIERARCHY_RECONSTRUCTION_USED`, with `before`, `after`, `reduced` |
| `HIERARCHY_RECONSTRUCTION_SUMMARY` | Summary event with `startUrl`, counts, and `maxDepthAfter` |

If `URL_HIERARCHY_RECONSTRUCTION_USED` never appears in telemetry, reconstruction ran but found nothing to fix (no root-level orphans with known ancestors). If it appears with a large `inferred` count, the site is likely a WordPress/REST-fed flat tree.

## Limitations

1. **Intermediate nodes not in allFlat.** If the intermediate path segment was never visited, Strategy 2 skips it and attaches the page to the nearest ancestor that was visited. Example: `/a/b/c` is attached to `/a` instead of `/a/b` if `/a/b` was never crawled. This is better than root but less precise than the true hierarchy.

2. **REST API 100-page limit.** WP REST returns at most 100 pages. For municipalities with >100 pages, some section ancestors may not be in the initial list and thus not in `allFlatByUrl` until they are visited during crawl. Sorting candidates by segment count (parents before children) mitigates this by ensuring any section page discovered during crawl is already in `allFlatByUrl` when its children are processed.

3. **Non-hierarchical WordPress URL schemes.** Some WordPress sites use flat slugs that do not reflect content hierarchy (e.g., all pages at `/page-slug` regardless of nesting). For those sites, Strategy 2 will find no ancestors and the root-level count will be unchanged. This is correct — the algorithm only infers hierarchy when the URL literally encodes it.

4. **Full-scale validation.** Regression checks used MaxPages=200 nav-diagnostic runs. Production runs may expose edge cases not visible at this scale.

## Files changed

- `NavCrawler.cs` — `ReparentOrphansByUrlPath` (new signature, new body), `FindNearestKnownAncestorUrl` (new helper), `CountUrlPathSegments` (new helper), `CrawlAsync` return (CmsKind assignment fix)
