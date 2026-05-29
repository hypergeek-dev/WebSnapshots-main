using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WebSnapshots;

public enum MunicipalRootClassificationKind
{
    EligibleStructuralRoot,
    UtilityRoot,
    HomepageModuleRoot,
    MicrositeRoot,
    NewsOrEventRoot,
    ErrorOrSystemRoot,
    DiscoveredOtherRoot
}

public sealed class MunicipalRootClassification
{
    public MunicipalRootClassificationKind Kind { get; init; }
    public bool IsEligible { get; init; }
    public double Confidence { get; init; }
    public List<string> Reasons { get; init; } = new();
    public List<string> EvidenceSignals { get; init; } = new();
    public List<string> SourceSignals { get; init; } = new();

    [JsonIgnore]
    public string KindName => Kind.ToString();
}

public sealed class MunicipalRootContext
{
    public string StartUrl { get; init; } = "";
    public bool WasPrimaryNav { get; init; }
    public bool WasAcceptedHomepageAnchor { get; init; }
    public bool HadChildren { get; init; }
    public string SourceGroup { get; init; } = "";
    public int BeforeRootCount { get; init; }
}

public static class MunicipalRootClassifier
{
    public static MunicipalRootClassification Classify(NavItem item, MunicipalRootContext context)
    {
        var url = item.Url ?? "";
        var title = item.Title ?? "";
        var sourceSignals = BuildSourceSignals(item, context);
        var evidence = new List<string>();
        var reasons = new List<string>();

        if (IsStartPageAlias(url, context.StartUrl))
            return Decision(MunicipalRootClassificationKind.HomepageModuleRoot, false, 1.0,
                reasons, evidence, sourceSignals, "homepage_alias_root");

        if (item.IsSynthetic)
            return Decision(MunicipalRootClassificationKind.DiscoveredOtherRoot, false, 0.85,
                reasons, evidence, sourceSignals, "synthetic_helper_or_discovered_bucket");

        if (IsUtilityPage(url, title) || item.IsUtility)
            return Decision(MunicipalRootClassificationKind.UtilityRoot, false, 0.95,
                reasons, evidence, sourceSignals, "utility_or_meta_page");

        if (IsErrorOrSystemRoot(url, title))
            return Decision(MunicipalRootClassificationKind.ErrorOrSystemRoot, false, 0.98,
                reasons, evidence, sourceSignals, "error_or_system_root");

        if (IsMicrositeRoot(url, title))
            return Decision(MunicipalRootClassificationKind.MicrositeRoot, false, 0.95,
                reasons, evidence, sourceSignals, "microsite_or_special_purpose_portal");

        if (IsNewsEventOrServiceRoot(url, title))
            return Decision(MunicipalRootClassificationKind.NewsOrEventRoot, false, 0.92,
                reasons, evidence, sourceSignals, "news_event_service_or_notice_root");

        var isNoticeboardRoot = IsNoticeboardRoot(url, title);
        if (isNoticeboardRoot)
        {
            if (context.WasPrimaryNav || context.WasAcceptedHomepageAnchor || context.HadChildren)
            {
                if (context.WasPrimaryNav)
                    evidence.Add("noticeboard_primary_nav_structural_signal");
                if (context.WasAcceptedHomepageAnchor)
                    evidence.Add("noticeboard_homepage_ia_anchor_structural_signal");
                if (context.HadChildren)
                    evidence.Add("noticeboard_parent_structural_signal");
            }
            else
            {
                return Decision(MunicipalRootClassificationKind.UtilityRoot, false, 0.86,
                    reasons, evidence, sourceSignals, "noticeboard_without_structural_root_evidence");
            }
        }

        if (!isNoticeboardRoot && IsHomepageModuleRoot(url, title, context.WasAcceptedHomepageAnchor, context.WasPrimaryNav))
            return Decision(MunicipalRootClassificationKind.HomepageModuleRoot, false, 0.84,
                reasons, evidence, sourceSignals, "homepage_module_or_promo_root");

        var municipalScore = ScoreMunicipalIaRoot(url, title, evidence);
        if (isNoticeboardRoot)
        {
            municipalScore += 0.45;
            evidence.Add("authored_noticeboard_section");
        }

        if (context.WasPrimaryNav)
        {
            municipalScore += 0.25;
            evidence.Add("primary_nav_positive_signal");
        }
        if (context.WasAcceptedHomepageAnchor)
        {
            municipalScore += 0.20;
            evidence.Add("homepage_ia_anchor_positive_signal");
        }
        if (context.HadChildren)
        {
            municipalScore += 0.10;
            evidence.Add("has_children_positive_signal");
        }

        if (municipalScore >= 0.55)
            return Decision(MunicipalRootClassificationKind.EligibleStructuralRoot, true, Clamp01(municipalScore),
                reasons, evidence, sourceSignals, "official_municipal_ia_section");

        return Decision(MunicipalRootClassificationKind.DiscoveredOtherRoot, false, 0.72,
            reasons, evidence, sourceSignals, "not_enough_municipal_ia_evidence");
    }

