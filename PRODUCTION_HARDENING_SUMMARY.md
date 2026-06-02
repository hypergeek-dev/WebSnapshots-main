# Production Hardening Summary

## Scope

This change implements conservative production hardening around archive metadata, incremental manifests, regression fixture documentation, and non-destructive output reporting.

Navigation semantics were not changed. `MunicipalRootClassifier`, crawl topology, root policy, and parallel scraping behavior were not modified.

## Files Changed

- `ProductionRunner.cs`
- `SiteRunArtifacts.cs`
- `RunManifestStore.cs`
- `OutputDoctorReporter.cs`
- `RunManifest.cs`
- `AtomicWrite.cs`
- `Program.cs`
- `AppRunner.cs`
- `GlobalIndexBuilder.cs`
- `docs/REGRESSION_FIXTURES.md`
- `README.md`
- `PRODUCTION_HARDENING_SUMMARY.md`

## Artifact Contract

Every created site scrape folder now receives these files immediately:

- `scrape.json`
- `index.htm`
- `index.html`

The initial status is `PARTIAL`, with a note that the scrape is still in progress and incomplete until it reaches `OK`.

Terminal site statuses are:

- `OK`
- `ERROR`
- `CANCELLED`
- `CAP_REACHED`
- `PARTIAL`

`scrape.json` records:

- municipality
- host
- start URL
- status
- reason
- note
- pages done
- screenshots done
- started time
- finished time when available
- entry relative path
- viewer relative path only when a viewer exists

The entry page clearly labels incomplete archives and shows the reason and note. It does not offer a viewer link when no viewer exists.

## Failed And Partial Runs

Errors, cancellation, and storage-cap stops preserve the site folder and update its status honestly.

Where `nav.json` exists, a best-effort partial viewer rebuild is attempted. Failure to build a partial viewer is logged without hiding the site status.

The run-level status is:

- `OK` when every planned site completed successfully
- `PARTIAL` when one or more sites ended incomplete
- `CANCELLED` when operator cancellation stops the run

## Incremental Manifest Writes

`run.json` is now created at run startup with:

- run ID
- output root
- start time
- `PARTIAL` status
- planned site count
- effective production settings

Each site is inserted into `run.json` as `PARTIAL` immediately after its folder is created. The manifest is updated again when the site reaches its terminal status.

Manifest and site-artifact writes use `AtomicWrite`. On Windows, the writer prefers `File.Replace` and falls back to same-directory overwrite move when a filesystem or transient handle rejects replacement.

## Regression Fixtures

`docs/REGRESSION_FIXTURES.md` documents the five validated fixtures:

- Eslov
- Kristianstad
- Ystad
- Klippan
- Hassleholm

It records expected root behavior, forbidden root behavior, the authored `Anslagstavla` exception, and the rule that visual evidence is authoritative.

## README

The existing short validation section remains in place. The README now also documents:

```powershell
WebSnapshots.exe doctor output
```

## Doctor Command

Implemented a non-destructive report command:

```powershell
WebSnapshots.exe doctor output
```

It reports:

- output size
- scrape folders missing `scrape.json`
- scrape folders missing `viewer.htm`
- empty scrape folders
- `.tmp` leftovers
- `_runs` folders missing `run.json`
- broken local links from `index.htm` and `index.html`

It does not delete or modify files.

## Validation

Release build:

```powershell
dotnet build WebSnapshots.sln -c Release
```

Result: succeeded with zero warnings and zero errors.

Bounded success smoke:

```powershell
dotnet run --project .\WebSnapshots.csproj -c Release --no-build -- `
  --sites .tmp_smoke_sites.txt `
  --output .tmp_smoke_output `
  --landing-only `
  --max-depth 0 `
  --max-pages 1
```

Result:

- one `example.com` page captured
- site status `OK`
- run status `OK`
- `scrape.json`, `index.htm`, `index.html`, and `viewer.htm` present
- `pagesDone: 1`
- `screenshotsDone: 1`

Bounded incomplete smoke:

```powershell
dotnet run --project .\WebSnapshots.csproj -c Release --no-build -- `
  --sites .tmp_smoke_sites.txt `
  --output .tmp_cap_output2 `
  --max-depth 0 `
  --max-pages 1 `
  --cap-bytes 1
```

Result:

- site status `CAP_REACHED`
- run status `PARTIAL`
- explicit cap reason and incomplete note in JSON and entry HTML
- no misleading viewer link

Doctor verification:

- success smoke: zero findings
- capped smoke: one expected finding for missing `viewer.htm`

Bounded invalid-input smoke:

```powershell
dotnet run --project .\WebSnapshots.csproj -c Release --no-build -- `
  --sites .tmp_invalid_sites.txt `
  --output .tmp_invalid_output `
  --max-depth 0 `
  --max-pages 1
```

Result:

- malformed URL produced site status `ERROR`
- run status `PARTIAL`
- `scrape.json`, `index.htm`, and `index.html` were written
- URI parsing failure was recorded as the reason
- entry HTML clearly marked the archive incomplete

Temporary smoke files and folders were removed after validation.

## Remaining Risks

- The legacy orchestration helpers remain temporarily in `Program.cs` and `AppRunner.cs`, but both active entry points now delegate to `ProductionRunner`.
- Storage-governor incremental accounting is still a future change.
- Hard process termination can only preserve the last completed atomic write; it cannot run finalizers.
- Automated fixture tests and checked-in sanitized `nav.json` golden artifacts are still needed.
- Crawler browser recovery remains less comprehensive than snapshot browser recovery.
