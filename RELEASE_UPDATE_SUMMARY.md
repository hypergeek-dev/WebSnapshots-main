# WebSnapshots v1.0.1 Release Update Summary

## Files Changed

Code fix already committed:

- `NavCrawler.cs`

Release documentation:

- `README.md`
- `docs/REGRESSION_FIXTURES.md`
- `REGRESSION_VALIDATION_AFTER_ESLOV_FIX.md`
- `RELEASE_NOTES_v1.0.1.md`
- `RELEASE_UPDATE_SUMMARY.md`

## Commit Recommendation

Keep the release history as two focused commits:

1. `92e695d Preserve accepted homepage roots during nav seeding`
2. A release-documentation commit containing the fixture guide, validation report, README update, release notes, and this summary.

Do not commit generated diagnostics, screenshots, scrapes, publish output, or unrelated local files.

## Release Recommendation

Create the GitHub release update as:

- Tag: `v1.0.1`
- Title: `WebSnapshots v1.0.1`

Summary:

- Fixes WordPress / Municipio root preservation issue.
- Validated against five-municipality regression suite.
- No regressions detected.
- Improved navigation topology reliability.

## Final Release Readiness Assessment

APPROVED

The Eslöv root-preservation change passed bounded diagnostic and visual regression validation across Eslöv, Kristianstad, Ystad, Klippan, and Hässleholm. No regression was introduced. Navigation topology is release-frozen for v1.0.1 unless a new regression is proven.
