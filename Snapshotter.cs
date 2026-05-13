using Microsoft.Playwright;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using System.Text;
using System.Text.Json;
using Img = SixLabors.ImageSharp.Image;

namespace WebSnapshots;

public sealed class Snapshotter
{
    private const int ABOVE_FOLD_MULTIPLIER = 2;
    private const int STABILITY_THRESHOLD = 3;
    private const int LAZY_LOAD_DELAY_MS = 120;
    private const int POST_DECODE_DELAY_MS = 150;

    private readonly SnapshotConfig _cfg;
    private readonly PlaywrightRunner _runner;
    private readonly Logger? _log;

    public Snapshotter(SnapshotConfig cfg, PlaywrightRunner runner) : this(cfg, runner, null) { }

    public Snapshotter(SnapshotConfig cfg, PlaywrightRunner runner, Logger? log)
    {
        _cfg = cfg;
        _runner = runner;
        _log = log;
    }

    public Task<int> CaptureAllAsync(string siteDir, List<NavItem> flat, StorageGovernor governor)
        => CaptureAllAsync(siteDir, flat, governor, CancellationToken.None, PauseController.Noop);

    public async Task<int> CaptureAllAsync(
        string siteDir,
        List<NavItem> flat,
        StorageGovernor governor,
        CancellationToken ct,
        PauseController pause)
    {
        var host = new DirectoryInfo(siteDir).Name;
        var pagesDir = Path.Combine(siteDir, "pages");
        var shotsDir = Path.Combine(siteDir, "shots");
        Directory.CreateDirectory(pagesDir);
        Directory.CreateDirectory(shotsDir);

        NavIndex? navIndex = null;
        try
        {
            var navPath = Path.Combine(siteDir, "nav.json");
            if (File.Exists(navPath))
            {
                var json = await File.ReadAllTextAsync(navPath, Encoding.UTF8, ct);
                navIndex = JsonSerializer.Deserialize<NavIndex>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
        }
        catch (Exception ex)
        {
            _log?.Warn($"SHOT_NAV_LOAD_WARN host={host} err={ex.Message}");
        }

        var treeOrder = new List<string>();
        if (navIndex?.Nodes != null && navIndex.Nodes.Count > 0)
        {
            void Walk(NavNode n)
            {
                if (!string.IsNullOrWhiteSpace(n.Url))
                    treeOrder.Add(Utils.NormalizeUrl(n.Url, _cfg.DropQueryStrings));

                if (n.Children == null) return;
                foreach (var c in n.Children) Walk(c);
            }

            foreach (var n in navIndex.Nodes) Walk(n);
        }
        else
        {
            foreach (var it in flat)
                treeOrder.Add(Utils.NormalizeUrl(it.Url, _cfg.DropQueryStrings));
        }

        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dedup = new List<string>(treeOrder.Count);
            foreach (var u in treeOrder)
                if (seen.Add(u)) dedup.Add(u);
            treeOrder = dedup;
        }

        var prevMap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var nextMap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < treeOrder.Count; i++)
        {
            var u = treeOrder[i];
            prevMap[u] = i > 0 ? treeOrder[i - 1] : null;
            nextMap[u] = i + 1 < treeOrder.Count ? treeOrder[i + 1] : null;
        }

        var topNavLinks = new List<(string Title, string Href)>();

        if (navIndex?.NavGroups != null && navIndex.NavGroups.Count > 0)
        {
            var primary = navIndex.NavGroups.OrderBy(g => g.Rank).FirstOrDefault() ?? navIndex.NavGroups[0];
            var tops = primary.Flat.Where(x => x.Depth == 0).ToList();

            foreach (var it in tops)
            {
                var norm = Utils.NormalizeUrl(it.Url, _cfg.DropQueryStrings);
                var safe = Utils.SafeFileBaseFromUrl(norm);
                var href = $"./{safe}.htm";
                var t = string.IsNullOrWhiteSpace(it.Title) ? it.Url : it.Title;
                topNavLinks.Add((t, href));
            }
        }
        else if (navIndex?.Nodes != null && navIndex.Nodes.Count > 0)
        {
            foreach (var n in navIndex.Nodes)
            {
                var norm = Utils.NormalizeUrl(n.Url, _cfg.DropQueryStrings);
                var safe = Utils.SafeFileBaseFromUrl(norm);
                var href = $"./{safe}.htm";
                var t = string.IsNullOrWhiteSpace(n.Title) ? n.Url : n.Title;
                topNavLinks.Add((t, href));
            }
        }

