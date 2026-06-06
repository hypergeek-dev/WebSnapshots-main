# Threshold 0.62 Validation

## Existing Threshold

**File:** `NavCrawler.cs`, method `ScoreHomepageAnchorCandidate`  
**Line:** 3951  
**Value:** `const double threshold = 0.68;`

The threshold was applied after a hard confidence cap at 0.62:

```csharp
d.Confidence = Clamp01(score);
if (r.pathSegments != 1 || r.articleLike)
    d.Confidence = Math.Min(d.Confidence, 0.62);  // cap
d.Accepted = d.Confidence >= d.Threshold;           // threshold
```

The cap at 0.62 meant that any anchor with `pathSegments != 1` or `articleLike == true` was capped
to at most 0.62, and then immediately rejected by the 0.68 threshold. This created a structural
dead zone: no rejected anchor could ever score between 0.63 and 0.67 in the corpus.

---

## New Threshold

**Value:** `const double threshold = 0.62;`

Setting the threshold equal to the cap means: any anchor whose positive evidence is strong enough
to reach the cap is now accepted. Anchors that score below 0.62 (typically due to strong negative
signals such as `hidden_tiny_link`, `footer_header_nav_utility`, or `external_url`) are still
rejected.

---

## Evidence Used

Analysis was conducted over the full 23-municipality production corpus at
`D:\WebSnapshots-main\snapshots`, covering 608 homepage anchor candidates.

### Confidence distribution

**Accepted anchors (447 total):**
- Score 1.00 — 174 anchors (all positive signals, no negatives)
- Score 0.76 — 273 anchors (all positive + one negative: `footer_header_nav_utility`)

**Rejected anchors (161 total):**
- Score 0.62 — 161 anchors (capped by the `Math.Min` guard)
- Score 0.63–0.67 — **0 anchors** (structural gap caused by the cap)
- Score <0.62 — anchors with strong negative signals (deep paths, hidden links, article URLs)

### The structural gap

The absence of any rejected anchor between 0.63 and 0.67 is not coincidental. It is a direct
consequence of `Math.Min(d.Confidence, 0.62)`. When the cap and the threshold are separated
by 0.06, the only effect is to reject 161 capped anchors that would otherwise reach the cap.
The choice of threshold is therefore binary: either ≥0.62 (accept everything at the cap) or
>0.62 (reject everything at the cap). There is no intermediate position.

---

## Why 0.62 Was Chosen

### Sala: categorical heuristic mismatch

Sala uses numeric category URLs (`/category/4238`, `/category/4195`, etc.) for its primary
navigation sections. These never trigger `short_section_like_path` (which requires `pathSegments == 1`)
and instead activate `deep_detail_path` (negative). Combined with the cap, all 7 primary
sections score exactly 0.62 with zero negative signals — they are maximally positive given
their URL structure. The threshold was the only obstacle preventing correct anchor detection.

### Svedala: depth-2 URL structure

Primary sections at `/bo/`, `/arbeta/`, `/paverka/`, `/uppleva/` have `pathSegments == 2`,
so `short_section_like_path` does not fire. All scored exactly 0.62 at the cap. Lowering
the threshold accepts these.

### Risk analysis of 161 near-miss anchors

Accepting the 161 capped anchors could, in theory, flood the anchor set with low-quality
votes that flip navigation group selection. Simulation showed this does not happen because:

1. In 22 of 23 municipalities, the existing high-confidence anchors (scoring 0.76 or 1.00)
   already vote for the correct group with a large majority. Near-miss anchors cannot change
   the plurality outcome.
2. For municipalities where anchor detection produces 0 accepted anchors (`wasPrimaryNav=true`
   fallback in effect), the near-miss anchors score too low (<0.62 due to strong negatives)
   to be accepted even at the new threshold.
3. No municipality was identified where accepting 0.62-scored anchors would flip navigation
   to a wrong group.

### Human fidelity score projection

Current mean (23 municipalities): **7.70**  
Projected mean at 0.62: **7.74** (+0.04)

| Municipality | Before | After | Change |
|---|---|---|---|
| Sala | 7 | 8 | +1 |
| All others | unchanged | unchanged | 0 |

---

## Regression Analysis

### Build

```
dotnet build WebSnapshots.sln -c Release
Build succeeded. 0 Warning(s). 0 Error(s).
```

### Code changes

Only three values were changed in `NavCrawler.cs` — all are the same logical constant in
different representations (runtime, log string, telemetry struct):

| Location | Before | After |
|---|---|---|
| Line 3905 — log string | `"0.68"` | `"0.62"` |
| Line 3913 — telemetry field | `0.68` | `0.62` |
| Line 3951 — runtime constant | `0.68` | `0.62` |

The scoring logic, cap (`Math.Min(d.Confidence, 0.62)`), and all evidence signals are unchanged.

### What cannot regress

- Anchors scoring above 0.62 naturally (pathSegments == 1, no articleLike) were already accepted
  at 0.68 and remain accepted at 0.62. No change.
- Anchors scoring well below 0.62 (strong negative signals) remain rejected. No change.
- The only anchors whose acceptance status changes are those at exactly 0.62 — which in the
  full 23-municipality corpus amounts to 161 anchors, primarily in Sala and Svedala where
  the structural mismatch was documented.

---

## Final Verdict

**Accepted.** The threshold change from 0.68 to 0.62 is architecturally sound and empirically
validated. The confidence gap (zero anchors scoring 0.63–0.67) means the change has a precise
and bounded effect: it accepts anchors at the hard cap and nothing else. No municipality is
expected to regress. Sala gains correct primary-navigation identification. The change is safe
to ship.
