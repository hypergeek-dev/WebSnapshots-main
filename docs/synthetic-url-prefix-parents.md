# Synthetic URL-Prefix Parents

## Problem: root-child visual failure

An archive viewer with 380 direct root children is visually unusable even when all other
quality metrics are green (no cycles, no duplicates, reasonable max depth, good screenshot
coverage).

The failure looks like this: expanding the root node in the sidebar tree reveals a flat,
undifferentiated wall of hundreds of leaf pages — sports clubs, social programmes, event
listings, and job ads mixed together at the same indentation level as the actual municipal
sections.

### Why aggregate metrics miss it

Standard quality metrics check things like:
- `orphanPageCount` — pages whose declared parentUrl is not in the flat list
- `maxDepthReached` — the deepest depth in the tree
- `duplicateUrlCount` — duplicate URL entries

None of these catch the specific failure where a page's parentUrl **is valid** (it is the
start URL / root) but the placement is still **wrong** because an intermediate section page
was never crawled.

A page like `/fritidsaktiviteter/eslovs-tennisklubb` with `parentUrl = https://www.eslov.se`
is not technically an orphan — its declared parent exists. But it belongs 2 levels deep, not
at root.

## Root cause

Some URL-prefix section pages (e.g. `/fritidsaktiviteter`, `/navetinsatser`, `/event`,
`/lediga-jobb`) are not part of the WordPress wp-nav-menu that drives hierarchy
reconstruction. They appear as list/index pages linked from within page content, not from
the CMS navigation menu.

During the crawl these pages are discovered as **parents** of their children (their URL
appears in child breadcrumb/link href attributes) but the section pages themselves are never
fetched as nav nodes. The result: their children land at root as fallback.

## Why synthetic URL-prefix parents are justified

The URL topology alone provides strong evidence of the correct grouping:

```
/fritidsaktiviteter/eslovs-tennisklubb
/fritidsaktiviteter/eslovs-taekwondoklubb
/fritidsaktiviteter/loberods-idrottsforening
```

These 48 pages share the exact same URL prefix `/fritidsaktiviteter`. The prefix is
structurally implied. Creating a synthetic node for it is not an inference about content
or meaning — it is a direct reading of URL topology, exactly like a filesystem directory
hierarchy.

This is equivalent to a ZIP archiver creating a directory entry for a path prefix that
appears in member file names but was never explicitly added.

## Creation rules

A synthetic parent node is created when:

1. One or more pages are direct root children (`parentUrl == startUrl`).
2. Their URL has path depth ≥ 2 (at least two path segments beyond the host).
3. Multiple pages (≥ 2) share the same immediate URL prefix.
4. That prefix URL does not already exist in the flat list.
5. The prefix slug is not numeric-only or a date pattern (e.g. `/2024-05-10`, `/12345`).
6. The prefix slug contains no query string or fragment characters.

### Safeguards

| Safeguard | Rationale |
|-----------|-----------|
| Minimum 2 children | A single page under a missing parent is ambiguous — could be a redirect, an alias, or a one-off URL. Two or more pages strongly imply a real section. |
| Skip numeric/date slugs | Numeric IDs (SiteVision page IDs) and date-pattern URLs are not real sections. |
| Skip if prefix already exists | No synthesis needed if the section is already in the tree. |
| Shallowest-first ordering | Groups are processed in ascending URL depth order so that chained missing parents (e.g. `/a` missing and `/a/b` missing) resolve correctly — the `/a` synthetic node exists in knownUrls when `/a/b` is processed. |
| No content/text inference | Titles are derived mechanically from the URL slug only (hyphens → spaces, first letter capitalised). No AI, no page-content analysis. |
| No hardcoded municipality names | The algorithm is generic; it operates purely on URL structure. |

## Title derivation

The title of a synthetic node is derived from its URL slug:

```
fritidsaktiviteter  →  Fritidsaktiviteter
lediga-jobb         →  Lediga jobb
navetinsatser       →  Navetinsatser
event               →  Event
kommunen            →  Kommunen
```

Rule: replace hyphens with spaces, capitalise the first character.

## Parent assignment for the synthetic node

The synthetic node itself needs a parent. The algorithm walks up the URL path one segment at
a time and returns the first ancestor found in the current known-URL set:

```
For synthetic node: https://www.eslov.se/fritidsaktiviteter
  → try https://www.eslov.se  (found, is startUrl → use startUrl)
```

If `/fritidsaktiviteter` is one segment (i.e. a direct child of the host root), its only
possible parent is the site root, so it lands there. This is still far better than having
48 club pages at root — the tree now shows:

```
Eslövs kommun (root)
  └─ Fritidsaktiviteter  [synthetic]
       ├─ Eslövs Tennisklubb
       ├─ Eslövs Taekwondoklubb
       └─ Löberöds Idrottsförening
       ...
```

For deeper synthetic nodes (e.g. a missing `/foo/bar` where `/foo` is already known), the
algorithm correctly attaches the synthetic `/foo/bar` under the existing `/foo` node.

## IsSynthetic flag

Synthetic nodes carry `"isSynthetic": true` in `nav.json`. This field is omitted when false
(same pattern as `isDisplayOnly` and `isJsDynamic`) to keep the file compact.

The viewer does not currently render the flag specially — synthetic nodes appear as normal
expandable folders. A badge or visual marker can be added later without breaking existing
behaviour.

## Telemetry events

| Event | Fields | Description |
|-------|--------|-------------|
| `ROOT_CHILD_QUALITY` | `rootChildCount`, `deepRootChildCount`, `suspiciousLeafRootChildCount` | Emitted once before synthesis with root-child counts. |
| `SYNTHETIC_PARENT_CREATED` | `url`, `title`, `childCount`, `parentUrl`, `reason` | Emitted once per synthetic node created. |
| `ROOT_LEAF_POLLUTION_REDUCED` | `before`, `after`, `syntheticParentsCreated` | Emitted after synthesis showing how many deep root children remain. |

## Quality report metrics

The quality report (`quality-report.json` / `quality-report.md`) now includes:

| Metric | Meaning |
|--------|---------|
| `rootChildCount` | Total direct children of the root/start node. |
| `deepRootChildCount` | Root children whose URL has ≥ 2 path segments (leaf/article pages at root). |
| `syntheticParentCount` | Number of synthetic intermediate parent nodes in this archive. |

### Warnings triggered

| Code | Severity | Condition |
|------|----------|-----------|
| `root_child_explosion` | Error | `rootChildCount > 50` |
| `root_leaf_pollution` | Error | `deepRootChildCount > 20` |
| `root_child_path_depth_too_high` | Warning | `0 < deepRootChildCount ≤ 20` |
| `synthetic_parents_inserted` | Info | `syntheticParentCount > 0` |

## Limitations

- **No semantic reparenting**: synthetic nodes are always placed under the nearest known
  URL ancestor or root — never under a semantically related section (e.g.
  `/fritidsaktiviteter` will land at root, not under `/uppleva-gora`, unless
  `/uppleva-gora/fritidsaktiviteter` is the actual URL).

- **Title quality**: slug-derived titles are readable but may not match the actual page
  title. If the section page is later crawled, the real title will replace the synthetic one
  during deduplication.

- **Single-child prefixes**: groups of 1 are not synthesised. A lone page under a missing
  prefix stays at root (it could be a redirect, stub, or mislinked page — insufficient
  evidence to create a section for it).

- **Applies post-crawl only**: the synthesis pass runs after the crawl loop ends. It does
  not affect which URLs are crawled.
