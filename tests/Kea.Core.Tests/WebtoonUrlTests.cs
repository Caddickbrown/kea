using Xunit;

namespace Kea.Core.Tests;

public class WebtoonUrlTests
{
    private const string ListLink = "https://www.webtoons.com/en/action/hero-killer/list?title_no=2745";

    [Fact]
    public void TryParse_AcceptsAListLink()
    {
        Assert.True(WebtoonUrl.TryParse(ListLink, out WebtoonUrl? url));
        Assert.Equal("en", url.Language);
        Assert.Equal("action", url.Genre);
        Assert.Equal("hero-killer", url.Slug);
        Assert.Equal(2745, url.TitleNo);
    }

    [Fact]
    public void TryParse_RejectsASingleEpisodeLink()
    {
        // The README warns against these; the original silently skipped them without saying why.
        Assert.False(WebtoonUrl.TryParse(
            "https://www.webtoons.com/en/action/hero-killer/episode-1/viewer?title_no=2745&episode_no=1",
            out _,
            out string? error));

        Assert.Contains("list", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_RewritesTheMobileHost()
    {
        // The original rejected m.webtoons.com outright. The two hosts differ only by subdomain.
        Assert.True(WebtoonUrl.TryParse(
            "https://m.webtoons.com/en/action/hero-killer/list?title_no=2745", out WebtoonUrl? url));

        Assert.Equal("www.webtoons.com", url.ListUrl.Host);
    }

    [Fact]
    public void TryParse_StripsExtraQueryParameters()
    {
        Assert.True(WebtoonUrl.TryParse(ListLink + "&page=4&from=search", out WebtoonUrl? url));
        Assert.Equal(ListLink, url.ListUrl.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://www.webtoons.com/en/action/x/list?title_no=1")]
    [InlineData("https://example.com/en/action/x/list?title_no=1")]
    [InlineData("https://www.webtoons.com/en/action/x/list")]
    public void TryParse_RejectsBadInput(string input)
        => Assert.False(WebtoonUrl.TryParse(input, out _));

    [Fact]
    public void PageUrl_AppendsThePageParameter()
    {
        Assert.True(WebtoonUrl.TryParse(ListLink, out WebtoonUrl? url));
        Assert.Equal(ListLink + "&page=3", url.PageUrl(3).ToString());
    }

    [Theory]
    [InlineData(1, null, 0, 10)]
    [InlineData(3, null, 2, 10)]
    [InlineData(2, 5, 1, 5)]
    // An end chapter past the end of the comic is clamped rather than throwing.
    [InlineData(1, 999, 0, 10)]
    // A start chapter past the end yields an empty range, not a negative one.
    [InlineData(50, null, 10, 10)]
    public void ResolveRange_ClampsToWhatExists(int start, int? end, int expectedStart, int expectedEnd)
    {
        Assert.True(WebtoonUrl.TryParse(ListLink, out WebtoonUrl? url));
        ToonJob job = new(url, start, end);

        Assert.Equal((expectedStart, expectedEnd), job.ResolveRange(10));
    }

    [Fact]
    public void ToonJob_RejectsAnInvertedRange()
    {
        Assert.True(WebtoonUrl.TryParse(ListLink, out WebtoonUrl? url));
        Assert.Throws<ArgumentException>(() => new ToonJob(url, startChapter: 5, endChapter: 2));
    }

    [Fact]
    public void ToonJob_RejectsAZeroStartChapter()
    {
        Assert.True(WebtoonUrl.TryParse(ListLink, out WebtoonUrl? url));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToonJob(url, startChapter: 0));
    }
}
