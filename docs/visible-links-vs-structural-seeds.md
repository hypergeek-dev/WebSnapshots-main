# Visible Links vs Structural Seeds

## Previous problem

Every URL collected into a `VisibleLinkGroup.Flat` list was automatically
enqueued as a crawl-expansion seed in `NavCrawler`. This conflated two
distinct concerns:

1. **Display** — which links the viewer should show under "VISIBLE ON START PAGE"
2. **Crawl expansion** — which URLs the crawler should visit and explore for children

This caused crawl budget waste whenever a visible group contained article-level
URLs. For example, if a news section on the start page listed 8 recent articles,
all 8 article pages were visited by the crawler even though they have no
structural children worth discovering. The same problem would apply to individual
alert notices, event cards, and campaign pages.

## New model

Each `NavItem` carries a boolean flag:

```csharp
public bool IsDisplayOnly { get; set; } = false;
```

| Value | Meaning |
|---|---|
| `false` (default) | Structural seed — shown in viewer AND crawled for children |
| `true` | Display-only — shown in viewer, NOT crawled for children |

The flag is **omitted from nav.json when `false`** (the common case) to keep
the file compact. It appears as `"isDisplayOnly": true` only for links that
should be shown but not expanded.

## Why visible ≠ structural

| Link type | Show in viewer | Crawl for children |
|---|---|---|
| Section index (`/nyheter`) | yes | yes |
| Individual news article (`/nyheter/2026-05-22-titel`) | yes | **no** |
| Alert section index (`/driftinformation.4.xxx.html`) | yes | yes |
| Individual alert notice (`/driftinformation/2026-05-06-...`) | yes | **no** |
| Shortcut card to a municipal section (`/stadsplanering`) | yes | yes |
| External event page (`visitystadosterlen.se/...`) | yes | **no** |
| PDF document | yes | **no** |
| Campaign page (`/kampanj/…`) | yes | configurable |

Viewer richness (showing recent news headlines) does not require crawling those
articles structurally. The structural nav tree is about finding section roots and
their children — not about archiving article content.

## How seed selection works

`CmsAwareNavExtractor.IsDisplayOnlyUrl(url, host)` classifies each link at
extraction time using these rules (in priority order):

1. **External host** → display-only (can never be a same-site structural root)
2. **Path depth ≥ 3 segments** → display-only (article/leaf pages live this deep)
3. **Date-prefixed segment** (`YYYY-…` in any path component) → display-only
4. Everything else → structural seed

`NavCrawler` then builds `visibleSeedUrls` by skipping all items where
`IsDisplayOnly = true`, and logs:

```
EVT VISIBLE_DISPLAY_ONLY_SKIPPED host=… count=N
EVT NAV_GROUPS_EXTRACTED … visibleSeedUrls=M displayOnlyLinks=N
```

All items (both seeds and display-only) remain in `VisibleLinkGroup.Flat` and
are rendered by the viewer. Only crawl-expansion is restricted.

## Scalability and quality improvements

**Before:** A municipality with 10 recent news articles on the start page would
add 10 article URLs to the crawl queue. Each article visit costs a network
round-trip and potentially discovers further outlinks (related articles,
tag pages, author pages) that inflate `allFlat` without adding structural value.

**After:** Only the section-index URL (`/nyheter`) enters the queue. The viewer
still shows the article titles and links. Crawl time and page count are reduced
proportionally.

**Validation result (Kristianstad):**

| Metric | Before | After |
|---|---|---|
| `visibleSeedUrls` | 9 | 2 |
| `displayOnlyLinks` | 0 | 7 |
| `visibleGroups` | 3 | 3 (unchanged) |

7 article-level news/shortcut URLs are now display-only. The viewer shows them;
the crawler skips them.

## Remaining limitations

- The path-depth heuristic (`segments.Length >= 3`) is conservative. Some CMS
  platforms use shallow article URLs (e.g., `/artikel-titel` at depth 1). These
  would incorrectly be treated as structural seeds. The date-prefix rule catches
  most date-stamped article patterns.
- Campaign pages and promo cards are currently not excluded — they have short
  paths and no date prefix. This is intentional: promo cards often link to real
  section pages worth exploring. If a campaign URL floods the crawl, it should
  be handled via `IsNoisePath` rather than the display/seed split.
- The classification runs per-URL, not per-group-role. A "shortcuts" group whose
  links all happen to be 1-segment paths will still seed them all. This is
  correct behavior — shortcuts to municipal sections are structural by design.
