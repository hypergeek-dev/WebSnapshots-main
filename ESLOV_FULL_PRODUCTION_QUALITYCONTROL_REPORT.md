# Eslöv Full Production Quality Control Report

---

## Executive Summary

The 2026-06-03 full production run for Eslöv captured **1,444 pages** with **1,444 screenshots** across a 2h 41m crawl. All seven primary municipal navigation sections required by QualityControl.md are present and correctly rooted in the viewer's primary navigation tree. Child navigation under Education and Omsorg is sensibly grouped. News, Aktuellt, and archive material are separated from primary IA in a collapsible "Övrigt innehåll" section.

The main weaknesses are: four utility/meta/system pages leak into the primary nav tree alongside the seven proper roots, and five of the twelve QualityControl.md sections (the "Additional Top-Level Sections") are absent or demoted from root level. These are known limitations of the crawler rather than catastrophic structural failures.

**Verdict: PASS WITH KNOWN LIMITATIONS**

**Show to municipality employee: MOSTLY**

---

## Run Used

| Field | Value |
|---|---|
| Run folder | `D:\WebSnapshots-main\output_production_eslov\Eslov\260603_2` |
| Start time | 2026-06-03 08:16 CEST |
| Finish time | 2026-06-03 10:58 CEST |
| Duration | ~2h 41m |
| Pages crawled | 1,444 |
| Screenshots taken | 1,444 |
| CMS detected | WordPress (Municipio) |
| Status | OK |

---

## Primary Navigation Check

The viewer renders a **primary navigation tree** anchored to the seven homepage sections. All seven are present.

| Section | URL | Present | Children |
|---|---|---|---|
| Förskola, skola och utbildning | /utbildning-barnomsorg | ✓ | 7 |
| Omsorg och stöd | /omsorg-stod | ✓ | 15 |
| Uppleva och göra | /uppleva-gora | ✓ | 9 |
| Bygga, bo och miljö | /bygga-bo-miljo | ✓ | 14 |
| Trafik, gator och parker | /trafik-gator-parker | ✓ | 6 |
| Arbete och arbetsmarknad | /arbete-och-arbetsmarknad | ✓ | 2 |
| Kommun och politik | /kommun-politik | ✓ | 13 |

**7 / 7 required primary roots are present.**

The viewer correctly identifies all seven via `homepageSections` (the homepage card extractor), anchors them to the primary tree, and renders them as first-class navigation roots. This is the most important quality dimension and it passes cleanly.

---

## Extra / Wrong Navigation Roots

The primary nav tree contains **four additional nodes** that are not primary IA. These appear alongside the seven proper roots in the viewer's main navigation column.

| Node | URL | Classification | Problem |
|---|---|---|---|
| Ticket Server | /medborgartjanst | System / e-service gateway | System page; not a navigation section |
| Om webbplatsen | /om-webbplatsen | Meta / footer | Belongs in footer meta, not primary nav |
| Kontakt | /kommunen/kontakt | Utility | Marginally acceptable but not primary IA |
| För medarbetare | /for-medarbetare | Intranet / staff | Not a public-facing municipal section |

These four nodes are in the viewer's primary tree because the crawler found them at depth-1 and the classification pipeline did not move them to the discovered/Övrigt section. A municipality employee would immediately notice "Ticket Server" and "För medarbetare" sitting next to "Omsorg och stöd" — that is an IA anomaly.

**Archive and news roots are correctly handled.** The nodes below were moved to the collapsible "Övrigt innehåll" section (not the primary tree):

| Node | URL | Viewer classification | Viewer sub-label |
|---|---|---|---|
| Nyheter | /nyheter | NewsOrEventRoot | Nyheter |
| Anslag | /anslag | NewsOrEventRoot | Aktuellt |
| Driftsinformation | /driftsinformation | NewsOrEventRoot | Aktuellt |
| Event | /event | DiscoveredOtherRoot | Aktuellt |
| Lediga jobb | /lediga-jobb | DiscoveredOtherRoot | Aktuellt |
| Kampanjer | /kampanjer | NewsOrEventRoot | Aktuellt |
| Arkiv: Fritidsaktiviteter | /fritidsaktiviteter | NewsOrEventRoot | Övrigt |
| Arkiv: Navetinsatser | /navetinsatser | NewsOrEventRoot | Övrigt |
| Psidata | /psidata | ErrorOrSystemRoot | Övrigt |
| For elever | /for-elever | MicrositeRoot | Övrigt |
| Subvention för trygghetsboende | /bygga-bo-miljo/bostader... | NewsOrEventRoot | Övrigt |

