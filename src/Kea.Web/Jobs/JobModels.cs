using Kea.Core;

namespace Kea.Web.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>What the browser posts to start a download.</summary>
public sealed class JobRequest
{
    /// <summary>Comic list links, one per comic.</summary>
    public List<string> Urls { get; set; } = [];

    /// <summary>Save format token, e.g. "pdf" or "cbz".</summary>
    public string Format { get; set; } = "pdf";

    public int Start { get; set; } = 1;

    /// <summary>Last chapter, or null for "to the end".</summary>
    public int? End { get; set; }

    public bool ComicFolders { get; set; } = true;

    public bool ChapterFolders { get; set; } = true;
}

/// <summary>A chapter outcome, flattened for JSON.</summary>
public sealed record ChapterOutcome(string Comic, int ChapterNumber, string Name, string? File, string? Error);

/// <summary>An immutable view of a job, safe to serialise while the job is still running.</summary>
public sealed record JobView(
    string Id,
    JobStatus Status,
    string StatusText,
    double Progress,
    IReadOnlyList<string> Comics,
    string Format,
    int Start,
    int? End,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int Succeeded,
    int Failed,
    string? Error,
    IReadOnlyList<ChapterOutcome> Chapters);

/// <summary>
/// The mutable state of one queued or running job.
/// </summary>
/// <remarks>
/// Touched by the background runner and by request threads at the same time, so every read and
/// write goes through the lock and callers only ever see an immutable <see cref="JobView"/>.
/// </remarks>
internal sealed class JobRecord
{
    private readonly object _gate = new();
    private readonly List<ChapterOutcome> _chapters = [];

    private JobStatus _status = JobStatus.Queued;
    private string _statusText = "queued";
    private double _progress;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private string? _error;

    public JobRecord(IReadOnlyList<ToonJob> jobs, DownloadOptions options, SaveFormat format, int start, int? end)
    {
        Jobs = jobs;
        Options = options;
        Format = format;
        Start = start;
        End = end;
        Comics = jobs.Select(j => j.Name).ToList();
    }

    public Guid Id { get; } = Guid.NewGuid();

    public IReadOnlyList<ToonJob> Jobs { get; }

    public DownloadOptions Options { get; }

    public SaveFormat Format { get; }

    public int Start { get; }

    public int? End { get; }

    public IReadOnlyList<string> Comics { get; }

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    public CancellationTokenSource Cancellation { get; } = new();

    public JobStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public void MarkRunning()
    {
        lock (_gate)
        {
            _status = JobStatus.Running;
            _statusText = "starting";
            _startedAt = DateTimeOffset.UtcNow;
        }
    }

    public void ReportProgress(DownloadProgress update)
    {
        lock (_gate)
        {
            _statusText = update.Status;
            if (update.Fraction is { } fraction) _progress = Math.Clamp(fraction, 0, 1);
        }
    }

    public void Complete(DownloadReport report)
    {
        lock (_gate)
        {
            _chapters.Clear();
            _chapters.AddRange(report.Chapters.Select(c => new ChapterOutcome(
                c.Comic, c.ChapterNumber, c.Name, c.OutputPath, c.Error)));

            _status = report.FailedCount == 0 ? JobStatus.Completed : JobStatus.Failed;
            _statusText = report.FailedCount == 0
                ? $"done, {report.SucceededCount} chapter(s) saved"
                : $"done with errors: {report.SucceededCount} saved, {report.FailedCount} failed";
            _progress = 1;
            _finishedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Fail(string error)
    {
        lock (_gate)
        {
            _status = JobStatus.Failed;
            _statusText = "failed";
            _error = error;
            _finishedAt = DateTimeOffset.UtcNow;
        }
    }

    public void CancelledByUser()
    {
        lock (_gate)
        {
            _status = JobStatus.Cancelled;
            _statusText = "cancelled";
            _finishedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Takes a consistent snapshot for the API and the event stream.</summary>
    public JobView ToView(string libraryRoot)
    {
        lock (_gate)
        {
            // Absolute server paths are an implementation detail, and leaking them would tell a
            // caller about the host's filesystem. Report library-relative paths instead.
            List<ChapterOutcome> chapters = _chapters
                .Select(c => c with { File = ToRelative(c.File, libraryRoot) })
                .ToList();

            return new JobView(
                Id.ToString(),
                _status,
                _statusText,
                _progress,
                Comics,
                Format.CliName(),
                Start,
                End,
                CreatedAt,
                _startedAt,
                _finishedAt,
                chapters.Count(c => c.Error is null),
                chapters.Count(c => c.Error is not null),
                _error,
                chapters);
        }
    }

    private static string? ToRelative(string? path, string root)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            string relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal)
                ? null
                : relative.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
