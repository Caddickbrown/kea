using System.Net;

namespace Kea.Core;

/// <summary>
/// The HTTP plumbing shared by the scraper and the image downloader.
/// </summary>
/// <remarks>
/// The original used <see cref="WebClient"/>, which is obsolete and, more importantly, mutated a
/// shared header collection per request — repeatedly adding a Referer header rather than replacing
/// it. This uses <see cref="HttpClient"/> with per-request headers instead, which is both correct
/// and the only supported option on .NET 8.
/// </remarks>
public sealed class WebtoonClient : IDisposable
{
    // Webtoons serves an interstitial without this consent cookie.
    private const string ConsentCookie = "pagGDPR=true; needGDPR=false; needCCPA=false; needCOPPA=false";

    private const string DefaultUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public WebtoonClient(HttpClient? http = null)
    {
        if (http is not null)
        {
            _http = http;
            _ownsClient = false;
        }
        else
        {
            // UseProxy/DefaultProxy keeps the behaviour of the original, which attached
            // WebRequest.DefaultWebProxy so corporate proxies kept working.
            SocketsHttpHandler handler = new()
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseProxy = true,
                Proxy = HttpClient.DefaultProxy,
                AllowAutoRedirect = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };

            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
            _ownsClient = true;
        }

        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
    }

    public async Task<string> GetHtmlAsync(Uri url, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", ConsentCookie);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads one image. The <paramref name="referer"/> is required: the image CDN rejects
    /// requests that do not come from a viewer page.
    /// </summary>
    public async Task<(byte[] Bytes, string? ContentType)> GetImageAsync(
        Uri url, string referer, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", ConsentCookie);
        request.Headers.TryAddWithoutValidation("Referer", referer);
        request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/*,*/*;q=0.8");

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return (bytes, response.Content.Headers.ContentType?.MediaType);
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