    public static bool IsEligibleMunicipalRoot(
        string url,
        string title,
        string startUrl,
        bool wasPrimaryNav = false,
        bool wasAcceptedHomepageAnchor = false,
        bool hadChildren = false,
        string sourceGroup = "")
    {
        var item = new NavItem { Url = url, Title = title };
        var decision = Classify(item, new MunicipalRootContext
        {
            StartUrl = startUrl,
            WasPrimaryNav = wasPrimaryNav,
            WasAcceptedHomepageAnchor = wasAcceptedHomepageAnchor,
            HadChildren = hadChildren,
            SourceGroup = sourceGroup
        });
        return decision.IsEligible;
    }

    public static bool IsUtilityPage(string url, string title)
    {
        var path = NormalizedPathText(url);
        var text = NormalizeText((title ?? "") + " " + path);

        var utilityTokens = new[]
        {
            "kontakt", "tillganglighet", "tillgaenglighet", "accessibility",
            "press", "grafisk", "intranat", "intranet", "admin", "logga in",
            "logga-in", "login", "ticket", "e post", "epost", "e-post",
            "webbplatsen", "om webbplatsen", "om-webbplatsen", "medarbetare",
            "for medarbetare", "for-medarbetare", "cookie", "cookies", "kakor",
            "om kakor", "gdpr", "integritet", "webbkarta", "sitemap",
            "tillganglighetsredogorelse"
        };

        return utilityTokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsLikelyMunicipalSection(string url, string title = "")
        => ScoreMunicipalIaRoot(url, title, new List<string>()) >= 0.55
           && !IsUtilityPage(url, title)
           && !IsMicrositeRoot(url, title)
           && !IsNewsEventOrServiceRoot(url, title)
           && !IsErrorOrSystemRoot(url, title);

    public static string CanonicalUrlKey(string? url)
        => CanonicalDisplayUrl(url).ToLowerInvariant();

    public static string CanonicalDisplayUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";

        try
        {
            var u = new Uri(url.Trim(), UriKind.Absolute);
            var builder = new UriBuilder(u)
            {
                Scheme = u.Scheme.ToLowerInvariant(),
                Host = u.Host.ToLowerInvariant(),
                Query = "",
                Fragment = ""
            };

            if ((builder.Scheme == "http" && builder.Port == 80)
                || (builder.Scheme == "https" && builder.Port == 443))
                builder.Port = -1;

            return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }
        catch
        {
            return (url ?? "").Trim().TrimEnd('/');
        }
    }

