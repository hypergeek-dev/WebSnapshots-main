# Executive Verdict

PASS WITH WARNINGS

No navigation regression was introduced by the Eslöv homepage-root preservation fix.

All five bounded diagnostics completed, all generated viewers opened through the expanded audit tool, and all five audits produced ten expanded navigation screenshots plus text manifests. Visual evidence confirms that the fixture-specific expectations are preserved.

The warnings retained below are bounded-crawl quality observations. They do not show a regression caused by the Eslöv change.

# Fixture Summary Table

| Municipality | Viewer | Root Nav | Screenshots | Regression Status | Verdict |
| --- | --- | --- | --- | --- | --- |
| Eslöv | Opens | Seven expected municipal roots | 10 expanded slices | No regression | PASS |
| Kristianstad | Opens | Seven municipal roots; Badrike excluded from root NAVIGATION | 10 expanded slices | No regression | PASS WITH WARNINGS |
| Ystad | Opens | Seven municipal roots; forbidden organizational roots absent | 10 expanded slices | No regression | PASS WITH WARNINGS |
| Klippan | Opens | Seven real municipal roots remain in NAVIGATION | 10 expanded slices | No regression | PASS WITH WARNINGS |
| Hässleholm | Opens | Nine authored roots, including Anslagstavla | 10 expanded slices | No regression | PASS WITH WARNINGS |

# Eslöv

Run:

`D:\WebSnapshots-main\diagnostics_regression_after_eslov_fix\wordpress_municipio_eslov\2026-06-01_130507`

Visual root NAVIGATION contains exactly:

1. Förskola, skola och utbildning
2. Omsorg och stöd
3. Uppleva och göra
4. Bygga, bo och miljö
5. Trafik, gator och parker
6. Arbete och arbetsmarknad
7. Kommun och politik

The expanded screenshots show narrow descendants nested beneath the correct broad municipal roots. No prior substitute roots reappear.

Telemetry confirms:

```text
NAV_PRIMARY_ROOT_PROTECTION protectedRootCount=99 primaryCount=107
VISIBLE_ROOT_CLASSIFICATION_APPLIED rootCandidatesAccepted=7 acceptedPrimaryRootOverlap=7 visibleRootChildrenAfter=7
MUNICIPAL_ROOT_POLICY_SUMMARY beforeRootCount=7 afterRootCount=7 acceptedCount=7 demotedCount=0
```

Quality topology verdict: `PASS`.

# Kristianstad

Run:

`D:\WebSnapshots-main\diagnostics_regression_after_eslov_fix\sitevision_kristianstad\2026-06-01_130813`

Visual root NAVIGATION contains the seven expected municipal roots:

1. Barn och utbildning
2. Omsorg och hjälp
3. Uppleva och göra
4. Bygga, bo och miljö
5. Trafik och resor
6. Jobb och företagande
7. Kommun och politik

`Kristianstads badrike` does not appear as a NAVIGATION root. In the expanded visual audit it is separated under `ÖVRIGT INNEHÅLL`, and Badrike shortcuts also remain visible under the start-page helper area.

Telemetry confirms the root-policy reduction:

```text
MUNICIPAL_ROOT_POLICY_SUMMARY beforeRootCount=14 afterRootCount=7 acceptedCount=7 demotedCount=7
```

Warnings retained: five deep root children and a thirteen-page `Övrigt innehåll` cluster. The screenshots show correct root separation despite those warnings.

# Ystad

Run:

`D:\WebSnapshots-main\diagnostics_regression_after_eslov_fix\sitevision_ystad\2026-06-01_131625`

Visual root NAVIGATION contains:

1. Förskola och skola
2. Omsorg och stöd
3. Samhällsutveckling och trafik
4. Bygga och bo
5. Kommun och politik
6. Uppleva och göra
7. Näringsliv

Neither `Ystad Gymnasium` nor `Ystads Industrifastigheter` appears as a NAVIGATION root.

Telemetry confirms:

```text
VISIBLE_ROOT_CLASSIFICATION_APPLIED rootCandidatesAccepted=7 acceptedPrimaryRootOverlap=7 visibleRootChildrenAfter=7
MUNICIPAL_ROOT_POLICY_SUMMARY beforeRootCount=7 afterRootCount=7 acceptedCount=7 demotedCount=0
```

Warning retained: two deep root children. The screenshots show a sane municipal root topology and separated start-page helper content.

# Klippan

Run:

`D:\WebSnapshots-main\diagnostics_regression_after_eslov_fix\sitevision_klippan\2026-06-01_132413`

Visual root NAVIGATION contains:

1. Utbildning & barnomsorg
2. Omsorg & stöd
3. Bygga, bo & miljö
4. Uppleva & göra
5. Trafik & infrastruktur
6. Näringsliv & arbete
7. Kommun & politik

