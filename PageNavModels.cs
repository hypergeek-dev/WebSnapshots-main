namespace WebSnapshots;

public sealed class PageNavLink
{
    public string Url { get; set; } = "";          // absolute URL
    public string Text { get; set; } = "";         // visible text
    public string Kind { get; set; } = "";         // legacy extractor bucket
    public string SourceType { get; set; } = "";   // MainNav, LocalNav, Breadcrumb, Generic...
    public string DisplayRole { get; set; } = "";  // Navigation, Shortcut, News, Events, PromoCard, Utility, Footer...
    public string GroupLabel { get; set; } = "";   // Human-facing section title
    public double Confidence { get; set; }         // 0..1
    public bool IsStructural { get; set; }         // true = suitable for nav tree
}

public sealed class PageNavPayload
{
    public string SourceUrl { get; set; } = "";
    public string Host { get; set; } = "";
    public string CapturedUtc { get; set; } = "";
    public List<PageNavLink> Links { get; set; } = new();
}