        await using var ctx = await _runner.NewContextAsync();
        var textExtractor = new TextExtractor(_log);

        int capturedPages = 0;

        foreach (var it in flat)
        {
            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);

            var totalBytes = Utils.GetDirectorySizeBytes(_cfg.OutputDir);
            governor.ThrowIfOverCap(totalBytes);

            if (capturedPages >= _cfg.MaxPagesPerSite) break;

            var url = Utils.NormalizeUrl(it.Url, _cfg.DropQueryStrings);
            var safeBase = Utils.SafeFileBaseFromUrl(url);

            var outHtml = Path.Combine(pagesDir, safeBase + ".htm");
            var outTextHtml = Path.Combine(pagesDir, safeBase + ".text.htm");

            if (File.Exists(outHtml))
            {
                _log?.Event("SHOT_SKIP_EXISTS", ("host", host), ("url", url), ("page", outHtml));
                capturedPages++;
                continue;
            }

            _log?.Event("SHOT_START", ("host", host), ("idx", capturedPages + 1), ("max", _cfg.MaxPagesPerSite), ("url", url));
            Console.WriteLine($"[SHOT] {host} {capturedPages + 1}/{_cfg.MaxPagesPerSite} {url}");

            var page = await ctx.NewPageAsync();

            try
            {
                ct.ThrowIfCancellationRequested();
                pause.WaitIfPaused(ct);

                using (_log?.Scope("SHOT_PAGE", ("url", url)) ?? DummyDisposable.Instance)
                {
                    await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = _cfg.ShotGotoTimeoutMs
                    });

                    ct.ThrowIfCancellationRequested();
                    pause.WaitIfPaused(ct);

