# Confidence and Telemetry Architecture

WebSnapshots uses deterministic evidence scoring for structural navigation decisions. The goal is not to make the crawler smarter at runtime with AI, but to make important crawler choices inspectable, repeatable, and tunable.

## Decision Evidence Model

Structural decisions are represented with a lightweight evidence shape:

- `decisionType`
- `candidateUrl`
- `candidateTitle`
- `accepted`
- `confidence`
- `threshold`
- `positiveEvidence`
- `negativeEvidence`
- `finalReason`

The same fields are emitted to diagnostic logs and, when diagnostic telemetry is enabled, to `telemetry.jsonl`.

## Deterministic Confidence Scoring

Each decision area assigns fixed weights to positive and negative evidence. The final confidence is clamped to `0.0..1.0` and compared with a fixed threshold. There is no AI, LLM, embedding lookup, or site-specific rule in the scoring path.

Current thresholds:

- Homepage structural anchor: `0.68`
- Synthetic URL-prefix parent: `0.72`
- URL-parent inference: `0.60`

Homepage anchors get positive evidence for same-host internal URLs, main-content placement, card/tile/grid presentation, short section-like paths, visible titles, sibling card patterns, non-utility placement, non-article paths, non-binary URLs, and visible link geometry. Deep homepage promo/news/action cards are capped below the accept threshold so they can be explained and rejected without hardcoded municipal exceptions.

Synthetic parents require strong URL-topology evidence: shared missing prefix, stable non-article prefix, no binary/admin/search/login path, no cycle, reasonable depth, and root-pollution reduction. Single-child prefixes and unstable slugs are rejected.

URL-parent inference scores known ancestor relationships, descendant path evidence, non-root specificity, structural-section provenance, stable parent URLs, cycle avoidance, and child path depth.

## Telemetry Events

Homepage structural anchors:

- `HOMEPAGE_ANCHOR_CANDIDATE`
- `HOMEPAGE_ANCHOR_ACCEPTED`
- `HOMEPAGE_ANCHOR_REJECTED`
- `HOMEPAGE_ANCHOR_GROUP_SUMMARY`

Synthetic parents:

- `SYNTHETIC_PARENT_CANDIDATE`
- `SYNTHETIC_PARENT_CREATED`
- `SYNTHETIC_PARENT_REJECTED`

URL-parent inference:

- `URL_PARENT_CANDIDATE`
- `URL_PARENT_INFERRED`
- `URL_PARENT_REJECTED`
- `URL_PARENT_LOW_CONFIDENCE`

Display titles:

- `DISPLAY_TITLE_RESOLVED`
- `DISPLAY_TITLE_FALLBACK_USED`

Root topology diagnostics are exposed in quality metrics:

- `rootChildCount`
- `deepRootChildCount`
- `structuralRootChildren`
- `utilityRootChildren`
- `rawUrlTitleRootChildren`
- `syntheticRootChildren`
- `rootTopologyVerdict`

Warnings include `root_child_explosion`, `root_leaf_pollution`, `raw_url_labels_at_root`, and `utility_root_pollution`.

## Future Tuning

Tune by changing weights, caps, and thresholds in small steps, then rerun the same diagnostic targets. Prefer adding generic evidence labels over adding municipality-specific rules. A good tuning change should improve the relevant decision telemetry while preserving:

- no duplicate URLs
- no non-root `depth=0`
- no cycles
- SiteVision numeric-ID parent relationships
- credible root children for WordPress/Municipio sites

When a decision looks wrong, inspect its candidate event first. The positive and negative evidence should explain why it crossed or missed the threshold.
