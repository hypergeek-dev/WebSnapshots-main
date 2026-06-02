# Regression Fixtures

WebSnapshots uses five validated municipal fixtures to protect root navigation topology. Visual evidence is authoritative when telemetry, generated data, and the rendered viewer disagree.

Future navigation changes must validate against all five fixtures before release.

## Eslöv

- CMS: WordPress / Municipio
- Regression focus: homepage-root preservation
- Expected NAVIGATION roots:
  - Förskola, skola och utbildning
  - Omsorg och stöd
  - Uppleva och göra
  - Bygga, bo och miljö
  - Trafik, gator och parker
  - Arbete och arbetsmarknad
  - Kommun och politik

## Kristianstad

- CMS: SiteVision
- `Kristianstads badrike` must not appear as a root under NAVIGATION.
- Seven municipal roots should remain visible.

## Ystad

- CMS: SiteVision
- `Ystad Gymnasium` must not appear as a root under NAVIGATION.
- `Ystads Industrifastigheter` must not appear as a root under NAVIGATION.
- Seven municipal roots should remain visible.

## Klippan

- CMS: SiteVision
- Real municipal roots must remain under NAVIGATION.
- Municipal roots must not be swallowed by ÖVRIGT.

## Hässleholm

- CMS: SiteVision
- `Anslagstavla` is allowed as a root when it is authored as primary information architecture.
- Do not introduce a blanket `Anslagstavla` demotion.

## Validation Rule

Fixture expectations protect rendered root navigation topology. Review the generated viewer, expanded visual audit screenshots, `rendered-root-navigation.txt`, and `expanded-visible-nav-text.txt`. Treat visual evidence as authoritative.
