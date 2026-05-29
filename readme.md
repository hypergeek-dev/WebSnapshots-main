# WebSnapshots

WebSnapshots is a Windows desktop and command-line tool for creating static visual archives of websites.

It crawls same-host links, renders pages with Playwright Chromium, captures full-page WebP screenshots, and writes a local HTML archive that can be browsed without a server or database.

The output is a visual record of rendered pages, not an HTML mirror.

## Features

- WinForms GUI for interactive archive runs
- CLI mode for repeatable batch runs
- Same-host crawling with depth and page-count limits
- Full-page screenshot capture with tiling fallback for long pages
- Cookie/banner handling for common Swedish and English consent dialogs
- CMS-aware navigation grouping for cleaner archive viewers
- Static per-site viewer and global run index
- Optional quick-preview and landing-only modes
- Local static-file server for browsing generated archives

While WebSnapshots can archive any website, the navigation reconstruction and validation logic has primarily been developed and tested against Swedish municipal websites. Results on other website types may vary.

## Running the GUI

For the bundled Windows release:

```powershell
WebSnapshots.bat
```

For development from source:

```powershell
dotnet run --project .\WebSnapshots.csproj
```

In the GUI, paste one or more start URLs, choose output/crawl options, and start the run. Results are written under the selected output folder.

## Running the CLI

Create a `sites.txt` file, then run:

```powershell
WebSnapshots.exe --sites sites.txt --output output --max-depth 3 --max-pages 1500
```

From source:

```powershell
dotnet run --project .\WebSnapshots.csproj -- --sites sites.txt --output output --max-depth 3 --max-pages 1500
```

Useful options:

| Option | Default | Description |
| --- | --- | --- |
| `--sites`, `--input` | `sites.txt` | Text file with one URL per line |
| `--output`, `--out` | `output` | Output base folder |
| `--max-depth` | `3` | Crawl depth |
| `--max-pages` | `1500` | Maximum pages per site |
| `--cap-gb` | `150` | Total output size cap |
| `--landing-only` | off | Capture only each start URL |
| `--quick-preview` | off | Fast bounded crawl for checking site shape |
| `--preview-pages-per-depth` | `25` | Quick-preview page cap per depth |
| `--preview-children-per-page` | `18` | Quick-preview link cap per page |
| `--preview-seconds` | `90` | Quick-preview time cap |
| `--viewport` | `1920x1080` | Browser viewport |
| `--webp-quality` | `75` | Screenshot WebP quality, 1-100 |
| `--keep-query-strings` | off | Keep query strings when deduplicating URLs |
| `--delay-ms` | `0` | Delay between pages |
| `--user-agent` | default Chromium | Custom user-agent |

## Preparing sites.txt

Use one URL per line. Blank lines and lines starting with `#` are ignored.

```text
# municipalities
https://www.example.se/
https://www.example.org/
```

WebSnapshots adds a scheme when possible, but full `https://` URLs are recommended.

## Output Structure

A normal run writes:

```text
output/
  index.htm
  _runs/
    yyyy-MM-dd_HHmmss/
      index.htm
      run.json
      run.log
  MunicipalityName/
    yyMMdd/
      index.htm
      index.html
      viewer.htm
      nav.json
      scrape.json
      pages/
      shots/
```

Open `output/index.htm` for the global archive index, or open a municipality `viewer.htm` directly.

To serve an output folder locally:

```powershell
WebSnapshots.exe serve 8080 output
```

Then open:

```text
http://localhost:8080/
```

## Bundled Playwright Chromium

The Windows release package includes a Chromium browser under `browsers/`.

Use `WebSnapshots.bat` from the release folder so the app points Playwright at the bundled browser. `WebSnapshots.exe` can also run directly on machines where Playwright browsers are already installed globally.

## Troubleshooting

If the GUI opens but capture fails immediately, run through `WebSnapshots.bat` so `PLAYWRIGHT_CHROMIUM_SHELL` is set to the bundled Chromium executable.

If a run creates no archive pages, check that `sites.txt` contains valid URLs and that the target site is reachable from the machine.

If an archive is very small, check whether `--landing-only`, `--quick-preview`, `--max-depth`, or `--max-pages` limited the crawl.

If output grows too large, lower `--max-pages`, use `--quick-preview`, or reduce `--webp-quality`.

If a site blocks automated browsing, try a lower crawl rate with `--delay-ms` or use a custom `--user-agent`.

## Known Limitations

- Crawling is same-host only.
- The archive preserves rendered screenshots, not live page behavior.
- Forms, search, login-only areas, and dynamic post-load interactions are not captured as workflows.
- Very large or highly dynamic sites may need smaller page limits or manual review of the generated viewer.
- Sites that aggressively block automation may require operator tuning.

## License

Copyright (c) 2026 Dennis.

All rights reserved.

This software is proprietary. No permission is granted to use, copy, modify, distribute, or create derivative works without explicit prior written permission from the copyright holder.

The software is provided "as is", without warranty of any kind. The author shall not be liable for any damages arising from its use.
