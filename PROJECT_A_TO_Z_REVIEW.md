# Executive Summary

WebSnapshots is a capable, unusually well-observed single-machine archival tool. It has a clear product purpose, a practical Windows GUI and CLI, a strong Playwright-based capture pipeline, static outputs that are easy to preserve, and a navigation system that has been improved through real municipal-site evidence rather than broad rewrites.

The project is healthy enough for controlled production use by an informed operator. It is not yet ready for a low-friction open-source launch or unattended production operation.

The strongest part of the codebase is the navigation-quality work:

- deterministic CMS-aware extraction
- homepage-anchor preservation
- separation of visible links from structural crawl seeds
- URL-hierarchy reconstruction
- synthetic URL-prefix parents
- municipal-root classification with telemetry
- bounded diagnostic profiles and expanded visual QA

The largest risks are operational and maintainability risks around that behavior:

- `NavCrawler.cs` is a 3,000+ line policy engine, queue manager, extraction coordinator, topology rebuilder, and telemetry producer.
- Production orchestration is duplicated in `Program.cs` and `AppRunner.cs`; the GUI currently calls `Program.RunAsync`, leaving `AppRunner` as a drift-prone parallel implementation.
- Partial failures and cancellation can leave archive folders without consistent `scrape.json`, entry pages, or final `run.json`.
- The storage governor has incremental APIs, but capture repeatedly performs full recursive output-folder scans instead.
- Navigation regressions are validated with useful scripts and documentation, but there is no automated test project and no checked-in golden fixture corpus.
- Release packaging works, including bundled Chromium, but it is manual, unsigned, and not fully reproducible from the project file.

The right next move is not a rewrite. Freeze navigation semantics, add tests around current outputs, harden run-state consistency, unify orchestration, and automate release validation.

# Overall Assessment

## Health Rating

**Overall project health: good for supervised production, moderate risk for long-term maintenance, not yet open-source ready.**

| Area | Assessment | Notes |
| --- | --- | --- |
| Product concept | Strong | Clear scope: rendered visual archive, not HTML mirroring. |
| Core capture | Good | Playwright, WebP, full-page capture, tiled fallback, recovery, and static viewer are practical choices. |
| Navigation quality | Strong but delicate | Sophisticated and evidence-driven, with high regression sensitivity. |
| Maintainability | Needs work | Concentrated large files and orchestration duplication raise change risk. |
| Operational safety | Mixed | Cancellation and recovery exist, but partial-run metadata and storage behavior need hardening. |
| Diagnostics | Strong foundation | Telemetry and reports are valuable; reproducibility and automation lag behind. |
| Testing | Weak | No conventional automated unit or integration test project is present. |
| Packaging | Functional but manual | Bundled Chromium works; signing, checksums, CI, and installer strategy remain open. |
| Open-source readiness | Not ready | Proprietary license, no contributor docs, no automated test baseline, and release process is local/manual. |

## Review Basis

This review was performed read-only except for creating this report. No code was patched, no behavior was changed, and no long scrapes were run.

The assessment covered tracked source files, documentation, diagnostic configuration, audit tools, release folders, Git tags, release ZIP metadata, and the existing local working tree. The pre-existing `.gitignore` modification was left untouched.

# Architecture Review

## Current Shape

The project is a .NET 8 Windows Forms application with CLI subcommands and a static-output architecture:

1. `Program.cs` selects GUI, CLI scrape, local server, diagnostic, compare, review-pack, or acceptance mode.
2. `NavCrawler.cs` detects CMS behavior, seeds and traverses navigation, reconstructs hierarchy, and applies root policy.
3. `Snapshotter.cs` renders each archived page, extracts text, captures WebP screenshots, and writes static page wrappers.
4. `SiteViewerBuilder.cs` builds the per-site static viewer.
5. `TopIndexBuilder.cs`, `MunicipalityIndexBuilder.cs`, and `GlobalIndexBuilder.cs` build archive indexes.
6. `DiagnosticRunner.cs` runs bounded evidence-producing crawls.
7. `TelemetryWriter.cs` and `Analysis/*` produce structured reports, comparisons, review packs, and acceptance decisions.

This is a sensible architecture for a local archive tool. Static outputs reduce deployment complexity, browser-context reuse keeps capture practical, and the diagnostic pipeline is deliberately separate from normal archives.

## Separation Of Concerns

### Good Separation

- `PlaywrightRunner.cs` owns browser startup, contexts, and interaction helpers.
- `Snapshotter.cs` owns capture and text extraction orchestration.
- `SiteViewerBuilder.cs` builds archive UI without needing a live server.
- `Diagnostic/*`, `Telemetry/*`, and `Analysis/*` are distinct layers.
- `MunicipalRootClassifier.cs` centralizes root eligibility better than scattered viewer-only checks would.
- `AtomicWrite.cs` is a useful small utility for durable page-wrapper writes.

### Weak Separation

- `NavCrawler.cs` combines queue management, crawl retries, CMS extraction coordination, sitemap seeding, URL normalization decisions, link filtering, hierarchy reconstruction, synthetic-node insertion, root suppression, municipal-root policy application, title handling, and telemetry emission.
- `CmsAwareNavExtractor.cs` is also large and mixes SiteVision, WordPress, and generic extraction paths in one class.
- `SiteViewerBuilder.cs` reclassifies municipal roots during rendering, while `NavCrawler.cs` also classifies roots during topology generation. The classifier is centralized, but policy application remains split across stages.
- Production run orchestration exists in both `Program.cs` and `AppRunner.cs`. `MainForm.cs:545` calls `Program.RunAsync`, so `AppRunner` appears unused while still containing a parallel implementation.

