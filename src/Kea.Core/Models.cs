namespace Kea.Core;

/// <summary>One chapter of a comic, as advertised on its list page.</summary>
/// <param name="EpisodeNo">The site's own episode id, used to order chapters reliably.</param>
/// <param name="Url">The viewer page holding the chapter's images.</param>
/// <param name="Name">The chapter's title, unsanitised.</param>
public sealed record ChapterRef(int EpisodeNo, string Url, string Name);

/// <summary>A comic queued for download, together with the chapter range to fetch.</summary>
public sealed class ToonJob
{
    public ToonJob(WebtoonUrl url, int startChapter = 1, int? endChapter = null)
    {
        if (startChapter < 1)
            throw new ArgumentOutOfRangeException(nameof(startChapter), "The start chapter must be greater than zero.");
        if (endChapter is < 1)
            throw new ArgumentOutOfRangeException(nameof(endChapter), "The end chapter must be greater than zero.");
        if (endChapter is not null && endChapter < startChapter)
            throw new ArgumentException("The start chapter must be smaller than the end chapter.", nameof(endChapter));

        Url = url;
        StartChapter = startChapter;
        EndChapter = endChapter;
    }

    public WebtoonUrl Url { get; }

    /// <summary>First chapter to download, 1-based and inclusive.</summary>
    public int StartChapter { get; }

    /// <summary>Last chapter to download, inclusive. <see langword="null"/> means "to the end".</summary>
    public int? EndChapter { get; }

    /// <summary>The comic's slug, shown in the queue and used for its folder.</summary>
    public string Name => Url.Slug;

    /// <summary>
    /// Clamps the requested range against how many chapters actually exist.
    /// Returns the 0-based half-open range to download.
    /// </summary>
    public (int Start, int End) ResolveRange(int availableChapters)
    {
        int start = Math.Min(StartChapter - 1, availableChapters);
        int end = Math.Min(EndChapter ?? availableChapters, availableChapters);
        return (start, Math.Max(start, end));
    }
}

/// <summary>Everything the downloader needs beyond the queue itself.</summary>
public sealed class DownloadOptions
{
    /// <summary>Directory the downloads are written into. Must exist.</summary>
    public required string SavePath { get; init; }

    public SaveFormat Format { get; init; } = SaveFormat.Pdf;

    /// <summary>Give each comic its own sub-folder.</summary>
    public bool CartoonFolders { get; init; }

    /// <summary>
    /// Give each chapter its own sub-folder. Only meaningful for
    /// <see cref="SaveFormat.MultipleImages"/>; the other formats always need a scratch folder
    /// and collapse it into a single file afterwards.
    /// </summary>
    public bool ChapterFolders { get; init; } = true;

    /// <summary>
    /// How many images of a chapter to fetch at once. Kept low by default to stay polite to the
    /// site; the original fetched strictly one at a time.
    /// </summary>
    public int MaxParallelDownloads { get; init; } = 3;

    /// <summary>
    /// Ceiling on the height of stitched output, in pixels. Taller results are scaled down.
    /// </summary>
    public int MaxStitchedHeight { get; init; } = 30_000;
}

/// <summary>A progress update from a running download.</summary>
/// <param name="Status">Human readable description of the current step.</param>
/// <param name="Fraction">Overall completion between 0 and 1, or <see langword="null"/> if unknown.</param>
public readonly record struct DownloadProgress(string Status, double? Fraction);
