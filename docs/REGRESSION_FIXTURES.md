# Regression Fixtures

WebSnapshots uses a five-municipality regression fixture set to validate navigation topology preservation, root classification, and viewer rendering.

## Eslöv

- CMS: WordPress / Municipio
- Regression focus: homepage root preservation
- Expected behavior: accepted homepage structural anchors remain visible as municipal NAVIGATION roots.

## Kristianstad

- CMS: SiteVision
- Regression focus: Badrike exclusion
- Expected behavior: `Kristianstads badrike` does not appear as a municipal NAVIGATION root.

## Ystad

- CMS: SiteVision
- Regression focus: Gymnasium / Industrifastigheter exclusion
- Expected behavior: `Ystad Gymnasium` and `Ystads Industrifastigheter` do not appear as municipal NAVIGATION roots.

## Klippan

- CMS: SiteVision
- Regression focus: ÖVRIGT protection
- Expected behavior: real municipal roots remain under NAVIGATION and are not swallowed by `ÖVRIGT INNEHÅLL`.

## Hässleholm

- CMS: SiteVision
- Regression focus: Anslagstavla validation
- Expected behavior: `Anslagstavla` remains visible when it is genuinely authored as part of the municipality's primary information architecture.

## Release Validation

Before releasing navigation changes:

1. Run a bounded diagnostic for every fixture.
2. Generate navigation artifacts and an expanded viewer audit.
3. Review `rendered-root-navigation.txt`, `expanded-visible-nav-text.txt`, and expanded screenshots.
4. Treat visual evidence as authoritative when telemetry and viewer output disagree.
5. Keep the navigation system release-frozen unless a regression is proven.