## Crawler Design

The crawler is intentionally breadth-bounded and deterministic. Strong design choices include:

- same-site filtering
- CMS detection before extraction
- primary-nav and visible-module extraction
- visible-versus-structural seed separation
- protected homepage-root ordering before breadth-limited sampling
- quick-preview limits
- sitemap seeding outside quick-preview mode
- per-page retry
- post-crawl topology repair
- telemetry around important decisions

The main architectural concern is that the crawler has become the system's central policy container. A future maintenance change can accidentally affect discovery, hierarchy, viewer shape, and diagnostics in one edit.

## Snapshotter Design

`Snapshotter.cs` is robust for a local archival tool:

- one reusable browser context per capture phase
- page-per-URL lifecycle
- text extraction before capture
- full-page screenshot first
- tiled fallback
- blank-screen retry
- cookie-consent handling
- WebP encoding
- stub archive page after a per-page failure
- browser-context recreation after target-closed failures
- atomic wrapper-page writes

The capture pipeline is operationally useful, but it has too many responsibilities in one class. Screenshot readiness, consent handling, WebP encoding, storage accounting, page HTML writing, and recovery policy should eventually be separately testable components.

## Viewer Generation

Static viewer generation is one of the project's best product decisions. It makes archives portable and easy to preserve without a database or service.

`SiteViewerBuilder.cs` correctly distinguishes:

- structural navigation
- visible start-page groups
- utility roots
- discovered or helper roots
- external links
- dynamic links

The concern is policy duplication. Root grouping is computed again during viewer build. A viewer rebuild from the same `nav.json` should ideally be a pure rendering operation with no independent interpretation of root eligibility.

## CMS Detection And Extraction

`CmsDetector.cs` uses deterministic HTML signals for WordPress, SiteVision, Drupal, Umbraco, Optimizely, and Sitecore. This is appropriate and inspectable.

`CmsAwareNavExtractor.cs` provides meaningful CMS-specific handling, especially for SiteVision and WordPress/Municipio. Generic fallbacks in `LocalNavExtractor.cs` are valuable.

Risks:

- CMS behavior is concentrated in a large extractor class.
- Some extraction behavior overlaps between `CmsAwareNavExtractor.cs` and `LocalNavExtractor.cs`.
- The detector and extractor contracts are implicit rather than interface-driven.
- Configuration lists diagnostic targets, but extractor tuning itself is mostly code-based.

## Root Classification Policy

The policy is thoughtfully centralized in `MunicipalRootClassifier.cs`. It returns classification, eligibility, confidence, reasons, evidence, and source signals.

This is a good direction. The policy is deterministic and explainable.

The risk is overfitting:

- lexical tokens are Swedish municipal-domain specific
- `IsMicrositeRoot` includes terms such as `badrike`, `gymnasium`, and `industrifastigheter`
- `IsNewsEventOrServiceRoot` contains specific content-title patterns
- early classifier returns can override strong authored-navigation context

For example, a legitimate authored top-level `Gymnasium` section may be demoted before primary-navigation evidence is considered. That may be correct for current fixtures and wrong on another municipality.

## Telemetry And Logging

The two-layer approach is good:

- `Logger.cs` writes human-readable event logs.
- `TelemetryWriter.cs` writes structured JSONL for diagnostics and analysis.

Telemetry is especially useful around:

- CMS detection
- crawl success and failure
- link rejection
- homepage-anchor scoring
- hierarchy inference
- synthetic-parent creation
- municipal-root policy
- screenshot results
- text extraction

The gap is production parity. Structured telemetry is strongest in diagnostic mode; ordinary GUI and CLI runs primarily rely on text logs.

## CLI Versus GUI

The CLI exposes substantially more tuning than the GUI:

- max pages
- storage cap
- quick preview
- viewport
- WebP quality
- query handling
- delays
- user agent
- timing controls

The GUI currently exposes a narrower subset, principally URL list, output folder, and crawl depth. That is acceptable for a simple operator path, but the project needs an explicit product decision:

- keep the GUI intentionally simple and document advanced CLI use, or
- add named GUI profiles rather than exposing every low-level knob.

## Configuration And Profiles

`SnapshotConfig.cs` has useful defaults and validation. Diagnostic profiles in `config/diagnostic-profiles.json` are a good concept.

Problems:

- unknown CLI flags are silently ignored
- missing CLI values can quietly fall back to defaults
- `UseDatedOutput` is parsed but does not drive output behavior
- production runs do not have named profiles comparable to diagnostics
- GUI and CLI capabilities differ
- diagnostic target config is not bundled in the v1.0.1 release folder

## Diagnostic System

The diagnostic system is a major strength:

- bounded profiles
- deterministic target list
- telemetry
- fingerprints
- quality reports
- before/after comparisons
- AI review packs
- acceptance gate
- expanded visual viewer audit

The architecture is better than a pile of ad hoc logs. The remaining work is to make it reproducible in CI and fresh clones.

## Release Packaging

The v1.0.1 release folder includes:

- self-contained Windows executable and runtime
- `WebSnapshots.bat`
- bundled Chromium under `browsers/chromium-1223`
- Playwright package assets
- operator README

That solves the most important packaging problem: browser availability.

The release process is still manual:

- no explicit publish/package script is tracked
- the project file does not describe self-contained publish or browser-copy steps
- diagnostics config is not bundled
- no checksum manifest is shipped beside the ZIP
- the executable is not Authenticode signed
- no installer is present