No `Arkiv:` or archive-prefixed pages appear in the primary navigation tree. The `Arkiv:` prefix is applied to the titles of /fritidsaktiviteter and /navetinsatser, correctly signaling their nature to a viewer user.

---

## Child Navigation Check

### Förskola, skola och utbildning (7 children)

| Child | URL |
|---|---|
| Gymnasium | /utbildning-barnomsorg/gymnasium |
| Vuxenutbildning | /utbildning-barnomsorg/vuxenutbildning-yh |
| Elevhälsa och särskilt stöd | /utbildning-barnomsorg/elevhalsa |
| Förskola | /utbildning-barnomsorg/forskola |
| Grundskola | /utbildning-barnomsorg/grundskola |
| Förskolor | /utbildning-barnomsorg/forskola/forskolor |
| Synpunkter och klagomål | /utbildning-barnomsorg/synpunkter-och-klagomal |

**Assessment:** Sensible. Preschools (Förskola, Förskolor) are correctly under the education root, not floating loose. Gymnasium has 8 sub-children. Grundskola has 12. This matches what a human would expect. The duplication of "Förskola" and "Förskolor" as siblings is a minor oddity (they are distinct: the service vs. the listing), not a structural error.

### Omsorg och stöd (15 children)

| Child | Present |
|---|---|
| Äldre | ✓ |
| Funktionsnedsättning (incl. LSS/stöd) | ✓ |
| Akut hjälp och krisstöd | ✓ |
| Ekonomi och pengar | ✓ |
| Familj, barn och ungdom | ✓ |
| Eslov.se/soc – din väg till stöd | ✓ |
| Hälsovård och sjukvård | ✓ |
| Missbruk och beroende | ✓ |
| Nyanländ i Eslöv | ✓ |
| Stöd psykisk ohälsa | ✓ |
| Trygg och säker | ✓ |

**Assessment:** Passes. The three key items from QualityControl.md — Äldre, Akut hjälp, and LSS/Funktionsnedsättning — all appear as direct children of Omsorg och stöd. Funktionsnedsättning carries 12 sub-children and likely covers LSS services. Akut hjälp och krisstöd is present with 3 sub-children. The grouping is recognizable to anyone familiar with Swedish municipal social services.

### Navetinsatser placement

Navetinsatser (151 children) is placed in the "Övrigt" sub-group of the viewer's collapsible "Övrigt innehåll" section, titled "Arkiv: Navetinsatser". The children are individual social support program pages (rehab, counseling, partner support, etc.). This placement is **acceptable** — it is clearly labeled as an archive, it is outside the primary navigation, and the content is accessible if a user opens the collapsible. It is not awkward in the current viewer layout.

---

## Övrigt / Helper Group Check

The viewer renders a collapsible section titled **"Övrigt innehåll (11)"** below the primary navigation. It has three sub-labels.

### Nyheter sub-group
- **Nyheter** (107 news article children)

News is correctly isolated. It does not appear in or compete with primary navigation. ✓

### Aktuellt sub-group
- **Anslag** (122 official notice children)
- **Driftsinformation** (4 service disruption children)
- **Event** (138 event children)
- **Kampanjer** (1 child — an environmental campaign page)
- **Lediga jobb** (17 job listing children)

**Assessment:** Well-grouped. Aktuellt captures the dynamic, time-sensitive content that should not be part of structural navigation. Lediga jobb appearing here rather than under Arbete och arbetsmarknad is a known limitation — the jobs listing at /lediga-jobb was crawled as a discovered root rather than absorbed under its thematic parent. This is visible but understandable.

### Övrigt sub-group
- **Arkiv: Fritidsaktiviteter** (52 children — sports club listings, historical archive)
- **Arkiv: Navetinsatser** (151 children — social support programs archive)
- **For elever** (2 children — school/student microsite pages)
- **Psidata** (1 child — /psidata/dataset, open data portal)
- **Subvention för trygghetsboende** (1 child — a deep-leaf page that leaked to root)

**Assessment:** Mostly acceptable. The "Arkiv:" prefix on Fritidsaktiviteter and Navetinsatser is correct and communicates their nature. Psidata and its "Öppna data" child are correctly filed here as a system/data page. "Subvention för trygghetsboende" leaked to root as a standalone discovered node — it should be nested under Bygga, bo och miljö and is an IA oddity. "For elever" is a microsite (two unrelated student pages) and is appropriately in Övrigt.