                    try
                    {
                        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
                        {
                            Timeout = _cfg.NetworkIdleMaxMs
                        });
                    }
                    catch (Exception ex)
                    {
                        if (_cfg.Debug)
                            _log?.Event("NETWORK_IDLE_TIMEOUT", ("url", url), ("error", ex.Message));
                    }

                    ct.ThrowIfCancellationRequested();
                    pause.WaitIfPaused(ct);

                    TextContent textContent;
                    using (_log?.Scope("TEXT_EXTRACT", ("url", url)) ?? DummyDisposable.Instance)
                    {
                        textContent = await textExtractor.ExtractAsync(page, url);
                        _log?.Event("TEXT_EXTRACTED", ("url", url), ("sections", textContent.Sections.Count), ("links", textContent.Links.Count));
                    }

                    ct.ThrowIfCancellationRequested();
                    pause.WaitIfPaused(ct);

                    await EnhanceForSnapshotAsync(page);

                    ct.ThrowIfCancellationRequested();
                    pause.WaitIfPaused(ct);

                    List<string> images;
                    bool usedSingle = false;

                    try
                    {
                        images = await CaptureSingleAsWebpAsync(page, shotsDir, safeBase, governor, ct, pause);
                        usedSingle = true;
                    }
                    catch (Exception exSingle)
                    {
                        _log?.Warn($"SHOT_SINGLE_FAIL url={url} err={exSingle.Message}");
                        images = await CaptureTilesAsWebpAsync(page, shotsDir, safeBase, governor, ct, pause);
                    }

                    ct.ThrowIfCancellationRequested();
                    pause.WaitIfPaused(ct);

                    var title = (await page.TitleAsync())?.Trim();
                    if (string.IsNullOrWhiteSpace(title)) title = it.Title;
                    if (string.IsNullOrWhiteSpace(title)) title = url;

                    string? prevHref = null;
                    string? nextHref = null;

                    if (prevMap.TryGetValue(url, out var p) && !string.IsNullOrWhiteSpace(p))
                        prevHref = $"./{Utils.SafeFileBaseFromUrl(p!)}.htm";

                    if (nextMap.TryGetValue(url, out var n) && !string.IsNullOrWhiteSpace(n))
                        nextHref = $"./{Utils.SafeFileBaseFromUrl(n!)}.htm";

                    var pageHtml = PageHtmlBuilder.Build(
                        title: title!,
                        sourceUrl: url,
                        tileRelPaths: images,
                        overlapPx: usedSingle ? 0 : _cfg.TileOverlapPx,
                        textViewHref: safeBase + ".text.htm",
                        error: null,
                        topNavLinks: topNavLinks,
                        prevHref: prevHref,
                        nextHref: nextHref
                    );

                    await AtomicWrite.WriteAllTextAtomicAsync(outHtml, pageHtml, Encoding.UTF8);

                    var textPageHtml = TextPageBuilder.Build(textContent, safeBase + ".htm");
                    await AtomicWrite.WriteAllTextAtomicAsync(outTextHtml, textPageHtml, Encoding.UTF8);

                    _log?.Event("SHOT_OK",
                        ("url", url),
                        ("mode", usedSingle ? "single" : "tiles"),
                        ("images", images.Count),
                        ("page", outHtml),
                        ("textPage", outTextHtml));

                    capturedPages++;

                    if (_cfg.DelayBetweenPagesMs > 0)
                        await Task.Delay(_cfg.DelayBetweenPagesMs, ct);
                }
            }
            catch (OperationCanceledException)
            {
                _log?.Warn($"SHOT_CANCELLED url={url}");
                throw;
            }
            catch (StorageCapReachedException)
            {
                _log?.Warn($"SHOT_CAP_REACHED url={url}");
                throw;
            }
            catch (Exception ex)
            {
                _log?.Error($"SHOT_FAIL url={url} err={ex}");
                Console.WriteLine($"[SHOT] warn {url}: {ex.Message}");

                var stub = PageHtmlBuilder.Build(
                    title: $"FAILED: {it.Title}",
                    sourceUrl: url,
                    tileRelPaths: new List<string>(),
                    overlapPx: _cfg.TileOverlapPx,
                    textViewHref: null,
                    error: ex.Message,
                    topNavLinks: topNavLinks,
                    prevHref: null,
                    nextHref: null
                );

                await AtomicWrite.WriteAllTextAtomicAsync(outHtml, stub, Encoding.UTF8);
                capturedPages++;
            }
            finally
            {
                try { await page.CloseAsync(); } catch { }
            }
        }

        _log?.Event("SHOT_DONE", ("host", host), ("capturedPages", capturedPages));
        return capturedPages;
    }

    private async Task EnhanceForSnapshotAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync(@"
() => {
  const html = document.documentElement;
  const body = document.body;

  if (html) {
    html.style.overflow = 'auto';
    html.style.height = 'auto';
    html.classList.remove('no-scroll','noscroll','modal-open','is-locked','locked','sv-no-scroll','disable-scroll');
  }
  if (body) {
    body.style.overflow = 'auto';
    body.style.height = 'auto';
    body.classList.remove('no-scroll','noscroll','modal-open','is-locked','locked','sv-no-scroll','disable-scroll');
  }

  document.querySelectorAll('img[loading=""lazy""]').forEach(img => { try { img.loading = 'eager'; } catch {} });

  document.querySelectorAll('[data-src]').forEach(el => {
    try {
      const src = el.getAttribute('data-src');
      if (src && !el.getAttribute('src')) el.setAttribute('src', src);
    } catch {}
  });

  document.querySelectorAll('[data-srcset]').forEach(el => {
    try {
      const ss = el.getAttribute('data-srcset');
      if (ss && !el.getAttribute('srcset')) el.setAttribute('srcset', ss);
    } catch {}
  });

  document.querySelectorAll('[data-background],[data-bg],[data-background-image]').forEach(el => {
    try {
      const url = el.getAttribute('data-background') || el.getAttribute('data-bg') || el.getAttribute('data-background-image');
      if (url) el.style.backgroundImage = 'url(' + JSON.stringify(url) + ')';
    } catch {}
  });

  const sels = [
    '[aria-modal=""true""]',
    '.modal', '.overlay', '.backdrop',
    '.cookie', '.cookie-banner', '.cookie-consent', '.cookieConsent',
    '#cookieBanner', '#cookie-banner', '#cookie', '#cookies',
    '[id*=""cookie"" i]', '[class*=""cookie"" i]',
    '[id*=""consent"" i]', '[class*=""consent"" i]'
  ];
  sels.forEach(sel => {
    try { document.querySelectorAll(sel).forEach(el => { try { el.style.display = 'none'; } catch {} }); } catch {}
  });

  try {
    const all = Array.from(document.querySelectorAll('body *'));
    for (const el of all) {
      try {
        const cs = getComputedStyle(el);
        if (!cs) continue;
        if (cs.display === 'none' || cs.visibility === 'hidden') continue;
        if (cs.position !== 'fixed' && cs.position !== 'sticky') continue;

        const r = el.getBoundingClientRect();
        const bigEnough = (r.width > 200 && r.height > 40);
        const onScreen = (r.bottom > -50 && r.top < (window.innerHeight + 50));
        if (bigEnough && onScreen) {
          el.style.display = 'none';
        }
      } catch {}
    }
  } catch {}
}
");
        }
        catch (Exception ex)
        {
            if (_cfg.Debug)
                _log?.Event("ENHANCE_SCRIPT_ERROR", ("error", ex.Message));
        }

        await AutoScrollUntilStableAsync(page, _cfg.StabilizeMaxMs);

        try { await page.EvaluateAsync("() => window.scrollTo(0,0)"); } catch { }
        try { await page.WaitForTimeoutAsync(300); }
        catch (Exception ex)
        {
            if (_cfg.Debug)
                _log?.Event("SCROLL_TOP_TIMEOUT", ("error", ex.Message));
        }

        await EnsureAboveFoldReadyAsync(page);
    }

    private async Task AutoScrollUntilStableAsync(IPage page, int maxMs)
    {
        try
        {
            await page.EvaluateAsync(@"
async (params) => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const start = Date.now();
  let lastH = 0;
  let stable = 0;

  while (Date.now() - start < params.maxMs) {
    const h = Math.max(
      document.body ? document.body.scrollHeight : 0,
      document.documentElement ? document.documentElement.scrollHeight : 0
    );

    window.scrollTo(0, h);
    await sleep(350);

    const h2 = Math.max(
      document.body ? document.body.scrollHeight : 0,
      document.documentElement ? document.documentElement.scrollHeight : 0
    );

    if (h2 === lastH) stable++;
    else stable = 0;

    lastH = h2;
    if (stable >= params.threshold) break;
  }
}
", new { maxMs, threshold = STABILITY_THRESHOLD });
        }
        catch (Exception ex)
        {
            if (_cfg.Debug)
                _log?.Event("AUTO_SCROLL_ERROR", ("error", ex.Message));
        }
    }

    private async Task EnsureAboveFoldReadyAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync(@"
async (params) => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));

  try { window.scrollTo(0, 1); } catch {}
  await sleep(params.lazyDelay);
  try { window.scrollTo(0, 0); } catch {}
  await sleep(params.lazyDelay);

  const fold = window.innerHeight * params.foldMultiplier;
  const imgs = Array.from(document.images || []).filter(img => {
    const r = img.getBoundingClientRect();
    return r.bottom > -200 && r.top < fold;
  });

  for (const img of imgs) {
    try { img.loading = 'eager'; } catch {}
    try {
      const src = img.getAttribute('data-src');
      if (src && !img.getAttribute('src')) img.setAttribute('src', src);
    } catch {}
    try {
      const ss = img.getAttribute('data-srcset');
      if (ss && !img.getAttribute('srcset')) img.setAttribute('srcset', ss);
    } catch {}
  }

  const waitWithTimeout = (p, ms) => {
    return Promise.race([
      p,
      new Promise(res => setTimeout(res, ms))
    ]);
  };

  const waitOne = async (img) => {
    if (!img) return;

    if (!img.complete) {
      await waitWithTimeout(new Promise(res => {
        const done = () => res(null);
        try { img.addEventListener('load', done, { once: true }); } catch {}
        try { img.addEventListener('error', done, { once: true }); } catch {}
      }), params.perImgTimeoutMs);
    }

    try {
      if (typeof img.decode === 'function') {
        await waitWithTimeout(img.decode(), params.decodeTimeoutMs);
      }
    } catch {}
  };

  for (const img of imgs) {
    await waitOne(img);
  }

  await sleep(params.lazyDelay);
}
",
                new
                {
                    foldMultiplier = ABOVE_FOLD_MULTIPLIER,
                    lazyDelay = LAZY_LOAD_DELAY_MS,
                    perImgTimeoutMs = 2500,
                    decodeTimeoutMs = 2500
                });
        }
        catch (Exception ex)
        {
            if (_cfg.Debug)
                _log?.Event("ABOVE_FOLD_ERROR", ("error", ex.Message));
        }

        try { await page.WaitForTimeoutAsync(POST_DECODE_DELAY_MS); }
        catch (Exception ex)
        {
            if (_cfg.Debug)
                _log?.Event("POST_DECODE_TIMEOUT", ("error", ex.Message));
        }
    }

    private async Task<List<string>> CaptureSingleAsWebpAsync(
        IPage page,
        string shotsDirAbs,
        string safeBase,
        StorageGovernor governor,
        CancellationToken ct,
        PauseController pause)
    {
        ct.ThrowIfCancellationRequested();
        pause.WaitIfPaused(ct);

        var totalBytes = Utils.GetDirectorySizeBytes(_cfg.OutputDir);
        governor.ThrowIfOverCap(totalBytes);

        try { await page.WaitForTimeoutAsync(_cfg.ScrollSettleMs); }
        catch (Exception ex)
        {
            if (_cfg.Debug)
                _log?.Event("PRE_SCREENSHOT_TIMEOUT", ("error", ex.Message));
        }

        ct.ThrowIfCancellationRequested();
        pause.WaitIfPaused(ct);

        byte[] png = await page.ScreenshotAsync(new PageScreenshotOptions
        {
            FullPage = true,
            Type = ScreenshotType.Png
        });

        ct.ThrowIfCancellationRequested();
        pause.WaitIfPaused(ct);

        var fileName = safeBase + ".full.webp";
        var outAbs = Path.Combine(shotsDirAbs, fileName);
        var tmpAbs = outAbs + ".tmp";

        using (var pngStream = new MemoryStream(png))
        using (var img = await Img.LoadAsync(pngStream, ct))
        {
            var enc = new WebpEncoder { Quality = _cfg.WebpQuality };
            await img.SaveAsWebpAsync(tmpAbs, enc, ct);
        }

        File.Move(tmpAbs, outAbs, overwrite: true);

        _log?.Event("SHOT_SINGLE",
            ("base", safeBase),
            ("file", fileName),
            ("bytesPng", png.Length)
        );

        return new List<string> { "../shots/" + fileName };
    }

    private async Task<List<string>> CaptureTilesAsWebpAsync(
        IPage page,
        string shotsDirAbs,
        string safeBase,
        StorageGovernor governor,
        CancellationToken ct,
        PauseController pause)
    {
        var rels = new List<string>();

        int fullHeight;
        try
        {
            fullHeight = await page.EvaluateAsync<int>(
                @"() => Math.max(
                    document.body ? document.body.scrollHeight : 0,
                    document.documentElement ? document.documentElement.scrollHeight : 0
                  )");
        }
        catch
        {
            fullHeight = _cfg.ViewportHeight;
        }

        if (fullHeight <= 0) fullHeight = _cfg.ViewportHeight;

        var overlap = Math.Min(_cfg.TileOverlapPx, Math.Max(0, _cfg.ViewportHeight - 1));
        var autoStep = Math.Max(1, _cfg.ViewportHeight - overlap);

        var step = _cfg.ScrollStepPx > 0 ? _cfg.ScrollStepPx : autoStep;
        if (step >= _cfg.ViewportHeight) step = autoStep;

        try { await page.EvaluateAsync("(yy) => window.scrollTo(0, yy)", 0); } catch { }
        try { await page.WaitForTimeoutAsync(_cfg.ScrollSettleMs); }
        catch (Exception ex)
        {
            if (_cfg.Debug)
                _log?.Event("TILE_SETTLE_TIMEOUT", ("error", ex.Message));
        }

        await EnsureAboveFoldReadyAsync(page);

        var y = 0;
        var idx = 0;

        while (y < fullHeight)
        {
            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);

            var totalBytes = Utils.GetDirectorySizeBytes(_cfg.OutputDir);
            governor.ThrowIfOverCap(totalBytes);

            try { await page.EvaluateAsync("(yy) => window.scrollTo(0, yy)", y); } catch { }
            try { await page.WaitForTimeoutAsync(_cfg.ScrollSettleMs); }
            catch (Exception ex)
            {
                if (_cfg.Debug)
                    _log?.Event("TILE_SCROLL_TIMEOUT", ("idx", idx), ("error", ex.Message));
            }

            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);

            byte[] png = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                FullPage = false,
                Type = ScreenshotType.Png
            });

            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);

            var fileName = safeBase + ".tile" + idx.ToString("0000") + ".webp";
            var outAbs = Path.Combine(shotsDirAbs, fileName);
            var tmpAbs = outAbs + ".tmp";

            using (var pngStream = new MemoryStream(png))
            using (var img = await Img.LoadAsync(pngStream, ct))
            {
                var enc = new WebpEncoder { Quality = _cfg.WebpQuality };
                await img.SaveAsWebpAsync(tmpAbs, enc, ct);
            }

            File.Move(tmpAbs, outAbs, overwrite: true);

            rels.Add("../shots/" + fileName);

            _log?.Event("SHOT_TILE",
                ("base", safeBase),
                ("idx", idx),
                ("y", y),
                ("step", step),
                ("viewportH", _cfg.ViewportHeight),
                ("overlap", overlap),
                ("file", fileName)
            );

            idx++;
            y += step;

            if (idx >= _cfg.MaxTilesPerPage) break;
        }

        try { await page.EvaluateAsync("(yy) => window.scrollTo(0, yy)", 0); } catch { }

        return rels;
    }

    private sealed class DummyDisposable : IDisposable
    {
        public static readonly DummyDisposable Instance = new();
        public void Dispose() { }
    }
}

