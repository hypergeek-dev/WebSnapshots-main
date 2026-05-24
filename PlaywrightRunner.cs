using Microsoft.Playwright;

namespace WebSnapshots;

public sealed class PlaywrightRunner : IAsyncDisposable
{
    private readonly SnapshotConfig _cfg;
    private readonly Logger? _log;

    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public PlaywrightRunner(SnapshotConfig cfg, Logger? log = null)
    {
        _cfg = cfg;
        _log = log;
    }

    public async Task<IBrowserContext> NewContextAsync()
    {
        try
        {
            if (_playwright == null)
            {
                _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            }

            if (_browser == null)
            {
                // PLAYWRIGHT_CHROMIUM_SHELL lets the host override the headless-shell path.
                // Useful when the default Playwright revision is broken on a specific machine.
                var shellOverride = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_SHELL")?.Trim();
                var execPath = shellOverride is { Length: > 0 } && File.Exists(shellOverride) ? shellOverride : null;

                if (execPath != null)
                    _log?.Info($"[PLAYWRIGHT] Using Chromium shell override: {execPath}");
                else if (!string.IsNullOrWhiteSpace(shellOverride))
                    _log?.Warn($"[PLAYWRIGHT] Ignoring PLAYWRIGHT_CHROMIUM_SHELL because the file was not found: {shellOverride}");
                else
                    _log?.Info("[PLAYWRIGHT] Using Playwright default Chromium executable.");

                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    ExecutablePath = execPath,
                    Args = new[]
                    {
                        "--disable-gpu",
                        "--no-sandbox",
                        "--disable-dev-shm-usage",
                        "--disable-blink-features=AutomationControlled"
                    }
                });
            }

            var userAgent = string.IsNullOrWhiteSpace(_cfg.UserAgent)
                ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36"
                : _cfg.UserAgent!.Trim();

