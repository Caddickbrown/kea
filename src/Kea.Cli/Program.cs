using System.Globalization;
using Kea.Core;

namespace Kea.Cli;

/// <summary>
/// A console front-end for Kea.
/// </summary>
/// <remarks>
/// The GUI needs a desktop session; this does not, so it also covers headless servers, SSH
/// sessions and containers. Both front-ends sit on the same <see cref="Kea.Core"/> logic.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"kea: {ex.Message}");
            Console.Error.WriteLine("Try 'kea --help'.");
            return 2;
        }

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;      // let the download unwind cleanly instead of killing the process
            cts.Cancel();
            Console.Error.WriteLine("\nstopping...");
        };

        using WebtoonClient client = new();
        KeaDownloader downloader = new(client);

        Progress<DownloadProgress> progress = new(ReportProgress);

        try
        {
            DownloadReport report = await downloader.DownloadAsync(
                options.Jobs, options.DownloadOptions, progress, cts.Token);

            ClearStatusLine();
            PrintReport(report);
            return report.FailedCount == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            ClearStatusLine();
            Console.Error.WriteLine("cancelled.");
            return 130; // conventional exit code for SIGINT
        }
        catch (Exception ex)
        {
            ClearStatusLine();
            Console.Error.WriteLine($"kea: {ex.Message}");
            return 1;
        }
    }

    private static int _statusWidth;

    private static void ReportProgress(DownloadProgress update)
    {
        // Redrawing one line keeps output readable when piped to a file as well as on a terminal.
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(update.Status);
            return;
        }

        string percent = update.Fraction is { } f
            ? $"[{(int)Math.Round(f * 100),3}%] "
            : "       ";

        string line = percent + update.Status;
        int width = Math.Max(0, Console.WindowWidth - 1);
        if (width > 0 && line.Length > width) line = line[..width];

        Console.Write('\r' + line.PadRight(_statusWidth));
        _statusWidth = line.Length;
    }

    private static void ClearStatusLine()
    {
        if (Console.IsOutputRedirected || _statusWidth == 0) return;
        Console.Write('\r' + new string(' ', _statusWidth) + '\r');
        _statusWidth = 0;
    }

    private static void PrintReport(DownloadReport report)
    {
        foreach (ChapterResult failure in report.Chapters.Where(c => !c.Succeeded))
        {
            Console.Error.WriteLine(
                $"failed: {failure.Comic} chapter {failure.ChapterNumber} ({failure.Name}): {failure.Error}");
        }

        Console.WriteLine(report.FailedCount == 0
            ? $"done! {report.SucceededCount} chapter(s) saved."
            : $"done with errors: {report.SucceededCount} saved, {report.FailedCount} failed.");
    }

    private static void PrintUsage()
    {
        string formats = string.Join(", ", Enum.GetValues<SaveFormat>().Select(f => f.CliName()));

        Console.WriteLine($"""
            kea - download webtoons.com comics for personal, offline reading.

            usage:
              kea <list-url> [<list-url> ...] [options]
              kea --urls-from <file> [options]

            The URL must be a comic's chapter list page, not a single episode, e.g.
              https://www.webtoons.com/en/action/some-comic/list?title_no=1234

            options:
              -o, --out <dir>       where to save (default: the current directory)
              -f, --format <fmt>    one of: {formats} (default: pdf)
              -s, --start <n>       first chapter to download, 1-based (default: 1)
              -e, --end <n>         last chapter to download (default: the last one)
                  --urls-from <f>   read one URL per line from a file ('-' for stdin)
                  --comic-folders   put each comic in its own sub-folder
                  --no-chapter-folders
                                    for '{SaveFormat.MultipleImages.CliName()}', save straight into the comic folder
              -j, --parallel <n>    images to fetch at once, 1-16 (default: 3)
                  --max-height <n>  height limit for '{SaveFormat.OneImage.CliName()}' output (default: 30000)
              -h, --help            show this help

            examples:
              kea "https://www.webtoons.com/en/action/x/list?title_no=1" -o ~/Comics
              kea --urls-from queue.txt -f cbz -s 10 -e 20 --comic-folders
            """);
    }
}