public static class PageHtmlBuilder
{
    public static string Build(
        string title,
        string sourceUrl,
        List<string> tileRelPaths,
        int overlapPx,
        string? textViewHref = null,
        string? error = null)
    {
        return Build(
            title: title,
            sourceUrl: sourceUrl,
            tileRelPaths: tileRelPaths,
            overlapPx: overlapPx,
            textViewHref: textViewHref,
            error: error,
            topNavLinks: null,
            prevHref: null,
            nextHref: null
        );
    }

    public static string Build(
        string title,
        string sourceUrl,
        List<string> tileRelPaths,
        int overlapPx,
        string? textViewHref = null,
        string? error = null,
        List<(string Title, string Href)>? topNavLinks = null,
        string? prevHref = null,
        string? nextHref = null)
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        var safeTitle = E(title ?? "");
        var safeUrl = E(sourceUrl ?? "");

        var sb = new StringBuilder();

        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"sv\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("  <title>").Append(safeTitle).AppendLine("</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    :root{ --accent:#7a003c; --bg:#fff; --fg:#222; --muted:#666; --line:#ddd; }");
        sb.AppendLine("    body{margin:0;font-family:system-ui,Segoe UI,Arial,sans-serif;background:#f6f6f6;color:var(--fg);}");
        sb.AppendLine("    header{position:sticky;top:0;background:var(--bg);border-bottom:1px solid var(--line);padding:0.55rem 0.8rem;z-index:10;display:flex;flex-direction:column;gap:0.45rem;}");
        sb.AppendLine("    .toprow{display:flex;justify-content:space-between;align-items:center;gap:1rem;}");
        sb.AppendLine("    .header-info{flex:1;min-width:0;}");
        sb.AppendLine("    .t{font-weight:600;font-size:0.95rem;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}");
        sb.AppendLine("    .u{font-size:0.85rem;color:var(--muted);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}");
        sb.AppendLine("    .u a{color:var(--accent);text-decoration:none;}");
        sb.AppendLine("    .u a:hover{text-decoration:underline;}");
        sb.AppendLine("    .controls{display:flex;gap:0.5rem;align-items:center;flex-wrap:wrap;justify-content:flex-end;}");
        sb.AppendLine("    .btn{display:inline-flex;align-items:center;gap:0.45rem;padding:0.45rem 0.7rem;border-radius:10px;border:1px solid var(--line);background:#111;color:#fff;text-decoration:none;font-size:0.85rem;}");
        sb.AppendLine("    .btn.secondary{background:#fff;color:#111;}");
        sb.AppendLine("    .btn[aria-disabled=\"true\"]{opacity:0.45;pointer-events:none;}");
        sb.AppendLine("    .navrow{display:flex;gap:0.5rem;align-items:center;flex-wrap:wrap;max-width:100%;}");
        sb.AppendLine("    .pill{display:inline-flex;align-items:center;max-width:100%;padding:0.32rem 0.6rem;border-radius:999px;border:1px solid var(--line);background:#fff;color:#111;font-size:0.82rem;text-decoration:none;white-space:normal;overflow-wrap:anywhere;word-break:break-word;line-height:1.15;}");
        sb.AppendLine("    .pill:hover{border-color:var(--accent);}");
        sb.AppendLine("    .label{font-size:0.78rem;color:var(--muted);margin-right:0.2rem;}");
        sb.AppendLine("    main{padding:0.7rem;}");
        sb.AppendLine("    .tilewrap{display:block;width:100%;max-width:1200px;margin:0 auto 10px auto;overflow:hidden;background:#fff;border:1px solid var(--line);border-radius:10px;}");
        sb.AppendLine("    .tile{display:block;width:100%;height:auto;position:relative;top:0;left:0;}");
        sb.AppendLine("    .warn{max-width:900px;margin:2rem auto;padding:1rem;background:#fff3f3;border:1px solid #f0b2b2;border-radius:8px;}");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("  <header>");
        sb.AppendLine("    <div class=\"toprow\">");
        sb.AppendLine("      <div class=\"header-info\">");
        sb.Append("        <div class=\"t\">").Append(safeTitle).AppendLine("</div>");
        sb.Append("        <div class=\"u\"><a href=\"").Append(safeUrl).Append("\" target=\"_blank\" rel=\"noreferrer\">").Append(safeUrl).AppendLine("</a></div>");
        sb.AppendLine("      </div>");

        sb.AppendLine("      <div class=\"controls\">");

        if (!string.IsNullOrWhiteSpace(prevHref))
            sb.Append("        <a class=\"btn secondary\" href=\"").Append(E(prevHref!)).AppendLine("\">⬅ Prev</a>");
        else
            sb.AppendLine("        <a class=\"btn secondary\" aria-disabled=\"true\" href=\"#\">⬅ Prev</a>");

        if (!string.IsNullOrWhiteSpace(nextHref))
            sb.Append("        <a class=\"btn secondary\" href=\"").Append(E(nextHref!)).AppendLine("\">Next ➡</a>");
        else
            sb.AppendLine("        <a class=\"btn secondary\" aria-disabled=\"true\" href=\"#\">Next ➡</a>");

        if (!string.IsNullOrWhiteSpace(textViewHref))
            sb.Append("        <a class=\"btn\" href=\"").Append(E(textViewHref!)).AppendLine("\">📄 Text</a>");

        sb.AppendLine("      </div>");
        sb.AppendLine("    </div>");

        if (topNavLinks != null && topNavLinks.Count > 0)
        {
            sb.AppendLine("    <div class=\"navrow\">");
            sb.AppendLine("      <span class=\"label\">Top:</span>");
            foreach (var (t, h) in topNavLinks)
            {
                sb.Append("      <a class=\"pill\" href=\"").Append(E(h)).Append("\">").Append(E(t)).AppendLine("</a>");
            }
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("  </header>");

        sb.AppendLine("  <main>");

        if (tileRelPaths.Count > 0)
        {
            for (int i = 0; i < tileRelPaths.Count; i++)
            {
                var crop = i == 0 ? 0 : Math.Max(0, overlapPx);
                sb.Append("    <div class=\"tilewrap\" data-crop=\"").Append(crop.ToString()).AppendLine("\">");
                sb.Append("      <img class=\"tile\" src=\"").Append(E(tileRelPaths[i])).AppendLine("\" alt=\"\">");
                sb.AppendLine("    </div>");
            }

            sb.AppendLine("    <script>");
            sb.AppendLine("      (function(){");
            sb.AppendLine("        const wraps = Array.from(document.querySelectorAll('.tilewrap'));");
            sb.AppendLine("        function applyOne(w){");
            sb.AppendLine("          const cropRaw = parseInt(w.getAttribute('data-crop') || '0', 10) || 0;");
            sb.AppendLine("          if (cropRaw <= 0) return;");
            sb.AppendLine("          const img = w.querySelector('img');");
            sb.AppendLine("          if (!img) return;");
            sb.AppendLine("          if (!img.naturalHeight || !img.clientHeight) return;");
            sb.AppendLine("          const scale = img.clientHeight / img.naturalHeight;");
            sb.AppendLine("          const cropDisplay = cropRaw * scale;");
            sb.AppendLine("          const visibleH = Math.max(0, img.clientHeight - cropDisplay);");
            sb.AppendLine("          w.style.height = visibleH + 'px';");
            sb.AppendLine("          img.style.top = (-cropDisplay) + 'px';");
            sb.AppendLine("        }");
            sb.AppendLine("        for (const w of wraps){");
            sb.AppendLine("          const img = w.querySelector('img');");
            sb.AppendLine("          if (!img) continue;");
            sb.AppendLine("          if (img.complete) applyOne(w);");
            sb.AppendLine("          else img.addEventListener('load', function(){ applyOne(w); }, { once: true });");
            sb.AppendLine("        }");
            sb.AppendLine("        window.addEventListener('resize', function(){ for (const w of wraps) applyOne(w); });");
            sb.AppendLine("      })();");
            sb.AppendLine("    </script>");
        }
        else
        {
            var msg = string.IsNullOrWhiteSpace(error) ? "No images captured." : error;
            sb.Append("    <div class=\"warn\">").Append(E(msg)).AppendLine("</div>");
        }

        sb.AppendLine("  </main>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }
}