namespace Kea.Web;

/// <summary>Configuration for the hosted service, bound from the <c>Kea</c> section.</summary>
public sealed class KeaOptions
{
    public const string SectionName = "Kea";

    /// <summary>
    /// Directory downloads are written to and served from. Everything the web app can read or
    /// delete lives under here.
    /// </summary>
    public string LibraryPath { get; set; } = "library";

    /// <summary>
    /// Shared secret callers must present. When empty, the service only answers requests from
    /// loopback — see <see cref="AccessControl"/> for why.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Set to true only to run with no token on a network interface, e.g. behind a reverse proxy
    /// that already authenticates. This is an explicit, deliberate opt-out.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>How many jobs run at once. One keeps the load on webtoons.com predictable.</summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>Upper bound on the backlog, so the queue cannot grow without limit.</summary>
    public int MaxQueuedJobs { get; set; } = 50;

    /// <summary>Comics accepted in a single submission.</summary>
    public int MaxUrlsPerJob { get; set; } = 25;

    /// <summary>Images fetched at once within a chapter.</summary>
    public int MaxParallelDownloads { get; set; } = 3;

    /// <summary>Finished jobs kept in the history list.</summary>
    public int JobHistoryLimit { get; set; } = 100;

    /// <summary>Height limit for stitched single-image output.</summary>
    public int MaxStitchedHeight { get; set; } = 30_000;

    /// <summary>Normalises and validates the options, throwing on anything unusable.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(LibraryPath))
            throw new InvalidOperationException("Kea:LibraryPath must be set.");

        LibraryPath = Path.GetFullPath(LibraryPath);

        MaxConcurrentJobs = Math.Clamp(MaxConcurrentJobs, 1, 8);
        MaxQueuedJobs = Math.Clamp(MaxQueuedJobs, 1, 1000);
        MaxUrlsPerJob = Math.Clamp(MaxUrlsPerJob, 1, 200);
        MaxParallelDownloads = Math.Clamp(MaxParallelDownloads, 1, 16);
        JobHistoryLimit = Math.Clamp(JobHistoryLimit, 10, 10_000);
        MaxStitchedHeight = Math.Clamp(MaxStitchedHeight, 1_000, 200_000);
    }
}