/// <summary>The parsed command line.</summary>
internal sealed record CliOptions(IReadOnlyList<ToonJob> Jobs, DownloadOptions DownloadOptions)
{
    public static CliOptions Parse(string[] args)
    {
        List<string> urls = [];
        string? outDir = null;
        SaveFormat format = SaveFormat.Pdf;
        int start = 1;
        int? end = null;
        bool comicFolders = false;
        bool chapterFolders = true;
        int parallel = 3;
        int maxHeight = 30_000;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-o" or "--out":
                    outDir = Next(args, ref i, arg);
                    break;

                case "-f" or "--format":
                {
                    string value = Next(args, ref i, arg);
                    if (!SaveFormats.TryParse(value, out format))
                        throw new FormatException($"unknown format '{value}'.");
                    break;
                }

                case "-s" or "--start":
                    start = ParseInt(Next(args, ref i, arg), arg);
                    break;

                case "-e" or "--end":
                    end = ParseInt(Next(args, ref i, arg), arg);
                    break;

                case "-j" or "--parallel":
                    parallel = ParseInt(Next(args, ref i, arg), arg);
                    break;

                case "--max-height":
                    maxHeight = ParseInt(Next(args, ref i, arg), arg);
                    break;

                case "--comic-folders":
                    comicFolders = true;
                    break;

                case "--no-chapter-folders":
                    chapterFolders = false;
                    break;

                case "--urls-from":
                    urls.AddRange(ReadUrlsFrom(Next(args, ref i, arg)));
                    break;

                default:
                    if (arg.StartsWith('-')) throw new FormatException($"unknown option '{arg}'.");
                    urls.Add(arg);
                    break;
            }
        }

        if (urls.Count == 0) throw new FormatException("no URLs given.");

        List<ToonJob> jobs = [];
        HashSet<int> seen = [];
        foreach (string url in urls)
        {
            if (!WebtoonUrl.TryParse(url, out WebtoonUrl? parsed, out string? error))
                throw new FormatException($"skipping '{Shorten(url)}': {error}.");

            // The original deduplicated by slug; title_no is the site's own identity for a comic.
            if (!seen.Add(parsed.TitleNo)) continue;

            try
            {
                jobs.Add(new ToonJob(parsed, start, end));
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                // ArgumentException appends "(Parameter 'x')", which means nothing to a CLI user.
                int suffix = ex.Message.IndexOf(" (Parameter", StringComparison.Ordinal);
                throw new FormatException(suffix < 0 ? ex.Message : ex.Message[..suffix]);
            }
        }

        if (jobs.Count == 0) throw new FormatException("no usable URLs given.");

        string savePath = Path.GetFullPath(outDir ?? Directory.GetCurrentDirectory());

        return new CliOptions(jobs, new DownloadOptions
        {
            SavePath = savePath,
            Format = format,
            CartoonFolders = comicFolders,
            ChapterFolders = chapterFolders,
            MaxParallelDownloads = parallel,
            MaxStitchedHeight = maxHeight,
        });
    }

    private static IEnumerable<string> ReadUrlsFrom(string path)
    {
        IEnumerable<string> lines = path == "-"
            ? ReadAllStdinLines()
            : File.ReadLines(path);

        return lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
    }

    private static IEnumerable<string> ReadAllStdinLines()
    {
        while (Console.ReadLine() is { } line) yield return line;
    }

    private static string Next(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length) throw new FormatException($"'{option}' needs a value.");
        return args[++i];
    }

    private static int ParseInt(string value, string option)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new FormatException($"'{option}' needs a number, got '{value}'.");

    private static string Shorten(string value) => value.Length <= 60 ? value : value[..57] + "...";
}
