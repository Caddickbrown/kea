using Kea.Core;
using Microsoft.Extensions.Options;

namespace Kea.Web.Jobs;

/// <summary>
/// Pulls jobs off the queue and runs them, one worker per allowed concurrent job.
/// </summary>
/// <remarks>
/// Downloads take minutes, so they cannot run inside a request. This is the piece the desktop
/// builds did not need: the browser posts a job, gets an id back immediately, and watches progress
/// over the event stream.
/// </remarks>
public sealed class JobRunner : BackgroundService
{
    private readonly JobQueue _queue;
    private readonly KeaOptions _options;
    private readonly ILogger<JobRunner> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public JobRunner(
        JobQueue queue,
        IOptions<KeaOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<JobRunner> logger)
    {
        _queue = queue;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IEnumerable<Task> workers = Enumerable
            .Range(0, _options.MaxConcurrentJobs)
            .Select(i => WorkerLoopAsync(i, stoppingToken));

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task WorkerLoopAsync(int worker, CancellationToken stoppingToken)
    {
        await foreach (Guid id in _queue.Pending.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!_queue.TryGetRecord(id, out JobRecord? record) || record is null) continue;

            try
            {
                await RunAsync(record, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A worker must never die: one bad job would otherwise stall the queue for good.
                _logger.LogError(ex, "Worker {Worker} failed on job {JobId}", worker, id);
                record.Fail(ex.Message);
                _queue.Publish("updated", record);
            }
        }
    }

    private async Task RunAsync(JobRecord record, CancellationToken stoppingToken)
    {
        // Cancelled while it sat in the queue.
        if (record.Cancellation.IsCancellationRequested)
        {
            record.CancelledByUser();
            _queue.Publish("updated", record);
            return;
        }

        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, record.Cancellation.Token);

        record.MarkRunning();
        _queue.Publish("updated", record);

        // Throttled so the event stream carries progress without flooding it: each image reports,
        // but the browser only needs a few updates a second.
        DateTimeOffset lastPublish = DateTimeOffset.MinValue;
        Progress<DownloadProgress> progress = new(update =>
        {
            record.ReportProgress(update);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - lastPublish < TimeSpan.FromMilliseconds(400)) return;

            lastPublish = now;
            _queue.Publish("progress", record);
        });

        try
        {
            using WebtoonClient client = new(_httpClientFactory.CreateClient("webtoons"));
            KeaDownloader downloader = new(client);

            DownloadReport report = await downloader
                .DownloadAsync(record.Jobs, record.Options, progress, linked.Token)
                .ConfigureAwait(false);

            record.Complete(report);
        }
        catch (OperationCanceledException) when (record.Cancellation.IsCancellationRequested)
        {
            record.CancelledByUser();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server is shutting down; leave the job cancelled rather than failed.
            record.CancelledByUser();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed", record.Id);
            record.Fail(ex.Message);
        }
        finally
        {
            _queue.Publish("updated", record);
        }
    }
}