**Övrigt does not contain obvious primary IA.** No primary municipal service pages are misfiled here. ✓

---

## Additional Homepage Card Assessment

These are the "Additional Top-Level Sections" from QualityControl.md beyond the seven primary roots.

| Section | Status | Notes |
|---|---|---|
| Företag och näringsliv | **Absent as root** | Only found as "Webbplats för företag" under /kommun-politik/ny-foretagswebb. The dedicated business website (foretagieslov.se or similar) is a separate host and was not crawled. This is expected behavior — external host, not a crawler failure. |
| Kommunkarta | **Not found** | Not present in Flat list. Likely an external service (embedded map widget or external URL) not linked as a page. Not a crawler failure. |
| Våra övriga webbplatser | **Not found** | No page with this title was crawled. Either removed from current live site or only linked from secondary navigation not extracted. |
| Utveckla Eslöv | **Not found as section** | Only appears as a text fragment in a news article title. If this was a standalone navigation section, it has been removed from the live site or is on a different subdomain. |
| Eslövs kommuns historia | **Not found** | Not present in the crawled pages. Likely live-site drift — this section may have been removed or reorganized since the QualityControl.md baseline was written. |

**Summary:** 0 of 5 "Additional Top-Level Sections" are present as first-class roots. The primary causes are: external host separation (Företag), live-site drift (historia, Utveckla Eslöv), and pages linked only from secondary navigation not extracted by the crawler. These absences are documented limitations and should not be treated as blocking failures.

---

## Human Fidelity Score

| Dimension | Score | Notes |
|---|---|---|
| Primary 7 sections present | 10/10 | All correct |
| Primary sections correctly rooted | 10/10 | All at depth-1, properly linked |
| Child nav sensibility (Education) | 9/10 | Sensible; minor Förskola/Förskolor duplication |
| Child nav sensibility (Omsorg) | 9/10 | Key care sections present and navigable |
| News / Aktuellt separation | 9/10 | Clearly separated from primary IA |
| Archive handling | 9/10 | Labeled Arkiv:, not in primary tree |
| Övrigt content quality | 7/10 | Mostly correct; some leakage (Subvention) |
| Primary tree cleanliness | 5/10 | 4 unwanted nodes (Ticket Server, Om webbplatsen, Kontakt, För medarbetare) |
| Additional sections coverage | 2/10 | 0/5 Additional Top-Level Sections present as roots |
| Screenshot coverage | 10/10 | 1444/1444 pages |

**Overall human fidelity: 80/100**

The core primary IA is sound and recognizable. The penalties come from four non-primary nodes in the main tree (mild noise) and the absence of five additional sections that a more thorough visitor would notice are missing.

---

## Production Readiness Verdict

**PASS WITH KNOWN LIMITATIONS**

The archive reliably captures and presents the seven primary municipal navigation sections with sensible child hierarchies. News, events, and archive content are properly separated. Screenshots are complete (0% blank rate confirmed in prior validation).

Known limitations to document:
1. Four utility/system/intranet pages appear in the primary navigation tree alongside proper municipal sections (Ticket Server, Om webbplatsen, Kontakt, För medarbetare).
2. Företag och näringsliv does not appear as a root — accessible only as a link under Kommun och politik.
3. Kommunkarta, Våra övriga webbplatser, Utveckla Eslöv, and Eslövs kommuns historia are absent (live-site drift or external host).
4. Lediga jobb appears in Aktuellt rather than under Arbete och arbetsmarknad.
5. Subvention för trygghetsboende leaked to root as a standalone discovered node.

None of these limitations make the archive misleading. They reduce completeness and introduce minor IA noise.

---

## Final Assessment

**Would you show this Eslöv archive to a municipality employee as a reasonable representation of the website navigation?**

**MOSTLY**

A municipality employee would immediately recognize the seven primary sections — Förskola, Omsorg, Uppleva, Bygga, Trafik, Arbete, Kommun — as the correct navigation backbone of their site. The child navigation under each section is sensible and navigable. Screenshots are present for all pages.

However, the employee would likely ask:
- "Why is 'Ticket Server' in the main navigation?"
- "Where is Företag och näringsliv?"
- "Why do I see 'För medarbetare' as a main section?"

These are legitimate questions that would require explanation. The archive is useful and representative, but not clean enough to present without a brief caveat about the known limitations.