The inspected bundled ZIP SHA-256 was:

`0A058F50D455423DB8A475D48409D37D82AAA911E672A995D8139533DE23418A`

# Code Quality Review

## Large Classes

The largest maintenance hotspots are:

| File | Approximate lines | Concern |
| --- | ---: | --- |
| `NavCrawler.cs` | 3,141 | Too many policies and lifecycle stages in one class. |
| `CmsAwareNavExtractor.cs` | 1,826 | Multiple CMS strategies plus browser scripts in one extractor. |
| `Snapshotter.cs` | 914 | Capture orchestration, readiness, storage, encoding, recovery, and HTML output. |
| `SiteViewerBuilder.cs` | 792 | Data shaping, root classification, and large embedded HTML/JS template. |
| `Analysis/QualityReportBuilder.cs` | 790 | Metrics loading, warning policy, saturation, and Markdown rendering. |
| `Program.cs` | 633 | Mode routing, CLI scrape orchestration, diagnostics, and unused parallel path. |
| `MainForm.cs` | 610 | UI layout, folder picker, orchestration, logs, and post-run open behavior. |

These do not justify immediate rewrites. They justify characterization tests before extraction of smaller services.

## Duplicated Logic

### Must Address Soon

- `Program.RunAsync` and `AppRunner.RunAsync` substantially duplicate production orchestration.
- Both contain near-duplicate `ProcessSiteAsync`, shot counting, viewer recovery, scrape entry HTML, manifest construction, and index building.
- `AppRunner` differs subtly from `Program`, including links and logging details. This is drift waiting to happen.

### Should Consolidate Later

- municipal-root policy is applied in crawler and viewer stages
- local and CMS-aware visible-group extraction overlap
- embedded HTML construction is distributed across several builders
- JSON writes use both `AtomicWrite` and direct `File.WriteAllTextAsync`

## Naming And Clarity

Names are generally practical, but a few areas need cleanup:

- `OutputBaseDir` and `OutputDir` are easy to confuse because production sets them to the same path.
- `UseDatedOutput` suggests behavior that is not actually honored.
- `treeFlat`, `allFlat`, `visibleTreeFlat`, `Flat`, and `Nodes` are understandable after study but need a short architecture note.
- `ProcessSitesParallelAsync` is dead optional code, not an active feature.
- `AppRunner` looks authoritative but is not used by the GUI.

## Hidden Coupling

- `Snapshotter` reloads `nav.json` to derive tree order even though it also receives `flat`.
- Viewer classification depends on `NavGroups`, `HomepageSections`, root node shape, and classifier behavior.
- Output indexes depend on `scrape.json`, but error paths do not reliably write it.
- `SimpleWebServer` assumes archive paths are safe based on a string prefix check.
- release launcher behavior depends on environment variables set by `WebSnapshots.bat`.

## Fragile Assumptions

- daily folder naming (`yyMMdd`) plus suffixing is operator-friendly but not concurrency-safe
- run IDs use second-level timestamp precision
- `HostToMunicipality` derives folder names from the first host label only
- `SafeFileBaseFromUrl` always drops queries, even when the crawl is configured to retain them
- screenshot blank detection uses PNG byte size as a proxy
- sitemap fetching accepts all TLS certificates
- viewer and crawler independently interpret municipal roots
- current regression expectations are encoded as text containment checks against a small fixture set

## Missing Documentation

Add concise maintainer documentation for:

- data model: `allFlat`, `treeFlat`, visible tree, groups, and generated `nav.json`
- run-state lifecycle and partial-run states
- output-folder contract
- release build and package procedure
- how to add a CMS extractor safely
- how to add a regression fixture
- browser override and bundled Chromium version coupling
- threat model for local serving and remote crawling

# Operational Safety Review

## Cancellation

Cancellation exists in GUI and CLI and is checked throughout crawl and capture loops.

Strengths:

- Ctrl+C is handled in CLI.
- GUI stop resumes a paused run before cancellation.
- `PauseController.WaitIfPaused(ct)` is cancellation-aware.
- ImageSharp encoding receives the cancellation token.

Gaps:

- cancellation during `Program.RunAsync` exits before final `run.json`, indexes, and global index rebuild
- failed or cancelled site folders may not get `scrape.json`
- sitemap HTTP requests do not receive a cancellation token
- in-flight Playwright navigation and browser waits are timeout-bounded but not immediately cancellation-aware

## Pause And Resume

Pause/resume is adequate for supervised operation. It pauses between meaningful steps.

It is not checkpoint/resume:

- application restart cannot resume a run
- pause does not suspend an in-flight browser request
- no persisted queue or progress journal exists

Do not add persistent resume yet. First make partial-run metadata consistent.

## Error Handling

Strengths:

- crawl page visits retry once
- snapshot failures create stub pages and continue
- target-closed snapshot failures recreate the browser context
- top-level site errors do not necessarily stop later sites
- diagnostic mode records critical crawl failures

Gaps:

- `TryBuildViewerAsync` suppresses all exceptions
- several cleanup catches suppress failures without leaving telemetry
- browser recovery is stronger in snapshotting than crawling
- partial site status is not consistently materialized into `scrape.json` and entry HTML

## Storage Governor

The storage governor needs attention before wider production.

Current evidence:

