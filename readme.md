# WebSnapshots

WebSnapshots is a Playwright-based web archiving tool that captures visual snapshots of websites and produces a fully static, offline archive.

It crawls internal links on a site (same host only), renders each page headlessly, captures full-page screenshots (with automatic tiling fallback), and generates HTML viewers to browse the captured pages locally without any runtime dependencies.

The output is a visual record, not an HTML mirror.

---

## How It Works

    sites.txt
       ↓
    Program.cs
       ↓
    NavCrawler ──► nav.json
       ↓
    Snapshotter ──► WebP screenshots + per-page HTML
       ↓
    SiteViewerBuilder ──► viewer.htm (per site)
       ↓
    TopIndexBuilder ──► index.htm (global)

---

## Key Characteristics

- Same-host crawl only
- Depth and page-count limited
- Deterministic viewport rendering
- Full-page screenshots with WebP encoding
- Automatic handling of lazy loading, modals, and banners
- Hard global storage cap (abort on overflow)
- Static HTML output only (no JS frameworks, no backend)

---

## Running

Add URLs to `sites.txt`, one per line.

    dotnet run

Serve the output locally:

    dotnet run -- serve 8080 output

Open:

    http://localhost:8080/

---

## Copyright and License

Copyright © 2026 Dennis.

All rights reserved.

This software is proprietary. No permission is granted to use, copy, modify, distribute, or create derivative works without explicit prior written permission from the copyright holder.

The software is provided “as is”, without warranty of any kind. The author shall not be liable for any damages arising from its use.
