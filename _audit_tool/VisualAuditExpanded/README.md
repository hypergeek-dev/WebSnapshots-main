# Expanded Viewer Audit

Reusable visual QA capture for WebSnapshots archive viewers.

The tool opens each `viewer.htm`, expands the archive navigation and helper sections, then captures a scroll matrix:

- multiple left-navigation scroll positions
- multiple main iframe scroll positions
- every screenshot filename includes both offsets

Run from the repository root:

```powershell
.\Capture-ExpandedViewerAudit.ps1
```

Common focused run:

```powershell
.\Capture-ExpandedViewerAudit.ps1 `
  -Output "D:\WebSnapshots-main\output_precaution_run" `
  -Audit "D:\WebSnapshots-main\output_precaution_run\_visual_quality_audit\scroll-matrix" `
  -Municipalities "Eslov,Kristianstad,Ystad,Klippan,Hassleholm" `
  -MaxNavSlices 10 `
  -MaxMainSlices 6
```

Outputs per municipality:

- `rendered-root-navigation.txt`
- `expanded-visible-nav-text.txt`
- `scroll-manifest.json`
- `nav-XX-y*_main-XX-y*.png`

The default output directory is:

```text
D:\WebSnapshots-main\output_precaution_run\_visual_quality_audit\scroll-matrix
```