- `StorageGovernor.RegisterWrite` exists but has no call sites.
- `Snapshotter.cs:153`, `Snapshotter.cs:674`, and `Snapshotter.cs:810` perform recursive directory-size scans.
- tiled pages can scan the entire output folder once per tile.
- `Utils.GetDirectorySizeBytes` returns `0` after some failures, creating a fail-open cap check.

Recommended direction:

- establish one output-root storage policy
- initialize from one disk scan
- register actual successful writes
- recalibrate periodically
- define behavior when disk-size enumeration fails
- add minimum-free-disk checks

## Browser Recovery

Snapshot browser recovery is a strength. `Snapshotter` recreates context and resets the browser after target-closed errors.

Crawler recovery is weaker:

- it retries navigation on the existing page
- it does not recreate a closed page/context/browser inside the nav-crawl loop

Add characterization tests and then align crawler recovery with snapshot recovery.

## Retry Behavior

Retries are intentionally limited, which is good. Avoid broad retry escalation.

Improvements:

- classify timeout, DNS, TLS, blocked, HTTP status, and browser-crash outcomes separately
- emit retry outcome telemetry
- consider a small backoff policy object shared by crawler and snapshotter

## Partial Run Cleanup And Temp Files

Current behavior preserves evidence, which is better than deleting aggressively.

Gaps:

- `.tmp` WebP files can remain after encoding failure or cancellation
- cancelled `_runs/{runId}` folders can lack `run.json`
- failed scrape folders can lack `scrape.json` and entry pages
- no startup or explicit maintenance command reports stale temp files

Recommended direction:

- mark partial artifacts, do not silently delete them
- write status metadata in `finally`
- clean only known temporary file patterns within the active scrape directory
- add an opt-in `doctor` or `cleanup-report` command before adding deletion

## Stale Date Folders

No retention or stale-folder policy exists. That is acceptable for an archive product, because deletion should be conservative.

Add reporting before cleanup:

- list incomplete folders
- list folders without `scrape.json`
- list stale `.tmp` files
- list `_runs` folders without `run.json`
- list archive sizes and last-modified times

## Run Manifest Consistency

This is a **must-fix-now** issue.

Normal success writes:

- site archive
- viewer
- scrape entry pages
- `scrape.json`
- run index
- municipality index
- `run.json`
- global index

Error, cap, and cancellation paths do not consistently write the same minimum metadata. A run result may point at an entry page that was never created.

Define a minimum artifact contract for every status:

- `OK`
- `ERROR`
- `CAP_REACHED`
- `CANCELLED`
- `PARTIAL`

Each site folder should have a status-bearing `scrape.json` and a usable entry page even when capture is incomplete.

## Output Folder Safety

The scraper generally writes beneath the selected output path and does not perform broad deletion. That is good.

Risks:

- GUI allows high-level folder selection without an archive-root marker.
- recursive storage scans may traverse a much larger folder than intended.
- multiple concurrent app instances can race on daily folder suffixes.
- `SimpleWebServer.cs:50` checks `full.StartsWith(rootDir)`, which is not a path-boundary-safe containment check. A sibling path such as `C:\archive-other` can share the `C:\archive` prefix.

The local server binds to `localhost`, reducing exposure, but containment should still use a separator-aware boundary check.

# Navigation System Review

## Overall Assessment

The navigation system is the project's differentiator. It is also the area where uncontrolled refactoring would be most dangerous.

## MunicipalRootClassifier

Strengths:

- centralized decision API
- deterministic output
- explicit classifications
- confidence values
- reasons and evidence
- source-context signals
- canonical URL helpers
- utility, microsite, event, noticeboard, and structural handling

Risks:

- Swedish lexical assumptions are embedded in code
- some fixture-shaped patterns are encoded directly
- early hard exclusions can outweigh authored primary-nav evidence
- title substring matching can produce false positives
- crawler and viewer apply policy separately

Recommendation:

- preserve current behavior for v1.0.x
- add classifier table tests for every token family
- include positive and negative counterexamples
- move policy data to a reviewed table only after tests exist

## Root Eligibility Policy

The current model correctly distinguishes archive inclusion from root-navigation eligibility. Demoted items remain visible in helper groupings rather than disappearing.

That is the right product behavior.

The next improvement is not more rules. It is test coverage around current rules and explicit policy versioning in `nav.json` or scrape metadata.

## Homepage Anchor Handling

The recent Eslov fix is architecturally sound:

- accepted homepage roots are promoted
- promoted roots are ordered before broader descendant-heavy samples
- protected roots survive breadth limits

This should remain frozen until golden fixture tests exist.

## Queue Seeding

The crawler has multiple seed sources:

- start page
- CMS-aware structural nav
- visible structural modules
- local fallback links
- sitemap URLs

This is powerful but complex. Add telemetry summaries that make the final seed budget obvious by source and deduplicated URL count.

## URL Hierarchy Reconstruction

The post-crawl URL-parent reconstruction is a good, conservative technique:

- it only reparents root-level candidates
- it prefers known ancestors
- it handles SiteVision numeric IDs
- it handles WordPress clean slugs
- it avoids semantic guessing

Keep this behavior. Test it with checked-in synthetic `nav.json` inputs.

## Synthetic Parents

Synthetic URL-prefix parents are justified and well documented:

- missing intermediate nodes are common
- root pollution is visibly harmful
- shared URL prefixes are deterministic evidence
- synthetic nodes are marked
- numeric and date slugs are filtered

Test edge cases:

- one-child prefix
- two-child prefix
- nested missing prefixes
- existing real parent
- numeric SiteVision slug
- date slug
- query and fragment normalization
- cycles
- title replacement when a real parent later exists

