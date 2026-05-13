using Microsoft.Playwright;
using System.Text;
using System.Text.Json;

namespace WebSnapshots;

public sealed class TextExtractor
{
    private readonly Logger? _log;

    public TextExtractor(Logger? log = null)
    {
        _log = log;
    }

    public async Task<TextContent> ExtractAsync(IPage page, string url)
    {
        try
        {
            Console.WriteLine($"[TEXT] Step 1: Getting title for {url}");
            var title = await page.EvaluateAsync<string>("() => document.title || ''") ?? "";
            Console.WriteLine($"[TEXT] Title: {title}");

            Console.WriteLine($"[TEXT] Step 2: Getting description");
            var description = await page.EvaluateAsync<string>(
                "() => { const meta = document.querySelector('meta[name=\"description\"]'); return meta ? (meta.getAttribute('content') || '') : ''; }"
            ) ?? "";
            Console.WriteLine($"[TEXT] Description length: {description.Length}");

            Console.WriteLine($"[TEXT] Step 3: Collecting visible content blocks in DOM order (main-first)");

            var domItems = await page.EvaluateAsync<JsonElement>(@"
() => {
  const root =
    document.querySelector('main') ||
    document.querySelector('[role=""main""]') ||
    document.body;

  const norm = (s) => (s || '').replace(/\s+/g, ' ').trim();

  const isVisible = (el) => {
    try {
      if (!el) return false;
      const cs = window.getComputedStyle(el);
      if (!cs) return false;
      if (cs.display === 'none' || cs.visibility === 'hidden' || cs.opacity === '0') return false;
      const r = el.getBoundingClientRect();
      if (!r || (r.width <= 1 && r.height <= 1)) return false;
      return true;
    } catch {
      return false;
    }
  };

  const isCodeLike = (t) => {
    if (!t) return false;
    const s = t;
    const lower = s.toLowerCase();
    if (lower.includes('svdocready') || lower.includes('$svjq') || lower.includes('function(') || lower.includes('=>')) return true;

    let punct = 0;
    for (let i = 0; i < s.length; i++) {
      const c = s.charCodeAt(i);
      if (c === 59 || c === 123 || c === 125 || c === 40 || c === 41 || c === 61) punct++;
    }
    if (s.length >= 120 && punct / s.length > 0.05) return true;

    const spaces = (s.match(/ /g) || []).length;
    if (s.length >= 200 && spaces / s.length < 0.06) return true;

    return false;
  };

  const isNaturalText = (t) => {
    if (!t) return false;
    const s = t.trim();
    if (s.length < 10) return false;
    if (!s.includes(' ')) return false;

    const lower = s.toLowerCase();
    if (lower.includes('svdocready') || lower.includes('$svjq') || lower.includes('function') || lower.includes('=>')) return false;

    const letters = (s.match(/[a-z���A-Z���]/g) || []).length;
    const ratio = letters / s.length;
    if (ratio < 0.6) return false;

    if (!/[.!?\u0085]$/.test(s)) return false;
    return true;
  };

  const positions = new Map();
  try {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, null);
    let idx = 0;
    let n;
    while ((n = walker.nextNode())) positions.set(n, idx++);
  } catch {}

  const ignoreSelectors = [
    'nav', 'header', 'footer', 'aside',
    '[role=""navigation""]', '[role=""banner""]', '[role=""contentinfo""]',
    '.sv-toolbar', '.sv-cookie', '.cookie', '.cookie-banner', '.cookie-consent',
    '#cookieBanner', '#cookie-banner', '#cookies'
  ];

  const isInIgnoredZone = (el) => {
    try {
      for (const sel of ignoreSelectors) {
        if (el.closest && el.closest(sel)) return true;
      }
      return false;
    } catch {
      return false;
    }
  };

  const safeText = (el) => {
    try {
      return norm(el.innerText || '');
    } catch {
      try {
        const c = el.cloneNode(true);
        if (c.querySelectorAll) {
          c.querySelectorAll('script,style,noscript').forEach(n => n.remove());
        }
        return norm(c.textContent || '');
      } catch {
        return '';
      }
    }
  };

  // Include the specific SiteVision teaser span
  const sel = 'h1,h2,h3,h4,h5,h6,p,li,blockquote,pre,span.sol-article-item-desc';
  const nodes = Array.from(root.querySelectorAll(sel));

  const divCandidates = Array.from(root.querySelectorAll('div')).filter(el => {
    if (!isVisible(el)) return false;
    if (isInIgnoredZone(el)) return false;

    if (el.querySelector && el.querySelector('h1,h2,h3,h4,h5,h6,p,li,blockquote,pre,span.sol-article-item-desc')) return false;
    if (el.querySelector && el.querySelector('script,style,noscript')) return false;

    const t = safeText(el);
    if (!t) return false;
    if (t.length < 20) return false;
    if (isCodeLike(t)) return false;
    return true;
  });

  const spanCandidates = Array.from(root.querySelectorAll('span.sol-article-item-desc')).filter(el => {
    if (!isVisible(el)) return false;
    if (isInIgnoredZone(el)) return false;
    if (el.closest && el.closest('nav,header,footer,aside,button')) return false;

    const t = safeText(el);
    if (!t) return false;
    if (!isNaturalText(t)) return false;
    if (isCodeLike(t)) return false;
    return true;
  });

  const all = nodes.concat(divCandidates).concat(spanCandidates);

  const items = [];
  for (const el of all) {
    if (!el) continue;
    if (!isVisible(el)) continue;
    if (isInIgnoredZone(el)) continue;

    const tag = (el.tagName || '').toLowerCase();

    let text = '';
    if (tag === 'pre') text = norm(el.textContent || '');
    else text = safeText(el);

    if (!text) continue;
    if (tag !== 'pre' && isCodeLike(text)) continue;

    if (tag === 'span' && el.classList && el.classList.contains('sol-article-item-desc')) {
      if (!isNaturalText(text)) continue;
    }

    if (/^h[1-6]$/.test(tag)) {
      items.push({ type: tag, text, pos: positions.get(el) ?? 0 });
      continue;
    }

    if (tag === 'pre') {
      items.push({ type: 'pre', text, pos: positions.get(el) ?? 0 });
      continue;
    }

    if (tag === 'blockquote') {
      items.push({ type: 'blockquote', text, pos: positions.get(el) ?? 0 });
      continue;
    }

    if (text.length < 3) continue;
    items.push({ type: 'text', text, pos: positions.get(el) ?? 0 });
  }

  items.sort((a, b) => a.pos - b.pos);

  const deduped = [];
  let lastKey = '';
  for (const it of items) {
    const key = it.type + '|' + it.text;
    if (key === lastKey) continue;
    lastKey = key;
    deduped.push(it);
  }

  return deduped;
}
");

            int domCount = 0;
            if (domItems.ValueKind == JsonValueKind.Array) domCount = domItems.GetArrayLength();
            Console.WriteLine($"[TEXT] DOM items collected: {domCount}");

            Console.WriteLine($"[TEXT] Step 4: Extracting links (enabled)");

            // Important: Viewer "Current page links" is backed by /pages/{safe}.links.json.
            // Snapshotter writes that file from TextContent.Links.
            // So this must be non-empty for the UI to show anything.
            var linkItems = await page.EvaluateAsync<JsonElement>(@"
() => {
  const norm = (s) => (s || '').replace(/\s+/g, ' ').trim();

  const badHref = (href) => {
    if (!href) return true;
    const h = href.trim();
    if (!h) return true;
    if (h === '#') return true;
    if (h.startsWith('#')) return true;
    const lower = h.toLowerCase();
    if (lower.startsWith('javascript:')) return true;
    if (lower.startsWith('mailto:')) return true;
    if (lower.startsWith('tel:')) return true;
    return false;
  };

  const isVisible = (el) => {
    try {
      if (!el) return false;
      const cs = window.getComputedStyle(el);
      if (!cs) return false;
      if (cs.display === 'none' || cs.visibility === 'hidden' || cs.opacity === '0') return false;
      const r = el.getBoundingClientRect();
      if (!r) return false;
      if (r.width <= 1 && r.height <= 1) return false;
      return true;
    } catch {
      return true; // best-effort
    }
  };

  const anchors = Array.from(document.querySelectorAll('a[href]'));
  const out = [];
  const MAX = 2500; // guardrail for mega-pages

  for (const a of anchors) {
    if (!a) continue;
    const href = a.getAttribute('href') || '';
    if (badHref(href)) continue;

    // Keep links even if not visible sometimes, but prefer visible ones.
    // The list is used for navigation, not just 'what you can see'.
    const visible = isVisible(a);

    let text = '';
    try {
      text = norm(a.innerText || '') ||
             norm(a.getAttribute('aria-label') || '') ||
             norm(a.getAttribute('title') || '') ||
             norm(a.textContent || '');
    } catch {
      text = '';
    }

    // If still no text, keep it anyway but label later in UI as empty
    out.push({ href: href.trim(), text: text, visible });

    if (out.length >= MAX) break;
  }

  // Sort: visible first, then longer text first, then stable order
  out.sort((x, y) => {
    if (x.visible !== y.visible) return x.visible ? -1 : 1;
    const lx = (x.text || '').length;
    const ly = (y.text || '').length;
    return ly - lx;
  });

  return out;
}
");

            var links = new List<TextLink>();
            int linkCount = 0;

            if (linkItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var li in linkItems.EnumerateArray())
                {
                    var href = li.TryGetProperty("href", out var hProp) ? (hProp.GetString() ?? "") : "";
                    var text = li.TryGetProperty("text", out var tProp) ? (tProp.GetString() ?? "") : "";

                    if (string.IsNullOrWhiteSpace(href)) continue;

                    links.Add(new TextLink
                    {
                        Href = href.Trim(),
                        Text = (text ?? "").Trim()
                    });
                }

                linkCount = links.Count;
            }

            Console.WriteLine($"[TEXT] Links extracted: {linkCount}");

            Console.WriteLine($"[TEXT] Step 5: Building sections from DOM order");

            var top = new TextSection
            {
                Type = "heading",
                Level = "h2",
                Text = "Top",
                Content = new List<ContentBlock>()
            };

            var sections = new List<TextSection>();
            TextSection? current = null;

            if (domItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in domItems.EnumerateArray())
                {
                    var type = item.TryGetProperty("type", out var tProp) ? (tProp.GetString() ?? "") : "";
                    var text = item.TryGetProperty("text", out var xProp) ? (xProp.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(text)) continue;

                    var isHeading = type.Length == 2 && type[0] == 'h' && char.IsDigit(type[1]);

                    if (isHeading)
                    {
                        if (current == null)
                        {
                            if (top.Content.Count > 0) sections.Add(top);
                        }
                        else
                        {
                            sections.Add(current);
                        }

                        current = new TextSection
                        {
                            Type = "heading",
                            Level = type,
                            Text = text.Trim(),
                            Content = new List<ContentBlock>()
                        };
                        continue;
                    }

                    var target = current ?? top;

                    if (type == "pre")
                        target.Content.Add(new ContentBlock { Type = "code", Text = text });
                    else if (type == "blockquote")
                        target.Content.Add(new ContentBlock { Type = "quote", Text = text });
                    else
                        target.Content.Add(new ContentBlock { Type = "paragraph", Text = text });
                }
            }

            if (current != null) sections.Add(current);
            else if (top.Content.Count > 0) sections.Add(top);

            Console.WriteLine($"[TEXT] Sections built: {sections.Count}");
            Console.WriteLine($"[TEXT] Step 6: Building result");

            return new TextContent
            {
                Url = url,
                Title = title,
                Description = description,
                Sections = sections,
                Links = links
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TEXT] ERROR at: {ex.Message}");
            Console.WriteLine($"[TEXT] Stack trace: {ex.StackTrace}");
            _log?.Error($"Text extraction failed for {url}: {ex.Message}");

            return new TextContent
            {
                Url = url,
                Title = "Extraction Failed",
                Description = ex.Message,
                Sections = new List<TextSection>(),
                Links = new List<TextLink>()
            };
        }
    }
}

public sealed class TextContent
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<TextSection> Sections { get; set; } = new();
    public List<TextLink> Links { get; set; } = new();
}

public sealed class TextSection
{
    public string Type { get; set; } = "";
    public string Level { get; set; } = "";
    public string Text { get; set; } = "";
    public List<ContentBlock> Content { get; set; } = new();
}

public sealed class ContentBlock
{
    public string Type { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Ordered { get; set; }
    public List<string> Items { get; set; } = new();
}

public sealed class TextLink
{
    public string Text { get; set; } = "";
    public string Href { get; set; } = "";
}