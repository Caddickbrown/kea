using Xunit;

namespace Kea.Core.Tests;

/// <summary>
/// Parser tests run against hand-written markup that mimics the shape the scraper relies on:
/// a <c>_listUl</c> list of episode links, and an <c>_imageList</c> of lazy-loaded images.
/// </summary>
public class WebtoonScraperTests
{
    private const string ListHtml = """
        <html><body>
        <ul id="_listUl">
          <li id="episode_3">
            <a href="https://www.webtoons.com/en/action/x/ep-3/viewer?title_no=1&amp;episode_no=3">
              <span class="subj"><span>Episode 3</span></span>
            </a>
          </li>
          <li id="episode_2">
            <a href="https://www.webtoons.com/en/action/x/ep-2/viewer?title_no=1&amp;episode_no=2">
              <span class="subj"><span>Episode 2</span></span>
            </a>
          </li>
          <li id="episode_1">
            <a href="https://www.webtoons.com/en/action/x/ep-1/viewer?title_no=1&amp;episode_no=1">
              <span class="subj"><span>Episode 1</span></span>
            </a>
          </li>
        </ul>
        </body></html>
        """;

    [Fact]
    public void ParseChapterList_ReadsLinksAndTitles()
    {
        List<ChapterRef> chapters = WebtoonScraper.ParseChapterList(ListHtml);

        Assert.Equal(3, chapters.Count);
        Assert.Equal([3, 2, 1], chapters.Select(c => c.EpisodeNo));
        Assert.Equal("Episode 3", chapters[0].Name);
        Assert.Contains("episode_no=3", chapters[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseChapterList_IgnoresMarkupItRecognisesButCannotUse()
    {
        // Whitespace-only text nodes and non-episode links used to shift the original's
        // hard-coded child indices and throw.
        const string noisy = """
            <ul id="_listUl">
              <li><a href="/en/action/x/list?title_no=1">back to list</a></li>
              <li>
                <a href="/en/action/x/ep-9/viewer?title_no=1&amp;episode_no=9">
                  <span class="subj">Nine</span>
                </a>
              </li>
            </ul>
            """;

        List<ChapterRef> chapters = WebtoonScraper.ParseChapterList(noisy);

        ChapterRef only = Assert.Single(chapters);
        Assert.Equal(9, only.EpisodeNo);
        Assert.Equal("Nine", only.Name);
    }

    [Fact]
    public void ParseChapterList_DeduplicatesRepeatedEpisodes()
    {
        List<ChapterRef> chapters = WebtoonScraper.ParseChapterList(ListHtml + ListHtml);
        Assert.Equal(3, chapters.Count);
    }

    [Fact]
    public void ParseChapterList_ReturnsEmptyForUnrelatedMarkup()
        => Assert.Empty(WebtoonScraper.ParseChapterList("<html><body><p>nothing here</p></body></html>"));

    [Fact]
    public void ParseChapterList_FallsBackToTheAnchorTextWhenNoTitleSpanExists()
    {
        const string html = """
            <ul id="_listUl">
              <li><a href="/v?title_no=1&amp;episode_no=4">  Plain   Title  </a></li>
            </ul>
            """;

        Assert.Equal("Plain Title", Assert.Single(WebtoonScraper.ParseChapterList(html)).Name);
    }

    [Fact]
    public void ParseChapterImages_PrefersTheLazyLoadedDataUrl()
    {
        const string html = """
            <div id="_imageList">
              <img src="https://cdn.example.com/placeholder.png" data-url="https://cdn.example.com/1.jpg">
              <img src="https://cdn.example.com/placeholder.png" data-url="https://cdn.example.com/2.jpg">
            </div>
            """;

        List<Uri> images = WebtoonScraper.ParseChapterImages(html);

        Assert.Equal(2, images.Count);
        Assert.Equal("https://cdn.example.com/1.jpg", images[0].ToString());
    }

    [Fact]
    public void ParseChapterImages_HandlesProtocolRelativeUrlsAndSkipsInlineData()
    {
        const string html = """
            <div id="_imageList">
              <img data-url="//cdn.example.com/a.jpg">
              <img data-url="data:image/gif;base64,R0lGOD">
              <img src="https://cdn.example.com/b.jpg">
            </div>
            """;

        List<Uri> images = WebtoonScraper.ParseChapterImages(html);

        Assert.Equal(
            ["https://cdn.example.com/a.jpg", "https://cdn.example.com/b.jpg"],
            images.Select(i => i.ToString()));
    }

    [Fact]
    public void ParseChapterImages_ReturnsEmptyWhenThereIsNoImageList()
        => Assert.Empty(WebtoonScraper.ParseChapterImages("<html><body></body></html>"));
}
