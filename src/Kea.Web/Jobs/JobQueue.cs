using System.Collections.Concurrent;
using System.Threading.Channels;
using Kea.Core;
using Microsoft.Extensions.Options;

namespace Kea.Web.Jobs;

/// <summary>Raised when a job is created or changes state, so the browser can be told.</summary>
public sealed record JobEvent(string Type, JobView Job);

/// <summary>
/// Holds the queue, the running jobs and the finished history.
/// </summary>
/// <remarks>
/// State lives in memory only: a restart loses the job list, but never the downloads themselves,
/// which are already on disk. That keeps the service free of a database for what is a single-user
/// tool.
/// </remarks>
public sealed class JobQueue
{
    private readonly ConcurrentDictionary<Guid, JobRecord> _jobs = new();
    private readonly Channel<Guid> _pending;
    private readonly KeaOptions _options;
    private readonly JobEvents _events;

    public JobQueue(IOptions<KeaOptions> options, JobEvents events)
    {
        _options = options.Value;
        _events = events;

        // A bounded channel is the backpressure: submissions are refused rather than piling up.
        _pending = Channel.CreateBounded<Guid>(new BoundedChannelOptions(_options.MaxQueuedJobs)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    internal ChannelReader<Guid> Pending => _pending.Reader;

    /// <summary>Number of jobs waiting or running.</summary>
    public int ActiveCount => _jobs.Values.Count(j => j.Status is JobStatus.Queued or JobStatus.Running);

    /// <summary>
    /// Validates a submission and queues it.
    /// </summary>
    /// <exception cref="ArgumentException">The request cannot be turned into a job.</exception>
    /// <exception cref="InvalidOperationException">The queue is full.</exception>
    public JobView Submit(JobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Urls.Count == 0)
            throw new ArgumentException("Add at least one comic link.");

        if (request.Urls.Count > _options.MaxUrlsPerJob)
            throw new ArgumentException($"At most {_options.MaxUrlsPerJob} links per job.");

        if (!SaveFormats.TryParse(request.Format, out SaveFormat format))
            throw new ArgumentException($"Unknown format '{request.Format}'.");

        if (request.Start < 1)
            throw new ArgumentException("The start chapter must be greater than zero.");

        if (request.End is { } end && end < request.Start)
            throw new ArgumentException("The start chapter must be smaller than the end chapter.");

        List<ToonJob> jobs = [];
        HashSet<int> seen = [];
        List<string> rejected = [];

        foreach (string raw in request.Urls)
        {
            if (!WebtoonUrl.TryParse(raw, out WebtoonUrl? url, out string? error))
            {
                rejected.Add($"{Truncate(raw)} — {error}");
                continue;
            }

            if (!seen.Add(url.TitleNo)) continue;
            jobs.Add(new ToonJob(url, request.Start, request.End));
        }

        if (jobs.Count == 0)
        {
            throw new ArgumentException(rejected.Count > 0
                ? "No usable links. " + string.Join("; ", rejected)
                : "No usable links.");
        }

        DownloadOptions options = new()
        {
            SavePath = _options.LibraryPath,
            Format = format,
            CartoonFolders = request.ComicFolders,
            ChapterFolders = request.ChapterFolders,
            MaxParallelDownloads = _options.MaxParallelDownloads,
            MaxStitchedHeight = _options.MaxStitchedHeight,
        };

        JobRecord record = new(jobs, options, format, request.Start, request.End);
        _jobs[record.Id] = record;

        if (!_pending.Writer.TryWrite(record.Id))
        {
            _jobs.TryRemove(record.Id, out _);
            throw new InvalidOperationException("The queue is full. Try again once something finishes.");
        }

        Trim();

        JobView view = record.ToView(_options.LibraryPath);
        _events.Publish(new JobEvent("created", view));
        return view;
    }

    public IReadOnlyList<JobView> List()
        => _jobs.Values
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => j.ToView(_options.LibraryPath))
            .ToList();

    public JobView? Get(Guid id)
        => _jobs.TryGetValue(id, out JobRecord? record) ? record.ToView(_options.LibraryPath) : null;

    /// <summary>Asks a queued or running job to stop. Finished jobs are left alone.</summary>
    public bool Cancel(Guid id)
    {
        if (!_jobs.TryGetValue(id, out JobRecord? record)) return false;
        if (record.Status is not (JobStatus.Queued or JobStatus.Running)) return false;

        record.Cancellation.Cancel();
        return true;
    }

    /// <summary>Removes a finished job from the history. Running jobs must be cancelled first.</summary>
    public bool Remove(Guid id)
    {
        if (!_jobs.TryGetValue(id, out JobRecord? record)) return false;
        if (record.Status is JobStatus.Queued or JobStatus.Running) return false;

        if (!_jobs.TryRemove(id, out _)) return false;

        record.Cancellation.Dispose();
        _events.Publish(new JobEvent("removed", record.ToView(_options.LibraryPath)));
        return true;
    }

    internal bool TryGetRecord(Guid id, out JobRecord? record) => _jobs.TryGetValue(id, out record);

    internal void Publish(string type, JobRecord record)
        => _events.Publish(new JobEvent(type, record.ToView(_options.LibraryPath)));

    /// <summary>Drops the oldest finished jobs once the history grows past its limit.</summary>
    private void Trim()
    {
        List<JobRecord> finished = _jobs.Values
            .Where(j => j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
            .OrderByDescending(j => j.CreatedAt)
            .ToList();

        foreach (JobRecord old in finished.Skip(_options.JobHistoryLimit))
        {
            if (_jobs.TryRemove(old.Id, out _)) old.Cancellation.Dispose();
        }
    }

    private static string Truncate(string value) => value.Length <= 80 ? value : value[..77] + "...";
}