## Visible Versus Structural Navigation

The `IsDisplayOnly` split is a strong design decision. It preserves viewer richness while controlling crawl-budget waste.

The current URL-depth and date-prefix heuristics are intentionally conservative. Add fixture cases for shallow article URLs and deep-but-structural URLs before tuning them.

## OVRIGT And Helper Grouping

Separating structural navigation from helper content is correct. It prevents microsites, alerts, utilities, campaigns, and start-page modules from polluting municipal root navigation while keeping content discoverable.

The risk is not the concept. The risk is classification drift between crawler output and viewer rebuild.

## Overfitting Risk

Current regression coverage is heavily municipal and Swedish, especially SiteVision and WordPress/Municipio.

Do not claim generic website reconstruction quality yet. Keep the README limitation prominent and expand fixtures gradually:

- another Swedish SiteVision municipality with different root labels
- another Municipio site
- shallow-slug WordPress
- Umbraco municipality
- Optimizely municipality
- non-municipal public-sector site
- generic small business or documentation site

# Diagnostics and QA Review

## Quality Reports

`Analysis/QualityReportBuilder.cs` is valuable. It measures:

- flat pages
- tree nodes
- orphans
- duplicate URLs and titles
- empty titles
- root-child counts
- deep-root pollution
- synthetic parents
- homepage anchors
- utility roots
- failures and timeouts
- screenshot and text-extraction results
- rejection patterns
- saturation

This is the right evidence set for navigation work.

Improve it by:

- versioning the report schema
- recording app version and Git commit
- recording Chromium revision
- recording OS/runtime details
- recording whether the run was GUI, CLI, or diagnostic
- recording effective configuration after defaults

## Telemetry Usefulness

Telemetry is useful and inspectable. Keep it deterministic.

Avoid expanding telemetry indefinitely. Prefer a documented event schema and stable event names. Add an event catalog with required fields and intended consumers.

## Expanded Visual Audit

`Capture-ExpandedViewerAudit.ps1` and `_audit_tool/VisualAuditExpanded` are good QA tools:

- reusable output path
- expanded tree
- nav-pane scroll slices
- iframe scroll slices
- root text manifest
- visible-nav text manifest
- capture index

Gaps:

- defaults are tied to `D:\WebSnapshots-main\output_precaution_run`
- audit success is mostly manual inspection
- there is no baseline-image diffing
- freshness of input viewers is not automatically proven

## Regression Fixture Set

The documented five-municipality regression suite is valuable:

- Eslov
- Kristianstad
- Ystad
- Klippan
- Hassleholm

It protects the recent root-policy fixes. However:

- checked-in fixture data is absent
- validation depends on local scrape outputs
- `RootPolicyValidate` hardcodes local paths and date folders
- release validation is documented, not CI-enforced

## Diagnostic Complexity

The diagnostic system is justified, but it is approaching a complexity threshold.

Before adding more metrics:

1. define a report schema version
2. define an event catalog
3. identify release-blocking metrics
4. identify informational metrics
5. automate fixture assertions

## Reproducible Release Validation

Release validation is not fully reproducible from a clean clone.

Required improvements:

- checked-in small no-scrape fixture artifacts
- parameterized root-policy validation tool
- automated `nav.json` golden comparison
- automated viewer rebuild
- deterministic rendered-text assertions
- optional visual diffs
- tracked release script

# Performance Review

## Current Execution Model

Production site scraping is sequential. There is an unused `ProcessSitesParallelAsync` helper in `Program.cs`, capped at three sites.

Sequential execution is the correct default today:

- simpler storage accounting
- lower browser pressure
- easier log interpretation
- lower risk of target-site overload
- fewer race conditions in daily output folders

## Parallel Site Scraping

Do not enable parallel production scraping yet.

Before enabling it:

- unify production orchestration
- make output folder allocation concurrency-safe
- make manifest updates incremental and atomic
- verify Playwright runner concurrency behavior
- make storage accounting accurate under concurrency
- add per-host and global concurrency settings
- add conservative defaults

## Playwright Context Reuse

Current reuse is reasonable:

- crawler uses one context and page
- snapshotter uses one context and creates a page per URL
- browser process is reused
- snapshot context is recreated after target closure

This is a good balance for a local tool.

## Wait And Stabilization Tuning

Wait behavior is pragmatic but distributed:

- nav timeout
- shot timeout
- network idle
- stabilization loop
- scroll settle
- lazy-load delay
- cookie settle
- blank-screen retry delay

Move effective timing values into telemetry and eventually into named capture profiles. Avoid adding more magic constants without fixture evidence.

## WebP Encoding Overhead

The pipeline captures PNG in memory and re-encodes to WebP with ImageSharp. That is reasonable, but CPU and memory cost can be high for long pages.

Measure before optimizing:

- PNG byte size
- WebP byte size
- encode milliseconds
- full-page image dimensions
- tiled fallback frequency
- peak working set during representative runs

## Disk I/O

Disk I/O is the clearest avoidable performance issue:

- recursive output-folder scanning occurs repeatedly
- tiled fallback can scan per tile
- the scan includes historical archives under the same output base

Use governor recalibration plus registered writes after current behavior is covered by tests.

## Memory Pressure

Potential pressure points:

- full-page PNG byte arrays
- ImageSharp decode plus WebP encode
- large page DOM extraction
- telemetry read-all-lines in report generation
- large `nav.json` and viewer HTML serialization

For current bounded runs this is acceptable. For long-term production:

