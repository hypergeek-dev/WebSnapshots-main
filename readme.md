# WebSnapshots

WebSnapshots is a Playwright-based web archiving tool that captures visual snapshots of websites and produces a fully static, offline archive.

It crawls internal links on a site (same host only), renders each page headlessly, captures full-page screenshots (with automatic tiling fallback), and generates HTML viewers to browse the captured pages locally without any runtime dependencies.

The output is a visual record, not an HTML mirror.

---

## How It Works

    sites.txt
       ↓
    Program.cs
       ↓
    NavCrawler ──► nav.json
       ↓
    Snapshotter ──► WebP screenshots + per-page HTML
       ↓
    SiteViewerBuilder ──► viewer.htm (per site)
       ↓
    TopIndexBuilder ──► index.htm (global)

---

## Key Characteristics

- Same-host crawl only
- Depth and page-count limited
- Deterministic viewport rendering
- Full-page screenshots with WebP encoding
- Automatic handling of lazy loading, modals, and banners
- Hard global storage cap (abort on overflow)
- Static HTML output only (no JS frameworks, no backend)

---

## Operational Modes

WebSnapshots has two distinct runtime paths. They share the crawler and snapshotter code
but differ in everything else: output location, what files are written, whether screenshots
are taken, and whether quality measurement is done.

| Mode | Entry point | Screenshots | Quality report | Telemetry | Output location |
|---|---|---|---|---|---|
| **Scrape** | `dotnet run` / GUI | **Always on** | Never | Never | `output/{municipality}/{date}/` |
| **Diagnostic** | `dotnet run -- diagnostic` | Profile-controlled | Always | Always | `diagnostics/{targetId}/{timestamp}/` |