    public static int CountUrlPathSegments(string url)
    {
        try
        {
            return new Uri(url).AbsolutePath
                .TrimEnd('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Length;
        }
        catch
        {
            return 0;
        }
    }

    private static MunicipalRootClassification Decision(
        MunicipalRootClassificationKind kind,
        bool eligible,
        double confidence,
        List<string> reasons,
        List<string> evidence,
        List<string> sourceSignals,
        string reason)
    {
        reasons.Add(reason);
        evidence.Add(kind.ToString());
        return new MunicipalRootClassification
        {
            Kind = kind,
            IsEligible = eligible,
            Confidence = Clamp01(confidence),
            Reasons = reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            EvidenceSignals = evidence.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SourceSignals = sourceSignals.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static List<string> BuildSourceSignals(NavItem item, MunicipalRootContext context)
    {
        var signals = new List<string>();
        if (context.WasPrimaryNav) signals.Add("wasPrimaryNav");
        if (context.WasAcceptedHomepageAnchor) signals.Add("acceptedHomepageAnchor");
        if (context.HadChildren) signals.Add("hadChildren");
        if (item.IsUtility) signals.Add("itemMarkedUtility");
        if (item.IsSynthetic) signals.Add("itemMarkedSynthetic");
        if (item.IsDisplayOnly) signals.Add("itemMarkedDisplayOnly");
        if (!string.IsNullOrWhiteSpace(context.SourceGroup)) signals.Add("sourceGroup:" + context.SourceGroup);
        return signals;
    }

    private static bool IsStartPageAlias(string url, string startUrl)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (!Uri.TryCreate(startUrl, UriKind.Absolute, out var s)) return false;
        if (!u.Host.Equals(s.Host, StringComparison.OrdinalIgnoreCase)) return false;

        var path = NormalizePath(u.AbsolutePath).Trim('/');
        if (string.IsNullOrWhiteSpace(path)) return false;
        return path.Equals("index.html", StringComparison.OrdinalIgnoreCase)
            || path.Equals("index.htm", StringComparison.OrdinalIgnoreCase)
            || path.Equals("startsida", StringComparison.OrdinalIgnoreCase)
            || path.Equals("start", StringComparison.OrdinalIgnoreCase)
            || path.Equals("home", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsErrorOrSystemRoot(string url, string title)
    {
        var text = NormalizeText(title + " " + NormalizedPathText(url));
        var tokens = new[]
        {
            "sidan hittades inte", "page not found", "404", "error",
            "psidata", "ticket server", "system", "sok", "search"
        };
        return tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMicrositeRoot(string url, string title)
    {
        var text = NormalizeText(title + " " + NormalizedPathText(url));
        var tokens = new[]
        {
            "badrike", "ystad gymnasium", "gymnasium", "industrifastigheter",
            "industrifastighet", "for elever", "for-elever", "elevportal",
            "skolportal", "portal"
        };

        if (tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return true;

        var segments = PathSegments(url);
        if (segments.Length == 1)
        {
            var seg = NormalizeText(segments[0]);
            return seg.EndsWith("gymnasiet", StringComparison.OrdinalIgnoreCase)
                || seg.EndsWith("gymnasium", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsNewsEventOrServiceRoot(string url, string title)
    {
        var text = NormalizeText(title + " " + NormalizedPathText(url));
        var segments = PathSegments(url).Select(NormalizeText).ToArray();
        var first = segments.FirstOrDefault() ?? "";

        var rootTokens = new[]
        {
            "nyhet", "nyheter", "huvudnyheter", "evenemang", "event",
            "kalender", "aktuellt", "servicemeddelanden", "service meddelanden",
            "drift", "driftsinformation", "kampanj", "pressmeddelande"
        };
        if (rootTokens.Any(t => first.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (first.Equals("anslag", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Regex.IsMatch(text, @"\b20\d{2}\b") && !text.Contains("anslagstavla", StringComparison.OrdinalIgnoreCase))
            return true;

        var titleTokens = new[]
        {
            "sommarlov", "lov i ", "premiar", "pris", "prisas", "schema",
            "kungorelse", "beviljade bygglov", "grannehorande", "flyttade fordon",
            "subvention for trygghetsboende"
        };
        return titleTokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNoticeboardRoot(string url, string title)
    {
        var text = NormalizeText(title + " " + NormalizedPathText(url));
        return text.Contains("anslagstavla", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHomepageModuleRoot(
        string url,
        string title,
        bool wasAcceptedHomepageAnchor,
        bool wasPrimaryNav)
    {
        if (!wasAcceptedHomepageAnchor || wasPrimaryNav)
            return false;

        var score = ScoreMunicipalIaRoot(url, title, new List<string>());
        return score < 0.55 || IsNewsEventOrServiceRoot(url, title) || IsMicrositeRoot(url, title);
    }

    private static double ScoreMunicipalIaRoot(string url, string title, List<string> evidence)
    {
        var text = NormalizeText(title + " " + NormalizedPathText(url));
        var segments = PathSegments(url);
        var topLevel = segments.Length <= 1;
        var score = topLevel ? 0.12 : -0.10;
        if (topLevel) evidence.Add("top_level_path");

        AddSectionScore(text, evidence, ref score, "education", "barn", "utbildning", "skola", "forskola");
        AddSectionScore(text, evidence, ref score, "care_support", "omsorg", "stod", "hjalp", "aldre", "familj");
        AddSectionScore(text, evidence, ref score, "experience", "uppleva", "gora", "kultur", "fritid", "bibliotek");
        AddSectionScore(text, evidence, ref score, "building_environment", "bygga", "bo", "miljo", "bostad");
        AddSectionScore(text, evidence, ref score, "traffic", "trafik", "resor", "gator", "infrastruktur");
        AddSectionScore(text, evidence, ref score, "business_work", "jobb", "foretag", "naringsliv", "arbete", "arbetsmarknad");
        AddSectionScore(text, evidence, ref score, "municipality_politics", "kommun", "politik", "demokrati");
        AddSectionScore(text, evidence, ref score, "community_development", "samhallsutveckling", "utveckling");

        return Clamp01(score);
    }

    private static void AddSectionScore(
        string text,
        List<string> evidence,
        ref double score,
        string label,
        params string[] tokens)
    {
        var hits = tokens.Count(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (hits == 0) return;
        score += hits >= 2 ? 0.45 : 0.28;
        evidence.Add(label + "_tokens");
    }

    private static string[] PathSegments(string url)
    {
        try
        {
            return new Uri(url).AbsolutePath
                .Trim('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string NormalizedPathText(string url)
    {
        try
        {
            return NormalizeText(new Uri(url).AbsolutePath.Replace('-', ' ').Replace('_', ' '));
        }
        catch
        {
            return NormalizeText(url);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        path = path.Trim();
        if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;
        while (path.Contains("//", StringComparison.Ordinal)) path = path.Replace("//", "/", StringComparison.Ordinal);
        return path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal) ? path[..^1] : path;
    }

    private static string NormalizeText(string text)
    {
        var normalized = (text ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString()
            .Normalize(NormalizationForm.FormC)
            .ToLowerInvariant()
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Replace('|', ' ');
    }

    private static double Clamp01(double v) => Math.Max(0, Math.Min(1, v));
}
