// =============================================================================
//  Platform formats + formatter — one correctly-shaped variant per platform.
//  Dimensions, caption limits, and hashtag counts are applied here so the
//  operator can copy/paste the exported caption verbatim. The UTM link is
//  always preserved intact — it is what keeps Skool attribution working after
//  the switch to manual posting.
// =============================================================================
namespace InnerShiftLab.Outbox;

/// <summary>Rendering and caption rules for one target platform.</summary>
public sealed record PlatformFormat(
    string Platform,
    int Width,
    int Height,
    int CaptionMaxChars,
    int MaxHashtags,
    bool PrefersVideo,
    int TitleMaxChars,
    bool LinkInBio);

public static class PlatformFormats
{
    // Caption limits are the platform hard limits; hashtag counts are the
    // engagement sweet spots, not the maximums. LinkInBio marks platforms whose
    // captions are not clickable: a raw URL there is dead weight, so the caption
    // carries a bio call-to-action and the link ships separately (link.txt).
    public static readonly IReadOnlyList<PlatformFormat> All = new[]
    {
        new PlatformFormat("instagram", 1080, 1350, 2200,  8, PrefersVideo: false, TitleMaxChars: 0,   LinkInBio: true),
        new PlatformFormat("facebook",  1080, 1350, 63206, 3, PrefersVideo: false, TitleMaxChars: 0,   LinkInBio: false),
        new PlatformFormat("tiktok",    1080, 1920, 2200,  5, PrefersVideo: true,  TitleMaxChars: 0,   LinkInBio: false),
        new PlatformFormat("youtube",   1080, 1920, 5000,  3, PrefersVideo: true,  TitleMaxChars: 100, LinkInBio: false),
    };

    public static PlatformFormat Get(string platform)
        => All.FirstOrDefault(f => string.Equals(f.Platform, platform, StringComparison.OrdinalIgnoreCase))
           ?? throw new ArgumentException(
               $"Unknown platform '{platform}'. Expected one of: {string.Join(", ", All.Select(f => f.Platform))}");
}

/// <summary>
/// One platform-ready caption + title, paired with its format spec. <see cref="Link"/>
/// is the platform-stamped tracking link; on LinkInBio platforms it is NOT inside
/// the caption (the caption carries a bio call-to-action instead) — the operator
/// points the bio/Linktree at it, or drops it in the first comment.
/// </summary>
public sealed record PlatformVariant(PlatformFormat Format, string Caption, string Title, string Link);

public static class PlatformFormatter
{
    /// <summary>Call-to-action used instead of a raw URL on platforms with non-clickable captions.</summary>
    public const string LinkInBioCta = "🔗 Link in bio →";

    /// <summary>
    /// Reshapes an engine-built caption for one platform: caps the hashtag count,
    /// trims the body to the platform's caption limit (link and hashtags survive
    /// trimming untouched), stamps utm_source with the platform name so manual
    /// posts stay attributable per platform, and derives a title where needed.
    /// On LinkInBio platforms the dead in-caption URL is replaced by a bio CTA.
    /// </summary>
    public static PlatformVariant Format(string platform, string hookText, string caption)
    {
        var format = PlatformFormats.Get(platform);
        var (body, links, hashtags) = Dissect(caption);
        links = links.Select(l => RewriteUtm(l, format.Platform)).ToList();

        var keptTags = hashtags.Take(format.MaxHashtags).ToList();
        var tail = new List<string>();
        if (format.LinkInBio)
        {
            if (links.Count > 0) tail.Add(LinkInBioCta);
        }
        else
        {
            tail.AddRange(links);
        }
        if (keptTags.Count > 0) tail.Add(string.Join(" ", keptTags));

        var finalCaption = Assemble(body, tail, format.CaptionMaxChars);
        var title = format.TitleMaxChars > 0 ? Truncate(hookText, format.TitleMaxChars) : "";
        return new PlatformVariant(format, finalCaption, title, links.FirstOrDefault() ?? "");
    }

    /// <summary>
    /// Rewrites utm_source to the target platform and utm_medium to "manual" on a
    /// tracking link (the engine emits a platform-agnostic caption; the variant is
    /// what gets posted where — by hand). Every other query parameter is preserved:
    /// utm_campaign / utm_content carry hook + pillar and are what
    /// MonetizationLogger.ExtractUtms parses. Links without UTM parameters are
    /// left untouched; missing utm_source / utm_medium are appended.
    /// </summary>
    private static string RewriteUtm(string link, string platform)
    {
        if (!link.Contains("utm_", StringComparison.OrdinalIgnoreCase)) return link;
        var rewritten = ReplaceOrAppendParam(link, "utm_source", Uri.EscapeDataString(platform));
        return ReplaceOrAppendParam(rewritten, "utm_medium", "manual");
    }

    private static string ReplaceOrAppendParam(string link, string param, string value)
    {
        if (link.Contains(param + "=", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.Replace(
                link, $"({param}=)[^&]*", "${1}" + value,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return link + (link.Contains('?') ? "&" : "?") + $"{param}={value}";
    }

    /// <summary>Splits a caption into body lines, link lines, and an ordered de-duplicated hashtag list.</summary>
    private static (List<string> Body, List<string> Links, List<string> Hashtags) Dissect(string caption)
    {
        var body = new List<string>();
        var links = new List<string>();
        var hashtags = new List<string>();

        foreach (var rawLine in caption.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0) continue;

            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                links.Add(line.Trim());
                continue;
            }

            var kept = new List<string>();
            foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith('#') && token.Length > 1)
                {
                    if (!hashtags.Contains(token, StringComparer.OrdinalIgnoreCase))
                        hashtags.Add(token);
                }
                else kept.Add(token);
            }
            if (kept.Count > 0) body.Add(string.Join(" ", kept));
        }
        return (body, links, hashtags);
    }

    /// <summary>
    /// Joins body + tail (link/hashtag lines) into the final caption. When over the
    /// limit, only the body is shortened — the tail is sacred (UTM attribution).
    /// </summary>
    private static string Assemble(List<string> body, List<string> tail, int maxChars)
    {
        var bodyText = string.Join("\n", body);
        var tailText = string.Join("\n\n", tail);
        var full = tail.Count == 0 ? bodyText : $"{bodyText}\n\n{tailText}";
        if (full.Length <= maxChars) return full;

        // Budget for the body = limit minus tail and the joining blank line.
        var budget = maxChars - (tail.Count == 0 ? 0 : tailText.Length + 2);
        bodyText = Truncate(bodyText, Math.Max(0, budget));
        return bodyText.Length == 0 ? tailText : $"{bodyText}\n\n{tailText}";
    }

    /// <summary>Word-boundary truncation with a trailing ellipsis.</summary>
    private static string Truncate(string text, int maxChars)
    {
        text = text.Trim();
        if (text.Length <= maxChars) return text;
        if (maxChars <= 1) return "";
        var cut = text.LastIndexOf(' ', maxChars - 2);
        var head = cut > 0 ? text[..cut] : text[..(maxChars - 1)];
        return head.TrimEnd() + "…";
    }
}