> **Critical:** A diagnostic run is never a production archive. It is a measurement run.
> Even when it looks complete (viewer, per-page HTML, nav tree), it may have no screenshots.
> See [Snapshot Behavior](#snapshot-behavior) and [Common Mistakes](#common-mistakes).

---

## Running

### Production archive (scrape mode)

Add URLs to `sites.txt`, one per line, then:

```powershell
dotnet run
```

Or with flags:

```powershell
dotnet run -- --sites sites.txt --output ./output --max-depth 3 --max-pages 1500
```

Serve the output locally:

```powershell
dotnet run -- serve 8080 output
```

Open `http://localhost:8080/`

---

## Command Reference

### `scrape` (no verb — default CLI mode)

Reads `sites.txt` and produces a full visual archive: nav.json, WebP screenshots, per-page
HTML viewers, and a navigable `index.htm`.

```powershell
dotnet run -- [flags]
```

**Key flags:**

| Flag | Default | Description |
|---|---|---|
| `--sites` / `--input` | `sites.txt` | URL list (one per line, `#` for comments) |
| `--output` / `--out` | `output` | Output base directory |
| `--max-depth` | `3` | Crawl depth limit |
| `--max-pages` | `1500` | Page cap per site |
| `--cap-gb` | `150` | Storage cap in GB |
| `--landing-only` | off | Snapshot start page only, no crawl |
| `--quick-preview` | off | Breadth-capped fast shape check |
| `--preview-pages-per-depth` | `25` | Pages per depth in quick-preview |
| `--preview-children-per-page` | `18` | Children per page in quick-preview |
| `--preview-seconds` | `90` | Timeout for entire quick-preview |
| `--debug` | off | Verbose output |
| `--drop-query-strings` | on | Deduplicate by stripping query strings |
| `--keep-query-strings` | — | Disable query-string deduplication |
| `--viewport` | `1920x1080` | Browser viewport |
| `--webp-quality` | `75` | WebP compression (1–100) |
| `--nav-timeout-ms` | `45000` | Navigation page load timeout |
| `--shot-timeout-ms` | `60000` | Screenshot page load timeout |
| `--network-idle-ms` | `8000` | Max wait for network idle |
| `--stabilize-ms` | `12000` | Max wait for render stabilization |
| `--delay-ms` | `0` | Delay between pages |
| `--user-agent` | — | Override browser user-agent |

**Output structure:**

```
output/
  {municipality}/
    {yyMMdd}/
      nav.json          — navigation index
      index.htm         — self-contained viewer (nav tree + page viewers)
      pages/            — per-page HTML (screenshots embedded)
      shots/            — raw WebP screenshot files
  _runs/
    {yyyy-MM-dd_HHmmss}/
      run.log
      run.json
      index.htm
  index.htm             — global index across all runs
```

> **Note:** Screenshots are always captured in scrape mode. There is no flag to disable them.

---

### `diagnostic`

Runs the crawler against a single registered CMS target using a named profile. Produces
telemetry, quality reports, and optionally screenshots. **Does not produce a production archive.**

```powershell
dotnet run -- diagnostic --target <targetId> [--profile <name>] [--output ./diagnostics]
```

**Required:**

| Flag | Description |
|---|---|
| `--target` / `-t` | Target ID from `config/cms-targets.json` (e.g. `sitevision_ystad`) |

**Optional:**

| Flag | Default | Description |
|---|---|---|
| `--profile` / `-p` | `nav-diagnostic` | Profile name (see Profile Reference) |
| `--output` / `--out` | `./diagnostics` | Output base directory |
| `--max-depth` | Profile value | Override profile depth |
| `--max-pages` | Profile value | Override profile page cap |
| `--config` | `config/cms-targets.json` | Path to targets config |

**Output structure:**

```
diagnostics/
  {targetId}/
    {yyyy-MM-dd_HHmmss}/
      scrape-meta.json      — run parameters
      diagnostic.log        — human-readable log
      telemetry.jsonl       — structured event stream
      quality-report.json   — all quality metrics
      quality-report.md     — human-readable quality summary
      nav.json              — navigation index
      index.htm             — viewer (nav tree, present if buildViewer=true)
      pages/                — per-page HTML (present if extractText=true)
      shots/                — WebP screenshots (present if captureSnapshots=true)
```

**List available targets:**

```powershell
dotnet run -- diagnostic --target ?
```

---

### `compare`

Diffs two diagnostic run folders and produces a regression report.

```powershell
dotnet run -- compare --previous <baseline-dir> --current <candidate-dir>
```

Output (written into `--current` dir): `comparison-report.json`, `comparison-report.md`

---

### `review-pack`

Packages a run folder into a single Markdown file for LLM or human review.

```powershell
dotnet run -- review-pack --current <run-dir>
```

If `comparison-report.json` exists in the directory, it is included automatically.
Output: `ai-review-pack.md` in the `--current` directory.

---

### `acceptance`

Evaluates a comparison report through the acceptance gate and exits with a code for CI.

```powershell
dotnet run -- acceptance --comparison <path-to-comparison-report.json>
```

Exit codes: `0` = accepted, `1` = requires human review, `2` = rejected.

---

### `serve`

Serves an output directory over HTTP for local browsing.

```powershell
dotnet run -- serve 8080 output
```

Open `http://localhost:8080/`

---

### `gui` (no args or `gui`)

Launches the WinForms GUI. Reads URLs from a text box. Exposes depth, quick-preview,
and landing-only controls. Output is written to the same structure as CLI scrape mode,
but with additional per-scrape `index.html` and per-municipality `index.html` files.

> **Note:** The GUI currently has a broken viewer link. See [Known Bugs](#known-bugs).

---

## Profile Reference

Profiles control every aspect of a `diagnostic` run. They live in
`config/diagnostic-profiles.json` and can be customized there.

| Profile | Depth | Pages | Screenshots | Text | Viewer | Purpose |
|---|---|---|---|---|---|---|
| `smoke` | 0 | 1 | No | No | No | Verify pipeline can reach the target |
| `cms-diagnostic` | 1 | 50 | No | Yes | Yes | Confirm CMS detection, check shallow nav |
| `nav-diagnostic` | 3 | 200 | **No** | Yes | Yes | Main feedback loop for navigation quality |
| `snapshot-diagnostic` | 2 | 40 | **Yes** | Yes | Yes | Check screenshot and text extraction |
| `full-validation` | 3 | 1500 | **Yes** | Yes | Yes | Final confidence before shipping |

**Breadth limits** (how crawl is constrained per depth level):

| Profile | Max children/page | Max pages/depth | Effect |
|---|---|---|---|
| `smoke` | — | — | Only start page visited |
| `cms-diagnostic` | 15 | 50 | Shallow breadth-limited crawl |
| `nav-diagnostic` | 20 | 70 | Breadth-limited; may miss deep sections |
| `snapshot-diagnostic` | 10 | 30 | Narrower breadth for speed |
| `full-validation` | **None** | **None** | Full unrestricted crawl (like production) |

> `full-validation` is the only profile that matches production scrape crawl behavior.
> All other diagnostic profiles use breadth limits that may miss parts of a large site.

**What the default profile does:**

Running `diagnostic --target X` without `--profile` uses **`nav-diagnostic`**:
- 200 pages max
- Depth 3
- No screenshots — every page viewer shows "No images captured."
- Text files written (pages/ directory exists)
- Viewer generated (index.htm exists with nav tree)
- Quality report and telemetry written

---

## Production vs Diagnostic Explanation

| Question | Production scrape | Diagnostic run |
|---|---|---|
| Command | `dotnet run -- --sites ...` | `dotnet run -- diagnostic --target ...` |
| Default output folder | `output/` | `diagnostics/` |
| Screenshots always taken | Yes | Only if `captureSnapshots=true` in profile |
| Quality report generated | **Never** | Always |
| Telemetry generated | **Never** | Always |
| CMS detection performed | No explicit detection | Yes, with confidence scoring |
| Target must be registered | No (any URL) | Yes (`cms-targets.json`) |
| Breadth limits applied | No (unless `--quick-preview`) | Yes for all profiles except `full-validation` |
| Suitable for sharing as archive | Yes | Only if `captureSnapshots=true` |
| Suitable for CI regression | No | Yes (via `compare` + `acceptance`) |

**The conceptual separation:**

- **Production scrape** = crawl + screenshot + package. Output goes to `output/`. This is what end users receive.
- **Diagnostic run** = crawl + measure + optionally capture. Output goes to `diagnostics/`. This is engineering evidence.

A `diagnostic --profile full-validation` run is equivalent to a production scrape in crawl
scope (1500 pages, no breadth limits, screenshots on), but its output is in `diagnostics/`,
not `output/`, and it includes quality measurement that production does not.

---

## Snapshot Behavior

Screenshots are controlled differently in each mode:

**Scrape mode:**
- Screenshots are always taken. No flag to disable.
- Each page gets one full-page WebP (single file if page fits one viewport, tiled if taller).
- Blank-screenshot guard: if a screenshot is below 20,000 bytes, it is retried once after
  another cookie-consent click attempt.
- Cookie consent is clicked automatically (Swedish and English patterns).

**Diagnostic mode — per profile:**

| Profile | Snapshotter invoked? | Screenshots captured? | Text files written? |
|---|---|---|---|
| `smoke` | **No** | No | No |
| `cms-diagnostic` | Yes (text-only mode) | **No** | Yes |
| `nav-diagnostic` | Yes (text-only mode) | **No** | Yes |
| `snapshot-diagnostic` | Yes (full mode) | **Yes** | Yes |
| `full-validation` | Yes (full mode) | **Yes** | Yes |

**Text-only mode** means the Snapshotter loads every page, extracts text, and writes
`pages/{safe}.htm` and `pages/{safe}.text.htm` — but never calls the screenshot capture
path. The `shots/` directory is created but empty. The log records `SHOT_OK images=0`.

> **Operator trap:** `SHOT_OK images=0` in the telemetry is logged as a success event.
> It means "page viewer was written" — not "screenshot was taken." The image count is
> the key field. Zero images = text-only run.

---

## Example Commands

### Nav-diagnostic (main development loop)

```powershell
# Run nav-diagnostic — no screenshots, nav quality only
dotnet run -- diagnostic --target sitevision_ystad --profile nav-diagnostic

# Override page count
dotnet run -- diagnostic --target sitevision_ystad --profile nav-diagnostic --max-pages 100

# Compare two runs
dotnet run -- compare `
  --previous ./diagnostics/sitevision_ystad/2026-05-26_221151 `
  --current  ./diagnostics/sitevision_ystad/2026-05-27_121850

# Evaluate quality gate
dotnet run -- acceptance `
  --comparison ./diagnostics/sitevision_ystad/2026-05-27_121850/comparison-report.json
```

### Full-validation (pre-release confidence check)

```powershell
# Full 1500-page run with screenshots — comparable to production
dotnet run -- diagnostic --target sitevision_ystad --profile full-validation

# Compare against a nav-diagnostic baseline
dotnet run -- compare `
  --previous ./diagnostics/sitevision_ystad/2026-05-27_121850 `
  --current  ./diagnostics/sitevision_ystad/2026-05-27_200000

# Check acceptance gate
dotnet run -- acceptance `
  --comparison ./diagnostics/sitevision_ystad/2026-05-27_200000/comparison-report.json

# Build AI review pack
dotnet run -- review-pack --current ./diagnostics/sitevision_ystad/2026-05-27_200000
```

### Production archive

```powershell
# Single site
echo "https://ystad.se/" > sites.txt
dotnet run -- --output ./output --max-depth 3 --max-pages 1500

# Multiple sites
dotnet run -- --sites municipalities.txt --output ./output --max-depth 3

# Landing page only (no crawl)
dotnet run -- --sites sites.txt --landing-only

# Quick shape check (breadth-limited, fast)
dotnet run -- --sites sites.txt --quick-preview --preview-seconds 120
```

### Smoke test (just verify connectivity)

```powershell
dotnet run -- diagnostic --target sitevision_ystad --profile smoke
```

---

## Common Mistakes

### "I ran diagnostic but there are no screenshots"

This is expected if you used the default profile (`nav-diagnostic`) or `cms-diagnostic`.
Those profiles have `captureSnapshots=false`. The viewer will render with the nav tree
but every page shows "No images captured."

To get screenshots in diagnostic mode, use:
```powershell
dotnet run -- diagnostic --target X --profile snapshot-diagnostic
# or
dotnet run -- diagnostic --target X --profile full-validation
```

To get screenshots without using the diagnostic system, use scrape mode:
```powershell
echo "https://example.com/" > sites.txt
dotnet run --
```

### "I used full-validation but my output folder is empty"

`full-validation` writes to `./diagnostics/` by default, not `./output/`. Check:
```powershell
ls ./diagnostics/{targetId}/
```

The production archive is NOT updated by a diagnostic run.

### "The viewer links are broken after a GUI run"

Known bug in AppRunner (GUI mode). `SiteViewerBuilder` writes the viewer to `index.htm`,
then the GUI overwrites it with an entry page that links to `viewer.htm` (which never exists).

Workaround: use CLI scrape mode (`dotnet run -- --sites ...`) rather than the GUI.

### "nav-diagnostic shows 200 pages but the real site has 1,400"

The `nav-diagnostic` profile has breadth limits (20 children/page, 70 pages/depth).
It samples the site structure — it is not a full crawl. Use `full-validation` or
production scrape mode for complete coverage.

### "The diagnostic output looks like a real archive"

It is not. Diagnostic runs produce `index.htm`, `pages/*.htm`, and `nav.json` — the same
visual artifacts as a production archive — but with crawl limits, possible missing screenshots,
and no guarantee of completeness. Do not distribute diagnostic output as an archive.

### "Running `--quick-preview` in diagnostic mode has no effect"

The `--quick-preview` flag is only parsed for scrape mode. Diagnostic mode uses per-profile
breadth limits (`maxChildrenPerPage`, `maxPagesPerDepth`) that cannot be overridden from
the CLI. To change breadth limits in diagnostic mode, edit `config/diagnostic-profiles.json`.

---

## Recommended Workflows

### Develop and validate a new CMS target

```
1. Register target in config/cms-targets.json
2. smoke        — verify connectivity and CMS detection
3. cms-diagnostic — confirm nav extraction sees correct structure
4. nav-diagnostic — main iteration loop (repeat as you tune the navigator)
5. compare      — diff against previous nav-diagnostic baseline
6. acceptance   — confirm no regressions
7. snapshot-diagnostic — verify screenshot capture works
8. full-validation — final pre-release confidence run (1500 pages, full screenshots)
9. Production scrape — actual archive production (separate from diagnostic)
```

### Quick check after a code change

```
1. nav-diagnostic (baseline run before change)
2. Make the code change
3. nav-diagnostic (candidate run after change)
4. compare --previous baseline --current candidate
5. acceptance --comparison candidate/comparison-report.json
```

### Verify screenshot quality on a new site

```
1. snapshot-diagnostic — 40 pages with screenshots, fast
2. Inspect shots/ for blanks (< 20 KB), consent failures, tiling issues
3. If good, proceed to full-validation
```

---

## Archive-Grade vs Diagnostic-Only Runs

| Run type | Archive-grade? | Notes |
|---|---|---|
| CLI scrape mode (any flags) | **Yes** | Full screenshots, all pages within limits |
| GUI scrape mode | **Yes** (with viewer bug caveat) | Same as CLI but viewer link is broken |
| `diagnostic --profile full-validation` | **Yes** (screenshots on) | In `diagnostics/` folder, not `output/` |
| `diagnostic --profile snapshot-diagnostic` | Partial (40 pages only) | Good for screenshot QC, not site coverage |
| `diagnostic --profile nav-diagnostic` | **No** | No screenshots; navigation check only |
| `diagnostic --profile cms-diagnostic` | **No** | No screenshots; CMS detection check only |
| `diagnostic --profile smoke` | **No** | 1 page; pipeline connectivity check only |

---

## What `--quick-preview` Actually Means

`--quick-preview` (also `--preview`, `--test-preview`) activates breadth limiting in
**scrape mode only**. It is unrelated to diagnostic profiles.

When active:
- `--preview-pages-per-depth` (default 25): cap on pages per depth level
- `--preview-children-per-page` (default 18): cap on links followed per page
- `--preview-seconds` (default 90): hard timeout on the entire run

Use quick-preview to get a fast representative sample of a site's navigation structure
before committing to a full scrape. The output is a real archive but covers a fraction
of the site.

---

## Known Bugs

### GUI viewer link is broken (AppRunner)

When running via the GUI (`dotnet run` with no args, or `dotnet run -- gui`), the nav
viewer written by `SiteViewerBuilder` to `index.htm` is overwritten by the GUI's entry
page. The entry page links to `viewer.htm`, which is never written. Scrape output from
the GUI has a broken viewer link at the per-scrape level.

**Workaround:** Use CLI scrape mode instead of the GUI:
```powershell
dotnet run -- --sites sites.txt --output output
```

**Tracked in:** [operational-audit.md](operational-audit.md) item C-2 and F-1.

---

## Local Development

### Browser override

If the default Playwright headless shell crashes on your machine (ICU data error at startup), point it at a working revision:

```powershell
$env:PLAYWRIGHT_CHROMIUM_SHELL = "$env:LOCALAPPDATA\ms-playwright\chromium_headless_shell-1223\chrome-headless-shell-win64\chrome-headless-shell.exe"
```

Set this in your PowerShell profile (`$PROFILE`) so it persists across sessions. The override is a no-op on machines where the default shell works.

---

## Engineering History and Baselines

The `diagnostics/`, `diagnostics_final/`, and `overnight_batch_sitevision/` directories
contain deliberate engineering evidence — not clutter. Before deleting any run folder,
read the documents listed below.

**Do not blindly delete old diagnostic run folders.** Many are before/after references for
specific bugs. Deleting the pre-fix blank-screenshot run (`diagnostics/wordpress_municipio_eslov/2026-05-25_115541/`)
or the pipeline milestone run (`diagnostics/sitevision_ystad/2026-05-24_190930/`) would
permanently destroy regression evidence that cannot be reconstructed.

### Reference documents (read before cleaning up)

| Document | What it covers |
|---|---|
| [engineering-history.md](engineering-history.md) | 7-phase architectural evolution, motivating failures, fixes |
| [milestone-runs.md](milestone-runs.md) | Every KEEP_BASELINE run with its exact path and reason |
| [cms-baselines.md](cms-baselines.md) | Best-available quality metrics for each supported CMS |
| [regression-history.md](regression-history.md) | All known bugs, evidence runs, fixes, open issues |
| [diagnostics-evolution.md](diagnostics-evolution.md) | Telemetry events, quality metric additions, saturation model |
| [cleanup-plan.md](cleanup-plan.md) | KEEP / SAFE_DELETE / REVIEW_MANUALLY classification of every run folder |
| [archive_inventory/README.md](archive_inventory/README.md) | Directory orientation guide |

### Key preserved runs

- `diagnostics/sitevision_ystad/2026-05-24_190930/` — only run with full end-to-end pipeline proof. **Never delete.**
- `diagnostics/wordpress_municipio_eslov/2026-05-25_115541/` and `2026-05-25_210525/` — before/after for the blank-screenshot bug. **Never delete either.**
- `output_manual_test/Eslov/_synthetic_parent_audit/` — visual proof of the root pollution fix. **Never delete.**
- `diagnostics/_audit_screenshots/rendering_fix_validation/rendering-fix-validation.md` — irreplaceable root-cause document. **Never delete.**

---

## Copyright and License

Copyright © 2026 Dennis.

All rights reserved.

This software is proprietary. No permission is granted to use, copy, modify, distribute, or create derivative works without explicit prior written permission from the copyright holder.

The software is provided “as is”, without warranty of any kind. The author shall not be liable for any damages arising from its use.
