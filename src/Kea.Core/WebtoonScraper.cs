using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Kea.Core;

/// <summary>
/// Reads a comic's chapter list and the image URLs of a single chapter.
/// </summary>
/// <remarks>
/// The original walked the DOM by hard-coded child index
/// (<c>childNodes[j].ChildNodes[1].ChildNodes[3].ChildNodes[0]</c>), which counts whitespace text
/// nodes and breaks on any markup reflow. This uses XPath with fallbacks instead, which is both
/// portable and far less brittle.
/// </remarks>
public sealed partial class WebtoonScraper
{
    private readonly WebtoonClient _client;

    /// <summary>Safety net so a pagination change cannot spin forever.</summary>
    public int MaxListPages { get; init; } = 200;

    public WebtoonScraper(WebtoonClient client) => _client = client;

    [GeneratedRegex(@"[?&]episode_no=(?<no>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeNoPattern();

    /// <summary>
    /// Returns every chapter of a comic, oldest first.
    /// </summary>
    /// <remarks>
    /// Chapters are keyed and sorted by the site's own <c>episode_no</c> rather than by reversing
    /// the scrape order. That removes the original's sentinel-based loop, which compared each page's
    /// first link against the previous one and silently dropped a chapter whenever it tripped.
    /// </remarks>
    public async Task<IReadOnlyList<ChapterRef>> GetChaptersAsync(
        WebtoonUrl comic,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Dictionary<int, ChapterRef> byEpisode = [];

        for (int page = 1; page <= MaxListPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DownloadProgress($"scoping tab {page} of {comic.Slug}", null));

            string html;
            try
            {
                html = await _client.GetHtmlAsync(comic.PageUrl(page), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                break;
            }

            List<ChapterRef> found = ParseChapterList(html);
            if (found.Count == 0) break;

            // Past the last page webtoons.com re-serves the final page, so stop once a page
            // contributes nothing new rather than trusting the pager markup.
            int added = 0;
            foreach (ChapterRef chapter in found)
            {
                if (byEpisode.TryAdd(chapter.EpisodeNo, chapter)) added++;
            }

            if (added == 0) break;
        }

        if (byEpisode.Count == 0)
        {
            throw new InvalidOperationException(
                $"No chapters found for {comic.Slug}. The link may be wrong, the comic may be " +
                "region locked, or webtoons.com may have changed its page layout.");
        }

        return byEpisode.Values.OrderBy(c => c.EpisodeNo).ToList();
    }

    /// <summary>Extracts the chapters listed on one page of HTML. Exposed for testing.</summary>
    public static List<ChapterRef> ParseChapterList(string html)
    {
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        HtmlNodeCollection? anchors =
            doc.DocumentNode.SelectNodes("//ul[@id='_listUl']//li//a[@href]")
            ?? doc.DocumentNode.SelectNodes("//a[contains(@href, 'episode_no=')]");

        List<ChapterRef> chapters = [];
        if (anchors is null) return chapters;

        HashSet<int> seen = [];
        foreach (HtmlNode anchor in anchors)
        {
            string href = HtmlEntity.DeEntitize(anchor.GetAttributeValue("href", string.Empty)).Trim();
            if (href.Length == 0) continue;

            Match match = EpisodeNoPattern().Match(href);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups["no"].Value, out int episodeNo)) continue;
            if (!seen.Add(episodeNo)) continue;

            chapters.Add(new ChapterRef(episodeNo, href, ExtractChapterName(anchor, episodeNo)));
        }

        return chapters;
    }

    private static string ExtractChapterName(HtmlNode anchor, int episodeNo)
    {
        // The title sits in a span with a "subj" class; fall back to the anchor's own text.
        HtmlNode? subject =
            anchor.SelectSingleNode(".//span[contains(@class, 'subj')]")
            ?? anchor.SelectSingleNode(".//span[contains(@class, 'title')]");

        string name = HtmlEntity.DeEntitize(subject?.InnerText ?? anchor.InnerText ?? string.Empty);
        name = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return name.Length > 0 ? name : $"Episode {episodeNo}";
    }

    /// <summary>Returns the image URLs making up one chapter, in reading order.</summary>
    public async Task<IReadOnlyList<Uri>> GetChapterImagesAsync(
        ChapterRef chapter, CancellationToken cancellationToken = default)
    {
        string html = await _client.GetHtmlAsync(new Uri(chapter.Url), cancellationToken).ConfigureAwait(false);
        List<Uri> images = ParseChapterImages(html);

        if (images.Count == 0)
        {
            throw new InvalidOperationException(
                $"No images found in chapter '{chapter.Name}' ({chapter.Url}).");
        }

        return images;
    }

    /// <summary>Extracts a chapter's image URLs from viewer HTML. Exposed for testing.</summary>
    public static List<Uri> ParseChapterImages(string html)
    {
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        HtmlNodeCollection? nodes =
            doc.DocumentNode.SelectNodes("//div[@id='_imageList']//img")
            ?? doc.DocumentNode.SelectNodes("//img[@data-url]");

        List<Uri> images = [];
        if (nodes is null) return images;

        foreach (HtmlNode node in nodes)
        {
            // Images are lazy loaded: the real source lives in data-url, with src a placeholder.
            string raw = node.GetAttributeValue("data-url", string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) raw = node.GetAttributeValue("src", string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) continue;

            raw = HtmlEntity.DeEntitize(raw).Trim();
            if (raw.StartsWith("//", StringComparison.Ordinal)) raw = "https:" + raw;
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            if (Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                images.Add(uri);
            }
        }

        return images;
    }
}
