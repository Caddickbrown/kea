using System.Collections.Concurrent;

namespace Kea.Core;

/// <summary>Outcome of a single chapter.</summary>
/// <param name="ChapterNumber">1-based position of the chapter in the comic's list.</param>
public sealed record ChapterResult(string Comic, int ChapterNumber, string Name, string? OutputPath, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>Outcome of a whole queue.</summary>
public sealed record DownloadReport(IReadOnlyList<ChapterResult> Chapters)
{
    public int SucceededCount => Chapters.Count(c => c.Succeeded);

    public int FailedCount => Chapters.Count(c => !c.Succeeded);
}

/// <summary>
/// Drives the whole job: list chapters, fetch images, package them.
/// </summary>
/// <remarks>
/// All UI concerns are deliberately absent so the same logic backs both the Avalonia GUI and the
/// command line front-end. The original kept this logic inside the WinForms form, which is what
/// tied it to Windows in the first place.
/// </remarks>
public sealed class KeaDownloader
{
    private readonly WebtoonClient _client;
    private readonly WebtoonScraper _scraper;

    public KeaDownloader(WebtoonClient client)
    {
        _client = client;
        _scraper = new WebtoonScraper(client);
    }

    /// <summary>Keep going when a chapter fails rather than abandoning the queue.</summary>
    public bool ContinueOnError { get; init; } = true;

    public async Task<DownloadReport> DownloadAsync(
        IReadOnlyList<ToonJob> queue,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.SavePath))
            throw new ArgumentException("Please select a directory for saving.", nameof(options));

        Directory.CreateDirectory(options.SavePath);

        List<ChapterResult> results = [];

        // Resolve every comic's chapter list up front so overall progress is meaningful.
        List<(ToonJob Job, IReadOnlyList<ChapterRef> Chapters)> resolved = [];
        foreach (ToonJob job in queue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                IReadOnlyList<ChapterRef> chapters =
                    await _scraper.GetChaptersAsync(job.Url, progress, cancellationToken).ConfigureAwait(false);
                resolved.Add((job, chapters));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ContinueOnError)
            {
                results.Add(new ChapterResult(job.Name, 0, "(chapter list)", null, ex.Message));
            }
        }

        int totalChapters = resolved.Sum(r =>
        {
            (int start, int end) = r.Job.ResolveRange(r.Chapters.Count);
            return end - start;
        });

        int completedChapters = 0;

        foreach ((ToonJob job, IReadOnlyList<ChapterRef> chapters) in resolved)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string comicName = FileNaming.Sanitize(job.Name, "comic");
            string comicDirectory = options.CartoonFolders
                ? Path.Combine(options.SavePath, comicName)
                : options.SavePath;
            Directory.CreateDirectory(comicDirectory);

            (int start, int end) = job.ResolveRange(chapters.Count);

            for (int i = start; i < end; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ChapterRef chapter = chapters[i];
                int chapterNumber = i + 1;
                double chapterBase = totalChapters == 0 ? 0 : (double)completedChapters / totalChapters;
                double chapterSpan = totalChapters == 0 ? 0 : 1.0 / totalChapters;

                try
                {
                    string? output = await DownloadChapterAsync(
                        job, chapter, chapterNumber, comicName, comicDirectory, options,
                        progress, chapterBase, chapterSpan, cancellationToken).ConfigureAwait(false);

                    results.Add(new ChapterResult(job.Name, chapterNumber, chapter.Name, output, null));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results.Add(new ChapterResult(job.Name, chapterNumber, chapter.Name, null, ex.Message));
                    if (!ContinueOnError) throw;
                }

                completedChapters++;
                progress?.Report(new DownloadProgress(
                    $"finished chapter {chapterNumber} of \"{job.Name}\"",
                    totalChapters == 0 ? null : (double)completedChapters / totalChapters));
            }
        }

        progress?.Report(new DownloadProgress("done!", 1));
        return new DownloadReport(results);
    }

    private async Task<string?> DownloadChapterAsync(
        ToonJob job,
        ChapterRef chapter,
        int chapterNumber,
        string comicName,
        string comicDirectory,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress,
        double progressBase,
        double progressSpan,
        CancellationToken cancellationToken)
    {
        progress?.Report(new DownloadProgress(
            $"grabbing the html of chapter {chapterNumber} of \"{job.Name}\"", progressBase));

        IReadOnlyList<Uri> imageUrls =
            await _scraper.GetChapterImagesAsync(chapter, cancellationToken).ConfigureAwait(false);

        string label = FileNaming.ChapterLabel(chapterNumber, chapter.Name);

        // Every format except "multiple images without chapter folders" needs its own folder:
        // the single-file formats use one as a scratch area and delete it afterwards.
        bool usesOwnFolder = options.ChapterFolders || options.Format != SaveFormat.MultipleImages;
        bool isScratchFolder = options.Format != SaveFormat.MultipleImages;

        string imageDirectory = usesOwnFolder ? Path.Combine(comicDirectory, label) : comicDirectory;
        Directory.CreateDirectory(imageDirectory);

        string[] files = await DownloadImagesAsync(
            imageUrls, chapter, imageDirectory, comicName, chapterNumber, job.Name, options,
            progress, progressBase, progressSpan, cancellationToken).ConfigureAwait(false);

        try
        {
            switch (options.Format)
            {
                case SaveFormat.MultipleImages:
                    return imageDirectory;

                case SaveFormat.Pdf:
                {
                    string output = FileNaming.EnsureUnique(Path.Combine(comicDirectory, label + ".pdf"));
                    progress?.Report(new DownloadProgress($"writing {Path.GetFileName(output)}", progressBase));
                    await ImagePdfWriter.WriteAsync(files, output, cancellationToken).ConfigureAwait(false);
                    return output;
                }

                case SaveFormat.Cbz:
                {
                    string output = FileNaming.EnsureUnique(Path.Combine(comicDirectory, label + ".cbz"));
                    progress?.Report(new DownloadProgress($"writing {Path.GetFileName(output)}", progressBase));
                    ChapterPackager.WriteCbz(files, output);
                    return output;
                }

                case SaveFormat.OneImage:
                {
                    string output = FileNaming.EnsureUnique(Path.Combine(comicDirectory, label + ".png"));
                    progress?.Report(new DownloadProgress($"stitching {Path.GetFileName(output)}", progressBase));
                    ChapterPackager.WriteStitchedImage(files, output, options.MaxStitchedHeight);
                    return output;
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(options), options.Format, "Unknown save format.");
            }
        }
        finally
        {
            // Only ever remove a folder this method created as scratch space.
            if (isScratchFolder
                && usesOwnFolder
                && !PathsEqual(imageDirectory, comicDirectory)
                && Directory.Exists(imageDirectory))
            {
                Directory.Delete(imageDirectory, recursive: true);
            }
        }
    }

    private async Task<string[]> DownloadImagesAsync(
        IReadOnlyList<Uri> imageUrls,
        ChapterRef chapter,
        string imageDirectory,
        string comicName,
        int chapterNumber,
        string displayName,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress,
        double progressBase,
        double progressSpan,
        CancellationToken cancellationToken)
    {
        string[] files = new string[imageUrls.Count];
        int completed = 0;

        // Bounded parallelism: faster than the original's strictly serial fetch, still polite.
        int concurrency = Math.Clamp(options.MaxParallelDownloads, 1, 16);
        ConcurrentBag<Exception> failures = [];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, imageUrls.Count),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
            async (index, ct) =>
            {
                try
                {
                    (byte[] bytes, string? contentType) =
                        await _client.GetImageAsync(imageUrls[index], chapter.Url, ct).ConfigureAwait(false);

                    string extension = ImageExtension(bytes, contentType, imageUrls[index]);
                    string path = Path.Combine(
                        imageDirectory,
                        FileNaming.ImageFileName(comicName, chapterNumber, index, extension));

                    await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                    files[index] = path;

                    int done = Interlocked.Increment(ref completed);
                    progress?.Report(new DownloadProgress(
                        $"downloading image {done} of {imageUrls.Count} of chapter {chapterNumber} of \"{displayName}\"",
                        progressBase + progressSpan * done / imageUrls.Count));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(ex);
                }
            }).ConfigureAwait(false);

        if (!failures.IsEmpty)
        {
            throw new AggregateException(
                $"{failures.Count} of {imageUrls.Count} images failed in chapter {chapterNumber}.", failures);
        }

        return files;
    }

    /// <summary>
    /// Picks a file extension from the bytes themselves, falling back to the content type and URL.
    /// The original assumed every image was a .jpg.
    /// </summary>
    internal static string ImageExtension(ReadOnlySpan<byte> bytes, string? contentType, Uri url)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return ".gif";
        if (bytes.Length >= 12 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return ".webp";

        string? fromContentType = contentType?.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => null,
        };
        if (fromContentType is not null) return fromContentType;

        string fromUrl = Path.GetExtension(url.AbsolutePath);
        return string.IsNullOrEmpty(fromUrl) ? ".jpg" : fromUrl;
    }

    private static bool PathsEqual(string a, string b)
    {
        StringComparison comparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            comparison);
    }
}