The real municipal roots remain under `NAVIGATION`. They are not swallowed by `ÖVRIGT INNEHÅLL`. The expanded audit shows a separate `ÖVRIGT INNEHÅLL` section containing the `Ovrigt` branch and a separate `VISIBLE ON START PAGE` helper section.

Telemetry confirms:

```text
MUNICIPAL_ROOT_POLICY_SUMMARY beforeRootCount=8 afterRootCount=7 acceptedCount=7 demotedCount=1
```

Warnings retained: two deep root children, one synthetic parent, and missing homepage-card section extraction. Visual topology remains correct.

# Hässleholm

Run:

`D:\WebSnapshots-main\diagnostics_regression_after_eslov_fix\sitevision_hassleholm\2026-06-01_133342`

Visual root NAVIGATION contains nine authored roots:

1. Utbildning och barnomsorg
2. Omsorg och stöd
3. Bygga, bo och miljö
4. Uppleva och göra
5. Trafik och gator
6. Kommun och politik
7. Näringsliv och företag
8. Anslagstavla
9. Jobb och arbetsmarknad

`Anslagstavla` remains visible as an authored NAVIGATION root. There is no blanket Anslagstavla demotion.

Telemetry confirms:

```text
VISIBLE_ROOT_CLASSIFICATION_APPLIED rootCandidatesAccepted=9 acceptedPrimaryRootOverlap=9 visibleRootChildrenAfter=9
MUNICIPAL_ROOT_POLICY_SUMMARY beforeRootCount=9 afterRootCount=9 acceptedCount=9 demotedCount=0
```

Warning retained: five deep root children. The screenshots show sane municipal roots and a separate start-page helper section.

# Telemetry Findings

| Municipality | Flat Pages | Tree Nodes | Orphans | Root Policy After | Accepted Primary Root Overlap | Topology Verdict |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Eslöv | 92 | 91 | 0 | 7 | 7 | PASS |
| Kristianstad | 168 | 167 | 0 | 7 | Not emitted in this path | PASS_WITH_MINOR_ISSUES |
| Ystad | 172 | 166 | 0 | 7 | 7 | PASS_WITH_MINOR_ISSUES |
| Klippan | 175 | 173 | 0 | 7 | Not emitted in this path | PASS_WITH_MINOR_ISSUES |
| Hässleholm | 208 | 199 | 0 | 9 | 9 | PASS_WITH_MINOR_ISSUES |

Cross-fixture checks:

- Every diagnostic detected the expected CMS with high confidence.
- Every fixture has zero orphan pages.
- Every fixture has zero failed visits, timeouts, snapshot failures, and text extraction failures.
- No fixture collapsed to a single root.
- No rendered root list shows excessive root pollution.
- Helper, tool, start-page, and miscellaneous sections remain separated where generated.

# Visual Findings

Expanded audit artifacts were generated beneath each run folder using the `_expanded_visual_audit` suffix.

Each municipality has:

- `rendered-root-navigation.txt`
- `expanded-visible-nav-text.txt`
- `scroll-manifest.json`
- Ten expanded navigation screenshots

Representative screenshots reviewed:

| Municipality | Screenshots Reviewed |
| --- | --- |
| Eslöv | `nav-00-y0_main-00-y0.png`, `nav-09-y2175_main-00-y0.png` |
| Kristianstad | `nav-00-y0_main-00-y0.png`, `nav-09-y6698_main-00-y0.png` |
| Ystad | `nav-00-y0_main-00-y0.png`, `nav-09-y4695_main-00-y0.png` |
| Klippan | `nav-00-y0_main-00-y0.png`, `nav-09-y5660_main-00-y0.png` |
| Hässleholm | `nav-00-y0_main-00-y0.png`, `nav-03-y4938_main-00-y0.png`, `nav-06-y9876_main-00-y0.png`, `nav-09-y14808_main-00-y0.png` |

Visual evidence agrees with rendered text and telemetry for every fixture. No screenshot contradicts a passing fixture assertion.

# Regressions Introduced By Eslöv Fix

None proven.

The queue-ordering and root-protection change preserves SiteVision fixture behavior while correcting the WordPress/Municipio Eslöv case.

# Release Readiness

Ready for release with known bounded-diagnostic warnings.

The warnings are not release blockers for the Eslöv fix:

- Kristianstad retains a visible `ÖVRIGT INNEHÅLL` cluster, but Badrike is correctly excluded from root NAVIGATION.
- Ystad retains two deep-root warnings, but its forbidden roots remain excluded.
- Klippan retains missing-homepage-section and synthetic-parent warnings, but the visual root topology is correct.
- Hässleholm retains five deep-root warnings, but authored `Anslagstavla` remains correctly visible.

# Recommendation

Release the Eslöv fix without an additional code patch. Track the existing SiteVision bounded-diagnostic warnings separately from this change.
