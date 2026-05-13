
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WebSnapshots;

public static class Utils
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string EnsureScheme(string urlOrHost)
    {
        var s = (urlOrHost ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s)) return s;
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return s;
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return s;
        return "https://" + s;
    }

    public static string NormalizeUrl(string url, bool dropQuery)
    {
        try
        {
            var u = new Uri(url);
            var b = new UriBuilder(u)
            {
                Fragment = ""
            };
            if (dropQuery) b.Query = "";
            // Normalize trailing slash behavior
            var s = b.Uri.ToString();
            return s.TrimEnd('/');
        }
        catch
        {
            return (url ?? "").Trim();
        }
    }

    // Existing helper in your project (keep it)
    public static string? Absolutize(string baseUrl, string rawHref)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rawHref)) return null;
            var h = rawHref.Trim();

            if (h.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return null;
            if (h.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return null;
            if (h.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return null;
            if (h.StartsWith("#")) return null;

            if (Uri.TryCreate(new Uri(baseUrl), h, out var abs))
                return abs.ToString();

            return null;
        }
        catch
        {
            return null;
        }
    }

    // Convenience wrapper for newer code that expects "ToAbsoluteUrl"
    public static string ToAbsoluteUrl(string baseUrl, string rawHref)
        => Absolutize(baseUrl, rawHref) ?? "";

    public static bool IsHttpUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    // Used by NavCrawler filtering (avoid crawling obvious non-html assets)
    public static bool IsProbablyBinaryAsset(string pathOrUrlPath)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrlPath)) return false;

        var p = pathOrUrlPath.Trim();

        // If a full URL is passed, try extracting path.
        try
        {
            if (Uri.TryCreate(p, UriKind.Absolute, out var u))
                p = u.AbsolutePath;
        }
        catch { }

        p = p.ToLowerInvariant();

        string[] exts =
        {
            ".jpg",".jpeg",".png",".gif",".webp",".svg",".ico",
            ".pdf",".doc",".docx",".xls",".xlsx",".ppt",".pptx",
            ".zip",".rar",".7z",".gz",".tar",
            ".mp3",".wav",".flac",".mp4",".mov",".avi",".mkv",
            ".css",".js",".map",".woff",".woff2",".ttf",".eot"
        };

        return exts.Any(p.EndsWith);
    }

    public static string SafeFileBaseFromUrl(string url)
    {
        // Stable mapping: p_<sha1(normalized-url)>
        var norm = NormalizeUrl(url, dropQuery: true);
        using var sha1 = SHA1.Create();
        var bytes = Encoding.UTF8.GetBytes(norm);
        var hash = sha1.ComputeHash(bytes);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return "p_" + hex;
    }

    public static string PrettySegment(string seg)
    {
        if (string.IsNullOrWhiteSpace(seg)) return "(empty)";
        var s = seg.Trim().Replace("-", " ").Replace("_", " ");
        if (s.Length == 0) return "(empty)";
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    public static string HostToMunicipality(string host)
    {
        // You can customize mapping later. For now: strip www.
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;

        var h = host.Trim();
        if (h.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) h = h[4..];

        var parts = h.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return host;

        var first = parts[0];
        if (string.IsNullOrWhiteSpace(first)) return host;

        return char.ToUpperInvariant(first[0]) + first[1..];
    }

    public static long GetDirectorySizeBytes(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            long sum = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(f);
                    sum += fi.Length;
                }
                catch { }
            }
            return sum;
        }
        catch
        {
            return 0;
        }
    }

    public static async Task WriteJsonAsync<T>(string path, T obj)
    {
        var dirName = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dirName))
        {
            Directory.CreateDirectory(dirName);
        }
        var json = JsonSerializer.Serialize(obj, JsonOpts);
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);
    }
}