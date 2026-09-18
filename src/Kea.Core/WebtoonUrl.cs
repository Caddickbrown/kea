using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Kea.Core;

/// <summary>
/// A validated link to a comic's chapter list, e.g.
/// <c>https://www.webtoons.com/en/action/some-comic/list?title_no=1234</c>.
/// </summary>
public sealed partial class WebtoonUrl
{
    private WebtoonUrl(Uri listUrl, string language, string genre, string slug, int titleNo)
    {
        ListUrl = listUrl;
        Language = language;
        Genre = genre;
        Slug = slug;
        TitleNo = titleNo;
    }

    /// <summary>The canonical list URL, with tracking and page parameters stripped.</summary>
    public Uri ListUrl { get; }

    public string Language { get; }

    public string Genre { get; }

    /// <summary>The comic's URL slug, used as its display name and folder name.</summary>
    public string Slug { get; }

    public int TitleNo { get; }

    [GeneratedRegex(@"^/(?<lang>[^/]+)/(?<genre>[^/]+)/(?<slug>[^/]+)/list/?$", RegexOptions.IgnoreCase)]
    private static partial Regex ListPathPattern();

    /// <summary>Builds the URL for one page of the chapter list.</summary>
    public Uri PageUrl(int page) => new($"{ListUrl}&page={page}");

    public static bool TryParse(string? input, [NotNullWhen(true)] out WebtoonUrl? result)
        => TryParse(input, out result, out _);

    /// <summary>
    /// Parses a comic list link. Unlike the original, which required the exact host
    /// <c>www.webtoons.com</c> and rejected mobile links outright, this accepts the
    /// <c>m.webtoons.com</c> mobile host and rewrites it, since the two differ only by subdomain.
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out WebtoonUrl? result, out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "the line is empty";
            return false;
        }

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out Uri? uri))
        {
            error = "not a valid URL";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "only http and https links are supported";
            return false;
        }

        string host = uri.Host.ToLowerInvariant();
        if (host != "www.webtoons.com" && host != "webtoons.com" && host != "m.webtoons.com")
        {
            error = "not a webtoons.com link";
            return false;
        }

        Match match = ListPathPattern().Match(uri.AbsolutePath);
        if (!match.Success)
        {
            error = "not a chapter list link — use the comic's 'list' page, not a single episode";
            return false;
        }

        int titleNo = 0;
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length == 2
                && string.Equals(parts[0], "title_no", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(parts[1], out int parsed))
            {
                titleNo = parsed;
                break;
            }
        }

        if (titleNo <= 0)
        {
            error = "the link is missing a title_no parameter";
            return false;
        }

        string language = match.Groups["lang"].Value;
        string genre = match.Groups["genre"].Value;
        string slug = match.Groups["slug"].Value;

        // Always talk to the desktop site: the mobile host serves a different layout.
        Uri canonical = new($"https://www.webtoons.com/{language}/{genre}/{slug}/list?title_no={titleNo}");
        result = new WebtoonUrl(canonical, language, genre, slug, titleNo);
        return true;
    }

    public override string ToString() => ListUrl.ToString();
}