- record dimensions and encode timing
- stream telemetry analysis line-by-line
- set a maximum full-page pixel threshold before falling back to tiles

## Browser Crash Recovery

Snapshot recovery is useful and should be extended carefully to nav crawling. Record browser relaunch counts in quality reports.

# Product Readiness Review

## README

The README is concise and useful:

- scope is clear
- GUI and CLI examples exist
- output structure is documented
- bundled Chromium behavior is explained
- limitations are honest
- troubleshooting covers common operator issues

Add:

- supported Windows versions
- expected disk usage guidance
- a note that the GUI is the simple path and CLI exposes advanced tuning
- a production checklist
- how to verify a release checksum
- how to report a problematic site safely
- a pointer to maintainer architecture docs

## Release Notes

The v1.0.1 notes explain the navigation fix and fixture validation well.

Add release artifacts and verification details:

- exact ZIP filename
- SHA-256 checksum
- Chromium revision
- .NET runtime packaging mode
- known warnings
- signing status

## Bundled Chromium

Bundling Chromium is the right operator experience. The inspected v1.0.1 package is large but self-contained:

- unpacked release folder: approximately 705 MB
- bundled browser subtree: approximately 432 MB
- bundled ZIP: approximately 298 MB

Track Chromium revision explicitly in release metadata and automate verification that `WebSnapshots.bat` points at an existing executable.

## SmartScreen And Code Signing

The inspected `WebSnapshots.exe` is not digitally signed.

That matters for:

- SmartScreen reputation
- operator trust
- enterprise deployment
- release provenance

Recommended path:

1. ship checksums immediately
2. document unsigned status honestly
3. obtain Authenticode signing before broad external distribution
4. sign executable and installer/package where applicable

## License

The README currently states proprietary, all-rights-reserved terms. That is incompatible with an open-source release.

Before open sourcing:

- choose an OSI-approved license
- add a top-level `LICENSE` file
- confirm ownership of all code and assets
- document third-party dependencies and licenses
- decide contribution policy

Do not change licensing casually. This is a product-owner decision.

## GitHub Release Hygiene

The repository has `v1.0.0` and `v1.0.1` tags and focused release notes. Good.

Improve release hygiene:

- use a tracked release script
- generate checksums
- verify ZIP contents
- include release notes in GitHub release body
- attach one canonical bundled ZIP
- avoid ambiguous multiple ZIP variants unless clearly documented
- publish provenance: tag, commit, checksum, Chromium revision

## Installer Versus ZIP

Keep ZIP as the primary artifact for now. It is transparent and fits a portable archive tool.

Consider an installer later if:

- non-technical operators become common
- signing is in place
- update strategy is defined
- Start Menu integration and uninstall support are valuable

## User Onboarding

Improve onboarding with:

- a first-run smoke capture button or documented example
- a simple-vs-advanced modes explanation
- output-folder guidance
- storage-cap guidance
- a post-run summary with archive path, status, pages, failures, and disk use

## Troubleshooting

Add troubleshooting entries for:

- SmartScreen warning
- antivirus quarantine
- unsigned executable
- bundled Chromium path failure
- site blocks automation
- archive folder selected on cloud-synced storage
- incomplete run after cancellation
- disk-cap stop
- stale `.tmp` files
- viewer use through `serve`

# Testing Strategy

## Priority 1: Unit Tests

Create a conventional test project and cover deterministic logic first.

Test:

- `Utils.NormalizeUrl`
- `Utils.SafeFileBaseFromUrl`
- same-host rules including `www`
- query handling
- `MunicipalRootClassifier`
- `NavTreeBuilder`
- synthetic-parent scoring
- URL-parent inference
- display-only URL classification
- output-folder containment helper
- config parsing and validation

## Priority 2: Checked-In Fixture Tests

Add small sanitized fixture artifacts, not full scrapes:

- representative `nav.json`
- expected root-navigation text
- expected helper-group text
- expected quality metrics
- expected classifier decisions

Include the five current municipalities and synthetic edge-case fixtures.

## Priority 3: No-Scrape Viewer Rebuild Tests

Given checked-in `nav.json`:

1. rebuild `viewer.htm`
2. parse rendered data or open it with Playwright
3. assert structural roots
4. assert OVRIGT/helper groups
5. assert visible start-page groups
6. assert external-link handling
7. assert screenshot and text-page link mapping

These tests protect viewer changes without network variability.

## Priority 4: nav.json Golden Tests

For bounded diagnostic runs:

- normalize volatile fields
- compare structural roots
- compare critical parent-child edges
- compare synthetic parent set
- compare demoted root set
- compare quality-report thresholds

Avoid requiring every page count to remain identical when the live web changes.

## Priority 5: Regression Fixture Automation

Parameterize `_audit_tool/RootPolicyValidate`:

- remove hardcoded `D:\WebSnapshots-main`
- accept fixture manifest JSON
- accept input root and output root
- support no-scrape mode
- return non-zero on assertion failure

Run it in CI against checked-in artifacts.

## Priority 6: Visual Screenshot Comparisons

Use visual comparisons selectively:

- root expanded
- root collapsed
- bottom of long nav pane
- helper groups expanded
- page wrapper with single screenshot
- page wrapper with tiles
- failed page stub

Use tolerances and text assertions. Do not make pixel-perfect live-site screenshots a hard CI gate.

## Priority 7: Command And Profile Matrix

Automate a small matrix:

