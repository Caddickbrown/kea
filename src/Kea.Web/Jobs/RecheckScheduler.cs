using Kea.Web.Library;

namespace Kea.Web.Jobs;

/// <summary>
/// Runs once an hour and queues a recheck job for any comic whose scheduled interval has elapsed.
/// </summary>
public sealed class RecheckScheduler : BackgroundService
{
    private readonly LibraryService _library;
    private readonly JobQueue _queue;
    private readonly ILogger<RecheckScheduler> _logger;

    public RecheckScheduler(LibraryService library, JobQueue queue, ILogger<RecheckScheduler> logger)
    {
        _library = library;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger the first run by a few minutes so it doesn't fire the instant the server starts.
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { RunChecks(); }
            catch (Exception ex) { _logger.LogError(ex, "Scheduled recheck sweep failed."); }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
        }
    }

    private void RunChecks()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach ((string relativePath, KeaMeta meta) in _library.GetScheduledComics())
        {
            DateTimeOffset lastCheck = meta.LastCheckedAt ?? DateTimeOffset.MinValue;
            TimeSpan interval = TimeSpan.FromDays(meta.RecheckIntervalDays);

            if (now - lastCheck < interval) continue;

            try
            {
                _queue.Submit(new JobRequest
                {
                    Urls = [meta.Url],
                    Format = meta.Format,
                    Start = meta.LastChapter + 1,
                    End = null,
                    ComicFolders = meta.ComicFolders,
                    ChapterFolders = meta.ChapterFolders,
                });

                // Stamp the check time so we don't immediately re-queue on the next sweep.
                meta.LastCheckedAt = now;
                _library.WriteMetaFile(relativePath, meta);

                _logger.LogInformation(
                    "Scheduled recheck queued for '{Comic}' (interval {Days}d, last check {LastCheck:u}).",
                    relativePath, meta.RecheckIntervalDays, lastCheck);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not queue scheduled recheck for '{Comic}'.", relativePath);
            }
        }
    }
}
