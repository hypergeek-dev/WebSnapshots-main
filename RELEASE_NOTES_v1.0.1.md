# WebSnapshots v1.0.1

## Summary

WebSnapshots v1.0.1 improves navigation topology reliability for municipal archive viewers.

## Changes

- Fixed the WordPress / Municipio homepage-root preservation issue observed on Eslöv.
- Ensured accepted homepage structural anchors are visited before breadth-limited primary-navigation sampling.
- Completed regression validation across Eslöv, Kristianstad, Ystad, Klippan, and Hässleholm.
- Established a documented navigation validation suite for future releases.

## Validation

The five-municipality regression suite passed without a regression introduced by the Eslöv fix.

- Eslöv retains its seven expected municipal NAVIGATION roots.
- Kristianstads badrike remains excluded from municipal root NAVIGATION.
- Ystad Gymnasium and Ystads Industrifastigheter remain excluded from municipal root NAVIGATION.
- Klippan retains its structural roots under NAVIGATION rather than `ÖVRIGT INNEHÅLL`.
- Hässleholm retains authored `Anslagstavla` navigation without blanket demotion.

## GitHub Release

- Tag: `v1.0.1`
- Title: `WebSnapshots v1.0.1`

Suggested release summary:

- Fixes WordPress / Municipio root preservation issue.
- Validated against five-municipality regression suite.
- No regressions detected.
- Improved navigation topology reliability.