| Mode | Input | Expected |
| --- | --- | --- |
| CLI help/error | missing sites | clear exit and message |
| CLI smoke | local fixture or tiny local server | complete manifest |
| GUI orchestration service | mocked/local target | same artifacts as CLI |
| diagnostic smoke | checked-in/local target | telemetry and quality report |
| compare | two reports | deterministic verdict |
| review-pack | one report | generated Markdown |
| acceptance | comparison report | expected exit code |
| serve | traversal attempts | blocked outside-root access |

# Risk Register

## High Risk

### 1. Partial-run metadata inconsistency

**Why it matters:** Failed, capped, or cancelled runs can leave misleading indexes and folders that are hard to diagnose or preserve safely.

**Current evidence:** Site metadata and entry pages are written only on the normal success path in `Program.cs`. Error and cap paths can add results pointing to missing entry files. Cancellation escapes before final run manifest generation.

**Recommended mitigation:** Define a minimum artifact contract and write status-bearing `scrape.json`, entry HTML, and incremental `run.json` atomically for every terminal state.

### 2. No automated regression suite for navigation policy

**Why it matters:** Root classification, hierarchy repair, and helper grouping are high-value behavior with a history of regressions.

**Current evidence:** Good manual validation docs and audit tools exist, but no conventional automated test project or checked-in fixture corpus was found.

**Recommended mitigation:** Add unit tests, checked-in sanitized `nav.json` fixtures, no-scrape viewer rebuild assertions, and CI.

### 3. Storage governor is inefficient and partially disconnected

**Why it matters:** Large archives can become progressively slower and disk-cap behavior can become unreliable.

**Current evidence:** `RegisterWrite` is unused; capture scans the recursive output tree repeatedly, including per tile; size enumeration can fail open.

**Recommended mitigation:** Initialize once, register writes, periodically recalibrate, handle enumeration failures explicitly, and add free-space checks.

### 4. Local server path containment is not boundary-safe

**Why it matters:** A crafted local request may escape into a sibling path whose name shares the archive-root prefix.

**Current evidence:** `SimpleWebServer.cs` uses `full.StartsWith(rootDir)` without a separator-aware containment helper.

**Recommended mitigation:** Use canonical separator-aware containment checks and add traversal tests.

### 5. Root-policy overfitting risk

**Why it matters:** Fixes for known municipalities can demote legitimate roots on unseen sites.

**Current evidence:** Swedish lexical heuristics and fixture-shaped patterns exist in `MunicipalRootClassifier.cs`, including blanket microsite terms evaluated before authored-nav boosts.

**Recommended mitigation:** Freeze behavior, add classifier tables with counterexamples, expand fixtures, and only then tune policy.

## Medium Risk

### 6. Duplicated production orchestration

**Why it matters:** CLI and GUI behavior can drift, and fixes may be made in the wrong runner.

**Current evidence:** `Program.cs` and `AppRunner.cs` substantially duplicate scrape orchestration; GUI calls `Program.RunAsync`.

**Recommended mitigation:** After tests, choose one production application service and route GUI and CLI through it.

### 7. Crawler browser recovery is weaker than snapshot recovery

**Why it matters:** A Chromium crash during navigation discovery can degrade or abort a whole site.

**Current evidence:** Snapshotter resets browser/context on target closure; crawler retries on the existing page.

**Recommended mitigation:** Add shared browser-recovery policy after capturing current behavior in tests.

### 8. Release packaging is manual and unsigned

**Why it matters:** Manual packaging is error-prone; unsigned binaries trigger trust and SmartScreen friction.

**Current evidence:** Bundled release exists and works, but no tracked packaging script was found and Authenticode status is `NotSigned`.

**Recommended mitigation:** Track package script, validate contents, emit checksums, and plan signing.

### 9. Viewer rebuild is not a pure rendering step

**Why it matters:** Rebuilding a viewer can reinterpret navigation differently as classifier code evolves.

**Current evidence:** Root classification runs in crawler and viewer generation.

**Recommended mitigation:** Persist classification results and policy version; make viewer consume the archive contract.

### 10. Temporary and stale artifacts lack a reporting policy

**Why it matters:** Interrupted runs accumulate artifacts and make archive trust harder.

**Current evidence:** WebP `.tmp` files can remain; stale folders are preserved without a doctor/report command.

**Recommended mitigation:** Add non-destructive inventory reporting, then narrowly scoped temp cleanup.

### 11. CLI parsing is permissive

**Why it matters:** Mistyped options can silently produce unexpected long runs.

**Current evidence:** Unknown flags are ignored and some malformed values fall back to defaults.

**Recommended mitigation:** Add strict parsing, `--help`, effective-config output, and tests.

### 12. Sitemap TLS validation is disabled

**Why it matters:** Sitemap discovery can accept invalid certificates and seed untrusted content.

**Current evidence:** `SitemapFetcher.cs` uses `DangerousAcceptAnyServerCertificateValidator`.

**Recommended mitigation:** Default to normal certificate validation; make insecure mode explicit if it is genuinely needed.

### 13. Query-preserving mode can collide at archive filenames

**Why it matters:** Two distinct query-bearing URLs can map to the same static page wrapper and screenshot basename, causing skipped or overwritten archive content.

**Current evidence:** `--keep-query-strings` is documented and parsed, but `Utils.SafeFileBaseFromUrl` always normalizes with `dropQuery: true`.

**Recommended mitigation:** Define query-preserving archive semantics, include retained queries in stable file IDs, and add command-matrix tests.

## Low Risk

### 14. Dead optional parallel runner

**Why it matters:** It suggests support that is not ready and can be activated casually.