            var ctx = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize
                {
                    Width = _cfg.ViewportWidth,
                    Height = _cfg.ViewportHeight
                },
                IgnoreHTTPSErrors = true,
                Locale = "sv-SE",
                TimezoneId = "Europe/Stockholm",
                UserAgent = userAgent,
                JavaScriptEnabled = true,
                BypassCSP = false,
                AcceptDownloads = false,
                ColorScheme = ColorScheme.Light,
                ReducedMotion = ReducedMotion.NoPreference,
                ServiceWorkers = ServiceWorkerPolicy.Allow
            });

            await WireContextDefaultsAsync(ctx);
            return ctx;
        }
        catch (PlaywrightException ex)
        {
            var msg = ex.Message ?? "";

            if (LooksLikeMissingBrowserInstall(msg))
            {
                var installHint = GetInstallHint();

                _log?.Error("[PLAYWRIGHT] Browser binaries missing. " + installHint);
                throw new PlaywrightException(
                    "Playwright browser binaries are missing.\n" +
                    installHint + "\n" +
                    "Fix: run the install command above, then run the app again.\n\n" +
                    "Original Playwright error:\n" + msg, ex);
            }

            _log?.Error("[PLAYWRIGHT] " + msg);
            throw;
        }
    }

    public async Task<IPage> NewPageAsync(IBrowserContext? ctx = null)
    {
        ctx ??= await NewContextAsync();

        var page = await ctx.NewPageAsync();
        await WirePageDefaultsAsync(page);
        return page;
    }

    private async Task WireContextDefaultsAsync(IBrowserContext ctx)
    {
        try
        {
            await ctx.AddInitScriptAsync(@"
Object.defineProperty(navigator, 'webdriver', {
  get: () => undefined
});
");
        }
        catch (Exception ex)
        {
            _log?.Warn($"[PLAYWRIGHT] AddInitScript failed: {ex.Message}");
        }

        try
        {
            ctx.Page += async (_, page) =>
            {
                try
                {
                    await WirePageDefaultsAsync(page);
                }
                catch (Exception ex)
                {
                    _log?.Warn($"[PLAYWRIGHT] Page init failed: {ex.Message}");
                }
            };
        }
        catch (Exception ex)
        {
            _log?.Warn($"[PLAYWRIGHT] Context page event wiring failed: {ex.Message}");
        }
    }

    private async Task WirePageDefaultsAsync(IPage page)
    {
        try
        {
            page.SetDefaultTimeout(_cfg.NavGotoTimeoutMs);
            page.SetDefaultNavigationTimeout(_cfg.NavGotoTimeoutMs);
        }
        catch { }

        try
        {
            page.Dialog += async (_, dialog) =>
            {
                try
                {
                    await dialog.DismissAsync();
                }
                catch { }
            };
        }
        catch { }

        try
        {
            page.Console += (_, msg) =>
            {
                if (_cfg.Debug)
                    _log?.Info($"[BROWSER_CONSOLE] {msg.Type}: {msg.Text}");
            };
        }
        catch { }

        try
        {
            page.PageError += (_, err) =>
            {
                if (_cfg.Debug)
                    _log?.Warn($"[BROWSER_PAGE_ERROR] {err}");
            };
        }
        catch { }
    }

    public async Task PreparePageForHumanInteractionAsync(
        IPage page,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            await page.EvaluateAsync(@"
() => {
  const tryHide = (sel) => {
    try {
      document.querySelectorAll(sel).forEach(el => {
        try { el.style.setProperty('display', 'none', 'important'); } catch {}
      });
    } catch {}
  };

  const hideSelectors = [
    '.cookie',
    '.cookie-banner',
    '.cookie-consent',
    '.cookieConsent',
    '#cookie',
    '#cookies',
    '#cookie-banner',
    '#cookieBanner',
    '[id*=""cookie"" i]',
    '[class*=""cookie"" i]',
    '[id*=""consent"" i]',
    '[class*=""consent"" i]',
    '[aria-modal=""true""]',
    '.modal-backdrop'
  ];

  hideSelectors.forEach(tryHide);

  try {
    document.querySelectorAll('details:not([open])').forEach(d => {
      try { d.open = true; } catch {}
    });
  } catch {}

  try {
    document.documentElement.style.scrollBehavior = 'auto';
  } catch {}

  try {
    document.querySelectorAll('img[loading=""lazy""]').forEach(img => {
      try { img.loading = 'eager'; } catch {}
    });
  } catch {}

  try {
    document.querySelectorAll('[data-src]').forEach(el => {
      try {
        const src = el.getAttribute('data-src');
        if (src && !el.getAttribute('src')) el.setAttribute('src', src);
      } catch {}
    });
  } catch {}

  try {
    document.querySelectorAll('[data-srcset]').forEach(el => {
      try {
        const srcset = el.getAttribute('data-srcset');
        if (srcset && !el.getAttribute('srcset')) el.setAttribute('srcset', srcset);
      } catch {}
    });
  } catch {}
}
");
        }
        catch (Exception ex)
        {
            _log?.Warn($"[PLAYWRIGHT] PreparePageForHumanInteractionAsync failed: {ex.Message}");
        }
    }

    public async Task RunHumanInteractionPassAsync(
        IPage page,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await PreparePageForHumanInteractionAsync(page, ct);

        try
        {
            await ExpandInteractiveElementsAsync(page, ct);
        }
        catch (Exception ex)
        {
            _log?.Warn($"[PLAYWRIGHT] ExpandInteractiveElementsAsync failed: {ex.Message}");
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            await ProgressiveScrollAsync(page, ct);
        }
        catch (Exception ex)
        {
            _log?.Warn($"[PLAYWRIGHT] ProgressiveScrollAsync failed: {ex.Message}");
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            await HoverNavigationAreasAsync(page, ct);
        }
        catch (Exception ex)
        {
            _log?.Warn($"[PLAYWRIGHT] HoverNavigationAreasAsync failed: {ex.Message}");
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            await page.EvaluateAsync("() => window.scrollTo(0, 0)");
            await page.WaitForTimeoutAsync(120);
        }
        catch { }
    }

    public async Task ExpandInteractiveElementsAsync(
        IPage page,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await page.EvaluateAsync(@"
async () => {
  const sleep = (ms) => new Promise(r => setTimeout(r, ms));

  const isVisible = (el) => {
    try {
      if (!el) return false;
      const cs = window.getComputedStyle(el);
      if (!cs) return false;
      if (cs.display === 'none' || cs.visibility === 'hidden' || cs.opacity === '0') return false;
      const r = el.getBoundingClientRect();
      if (!r) return false;
      if (r.width < 2 || r.height < 2) return false;
      return true;
    } catch {
      return false;
    }
  };

  const selectors = [
    'button[aria-expanded=""false""]',
    '[role=""button""][aria-expanded=""false""]',
    '.accordion button',
    '.accordion [role=""button""]',
    '.menu button',
    '.submenu-toggle',
    '.nav-toggle',
    '.hamburger',
    '.menu-toggle',
    '[data-toggle=""collapse""]',
    '[data-bs-toggle=""collapse""]',
    '[aria-controls]'
  ];

  const clicked = new Set();

  for (const sel of selectors) {
    let nodes = [];
    try { nodes = Array.from(document.querySelectorAll(sel)); } catch {}

    for (const el of nodes) {
      try {
        if (!isVisible(el)) continue;

        const key =
          (el.tagName || '') + '|' +
          (el.id || '') + '|' +
          ((typeof el.className === 'string' ? el.className : '') || '');

        if (clicked.has(key)) continue;
        clicked.add(key);

        el.click();
        await sleep(40);
      } catch {}
    }
  }

  try {
    document.querySelectorAll('details:not([open])').forEach(d => {
      try { d.open = true; } catch {}
    });
  } catch {}
}
");
    }

    public async Task ProgressiveScrollAsync(
        IPage page,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await page.EvaluateAsync(@"
async () => {
  const sleep = (ms) => new Promise(r => setTimeout(r, ms));

  const getHeight = () => Math.max(
    document.body ? document.body.scrollHeight : 0,
    document.documentElement ? document.documentElement.scrollHeight : 0
  );

  let lastHeight = 0;
  let stableRounds = 0;

  for (let round = 0; round < 12; round++) {
    const fullHeight = getHeight();
    const viewport = Math.max(window.innerHeight || 0, 600);
    const step = Math.max(300, Math.floor(viewport * 0.75));

    for (let y = 0; y < fullHeight; y += step) {
      try { window.scrollTo(0, y); } catch {}
      await sleep(90);
    }

    try { window.scrollTo(0, fullHeight); } catch {}
    await sleep(160);

    const newHeight = getHeight();
    if (newHeight === lastHeight) stableRounds++;
    else stableRounds = 0;

    lastHeight = newHeight;

    if (stableRounds >= 2) break;
  }
}
");
    }

    public async Task HoverNavigationAreasAsync(
        IPage page,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            var locators = new[]
            {
                "nav",
                "[role='navigation']",
                "header",
                ".menu",
                ".nav",
                ".navbar",
                ".sidebar",
                ".sidenav"
            };

            foreach (var selector in locators)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var first = page.Locator(selector).First;
                    if (await first.CountAsync() > 0)
                    {
                        await first.HoverAsync(new LocatorHoverOptions
                        {
                            Timeout = 500
                        });

                        await page.WaitForTimeoutAsync(60);
                    }
                }
                catch
                {
                    // best effort only
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Warn($"[PLAYWRIGHT] HoverNavigationAreasAsync failed: {ex.Message}");
        }
    }

    private static bool LooksLikeMissingBrowserInstall(string msg)
    {
        msg = msg.ToLowerInvariant();
        return msg.Contains("executable doesn't exist")
            || msg.Contains("run playwright install")
            || msg.Contains("playwright was just installed")
            || msg.Contains("browser has been closed") && msg.Contains("executable");
    }

    private static string GetInstallHint()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var ps1 = Path.Combine(baseDir, "playwright.ps1");

        return
            "Install browsers with:\n" +
            $"  powershell.exe -ExecutionPolicy Bypass -File \"{ps1}\" install";
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_browser != null)
            {
                await _browser.CloseAsync();
                _browser = null;
            }
        }
        catch { }

        try
        {
            _playwright?.Dispose();
            _playwright = null;
        }
        catch { }
    }
}