**Current evidence:** `ProcessSitesParallelAsync` exists but is unused.

**Recommended mitigation:** Document as experimental or remove after confirming no intended caller.

### 15. Configuration and release folder mismatch

**Why it matters:** Diagnostic commands are less useful from the bundled release.

**Current evidence:** v1.0.1 release folder lacks `config/`.

**Recommended mitigation:** Decide whether diagnostics are developer-only; bundle config or document the boundary.

### 16. README-only proprietary license

**Why it matters:** Open-source users need a clear legal grant.

**Current evidence:** No top-level `LICENSE` file; README states proprietary terms.

**Recommended mitigation:** Make an explicit product-owner licensing decision before open-source release.

# Improvement Roadmap

## Phase 1: Low-Risk Cleanup Before Wider Production

### Must Fix Now

1. Define and document a partial-run artifact contract for `OK`, `ERROR`, `CAP_REACHED`, and `CANCELLED`.
2. Ensure every site folder gets status-bearing `scrape.json` and entry HTML even after partial failure.
3. Write run manifests incrementally and atomically.
4. Fix local-server path containment with a separator-aware helper and traversal tests.
5. Add a tracked release packaging script with ZIP-content validation and SHA-256 generation.
6. Add a first automated test project for deterministic helpers and municipal-root classification.
7. Check in sanitized no-scrape navigation fixtures for the five regression municipalities.

### Should Fix Soon

1. Add effective-config logging and strict CLI option validation.
2. Add a non-destructive `doctor` report for incomplete folders, stale `.tmp` files, and missing manifests.
3. Record app version, Git commit, Chromium revision, runtime, and effective configuration in diagnostics.
4. Parameterize root-policy validation and remove hardcoded local paths.
5. Decide whether diagnostic configs are bundled or explicitly developer-only.

## Phase 2: Medium Refactors After Production Proof

### Should Fix Soon

1. Consolidate `Program.RunAsync` and `AppRunner.RunAsync` into one application service used by GUI and CLI.
2. Make storage accounting incremental with periodic recalibration.
3. Extract a shared run-state writer for `scrape.json`, entry HTML, indexes, and manifests.
4. Align crawler browser recovery with snapshot recovery.
5. Make viewer rebuild consume persisted root classifications rather than independently recomputing policy.
6. Stream telemetry analysis rather than loading all JSONL lines at once.

### Nice To Have

1. Extract screenshot readiness and encoding metrics into a small capture service.
2. Extract HTML templates into separately testable renderers.
3. Introduce schema versions for `nav.json`, `scrape.json`, `run.json`, and quality reports.

## Phase 3: Larger Architecture Improvements

### Nice To Have

1. Split `NavCrawler` into:
   - seed planner
   - crawl engine
   - URL policy/filter
   - topology reconstructor
   - root policy applicator
   - telemetry adapter
2. Split `CmsAwareNavExtractor` into strategy classes per CMS plus a generic strategy.
3. Introduce immutable or clearly staged navigation models:
   - discovered pages
   - structural topology
   - display topology
   - viewer DTO
4. Add local deterministic integration targets served from fixtures.
5. Add policy versioning and migration notes for viewer rebuild compatibility.

## Phase 4: Optional Future Features

### Nice To Have

1. Conservative parallel site scraping with per-host and global limits.
2. Persisted pause/resume checkpoints.
3. Signed installer in addition to portable ZIP.
4. Visual diff dashboard.
5. Archive retention reporting and opt-in cleanup.
6. Additional CMS fixture families.
7. Operator-facing post-run summary and diagnostics bundle export.

## Do Not Touch Yet

1. Do not rewrite the crawler.
2. Do not enable parallel site scraping in production.
3. Do not broadly retune root-classifier thresholds or Swedish lexical rules.
4. Do not remove synthetic parents.
5. Do not remove visible-versus-structural separation.
6. Do not replace deterministic policy with AI inference.
7. Do not add destructive stale-folder cleanup before a report-only doctor mode exists.
8. Do not change archive output layout casually; users may preserve and link old archives.

# Top 10 Recommendations

1. Make partial-run metadata and manifests consistent for success, error, cap, and cancellation.
2. Add checked-in sanitized `nav.json` fixtures and automate the five-municipality root-policy regression suite.
3. Create a conventional test project for classifier, tree builder, URL policy, config parsing, and path containment.
4. Consolidate GUI and CLI production orchestration into one application service after tests exist.
5. Replace repeated recursive disk scans with registered writes plus periodic storage recalibration.
6. Fix `SimpleWebServer` path containment and test traversal attempts.
7. Parameterize local audit tools and make no-scrape viewer rebuild validation reproducible from a clean clone.
8. Track release packaging, browser-copy verification, SHA-256 generation, and artifact inspection in a script.
9. Ship checksums now and plan Authenticode signing before broad distribution.
10. Freeze navigation semantics until characterization tests protect current Eslov, Kristianstad, Ystad, Klippan, and Hassleholm behavior.

# What Not To Touch Yet

The recent navigation corrections should remain release-frozen while tests are added around them.

Preserve:

- accepted homepage-root promotion and priority seeding
- SiteVision numeric-ID handling
- WordPress/Municipio URL hierarchy reconstruction
- synthetic URL-prefix parents
- visible-versus-structural seed separation
- OVRIGT/helper grouping
- deterministic evidence scoring
- bounded diagnostic profiles
- static archive format
- bundled Chromium ZIP distribution

The project needs guardrails and consolidation, not a new architecture imposed all at once.
