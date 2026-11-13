using System.IO.Compression;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Kea.Core;

/// <summary>Collapses a folder of downloaded images into the chosen output format.</summary>
public static class ChapterPackager
{
    /// <summary>Merges multiple CBZ files, writing the result asynchronously to a stream.</summary>
    public static async Task MergeCbzFilesStreamAsync(IReadOnlyList<string> cbzPaths, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cbzPaths);
        if (cbzPaths.Count == 0) throw new ArgumentException("Nothing to merge.", nameof(cbzPaths));

        // ZipArchive only supports synchronous writes, so buffer into a MemoryStream first
        // then copy async — Kestrel forbids synchronous writes directly to Response.Body.
        using MemoryStream buffer = new();

        using (ZipArchive outArchive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int c = 0; c < cbzPaths.Count; c++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using ZipArchive inArchive = ZipFile.OpenRead(cbzPaths[c]);
                IOrderedEnumerable<ZipArchiveEntry> entries = inArchive.Entries
                    .OrderBy(e => e.Name, NaturalStringComparer.Instance);

                int page = 0;
                foreach (ZipArchiveEntry entry in entries)
                {
                    string name = $"{c + 1:D5}_{page:D5}{Path.GetExtension(entry.Name)}";
                    ZipArchiveEntry outEntry = outArchive.CreateEntry(name, CompressionLevel.Fastest);
                    using Stream inStream = entry.Open();
                    using Stream outStream = outEntry.Open();
                    await inStream.CopyToAsync(outStream, cancellationToken).ConfigureAwait(false);
                    page++;
                }
            }
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges multiple CBZ files, writing the result directly to a stream.</summary>
    public static void MergeCbzFilesStream(IReadOnlyList<string> cbzPaths, Stream output)
    {
        ArgumentNullException.ThrowIfNull(cbzPaths);
        if (cbzPaths.Count == 0) throw new ArgumentException("Nothing to merge.", nameof(cbzPaths));

        using ZipArchive outArchive = new(output, ZipArchiveMode.Create, leaveOpen: true);

        for (int c = 0; c < cbzPaths.Count; c++)
        {
            using ZipArchive inArchive = ZipFile.OpenRead(cbzPaths[c]);
            IOrderedEnumerable<ZipArchiveEntry> entries = inArchive.Entries
                .OrderBy(e => e.Name, NaturalStringComparer.Instance);

            int page = 0;
            foreach (ZipArchiveEntry entry in entries)
            {
                string name = $"{c + 1:D5}_{page:D5}{Path.GetExtension(entry.Name)}";
                ZipArchiveEntry outEntry = outArchive.CreateEntry(name, CompressionLevel.Fastest);
                using Stream inStream = entry.Open();
                using Stream outStream = outEntry.Open();
                inStream.CopyTo(outStream);
                page++;
            }
        }
    }

    /// <summary>
    /// Merges multiple CBZ files into one by extracting their entries in natural order.
    /// Entries are prefixed with a zero-padded chapter index so page order is preserved.
    /// </summary>
    public static void MergeCbzFiles(IReadOnlyList<string> cbzPaths, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(cbzPaths);
        if (cbzPaths.Count == 0) throw new ArgumentException("Nothing to merge.", nameof(cbzPaths));

        using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using ZipArchive outArchive = new(output, ZipArchiveMode.Create);

        for (int c = 0; c < cbzPaths.Count; c++)
        {
            using ZipArchive inArchive = ZipFile.OpenRead(cbzPaths[c]);
            IOrderedEnumerable<ZipArchiveEntry> entries = inArchive.Entries
                .OrderBy(e => e.Name, NaturalStringComparer.Instance);

            int page = 0;
            foreach (ZipArchiveEntry entry in entries)
            {
                string name = $"{c + 1:D5}_{page:D5}{Path.GetExtension(entry.Name)}";
                ZipArchiveEntry outEntry = outArchive.CreateEntry(name, CompressionLevel.Fastest);
                using Stream inStream = entry.Open();
                using Stream outStream = outEntry.Open();
                inStream.CopyTo(outStream);
                page++;
            }
        }
    }

    /// <summary>
    /// Merges multiple CBZ files into a single PDF by extracting images from each archive.
    /// </summary>
    public static async Task MergeCbzFilesToPdfAsync(
        IReadOnlyList<string> cbzPaths,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cbzPaths);
        if (cbzPaths.Count == 0) throw new ArgumentException("Nothing to merge.", nameof(cbzPaths));

        List<byte[]> allImages = [];
        foreach (string cbzPath in cbzPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using ZipArchive archive = ZipFile.OpenRead(cbzPath);
            foreach (ZipArchiveEntry entry in archive.Entries.OrderBy(e => e.Name, NaturalStringComparer.Instance))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using MemoryStream ms = new();
                using Stream s = entry.Open();
                await s.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                allImages.Add(ms.ToArray());
            }
        }

        await using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await ImagePdfWriter.WriteFromBytesAsync(allImages, output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges multiple CBZ files into a PDF, writing directly to a stream.</summary>
    public static async Task MergeCbzFilesToPdfAsync(
        IReadOnlyList<string> cbzPaths,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cbzPaths);
        if (cbzPaths.Count == 0) throw new ArgumentException("Nothing to merge.", nameof(cbzPaths));

        List<byte[]> allImages = [];
        foreach (string cbzPath in cbzPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using ZipArchive archive = ZipFile.OpenRead(cbzPath);
            foreach (ZipArchiveEntry entry in archive.Entries.OrderBy(e => e.Name, NaturalStringComparer.Instance))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using MemoryStream ms = new();
                using Stream s = entry.Open();
                await s.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                allImages.Add(ms.ToArray());
            }
        }

        await ImagePdfWriter.WriteFromBytesAsync(allImages, output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Zips a chapter's images into a comic book archive, in the order given.
    /// </summary>
    /// <remarks>
    /// The original called <see cref="ZipFile.CreateFromDirectory(string, string)"/>, which stores
    /// entries in whatever order the filesystem enumerates them. NTFS returns directory entries
    /// alphabetically, so on Windows the pages happened to come out in order; ext4, APFS and most
    /// other filesystems make no such promise. Readers that honour archive order over entry names
    /// would therefore show a scrambled chapter. Writing the entries explicitly makes page order
    /// deterministic on every platform.
    /// </remarks>
    public static void WriteCbz(IReadOnlyList<string> imagePaths, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        if (imagePaths.Count == 0) throw new ArgumentException("Nothing to archive.", nameof(imagePaths));

        using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(output, ZipArchiveMode.Create);

        foreach (string path in imagePaths)
        {
            archive.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Fastest);
        }
    }

    /// <summary>
    /// Stitches a chapter's images into one tall PNG, scaling down if the result would exceed
    /// <paramref name="maxHeight"/>.
    /// </summary>
    /// <remarks>
    /// Uses ImageSharp rather than System.Drawing, which throws on macOS and Linux from .NET 6
    /// onwards. The canvas is sized to the widest image; the original assumed every image matched
    /// the first one's width and clipped anything wider.
    /// </remarks>
    public static void WriteStitchedImage(IReadOnlyList<string> imagePaths, string outputPath, int maxHeight = 30_000)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        if (imagePaths.Count == 0) throw new ArgumentException("Nothing to stitch.", nameof(imagePaths));

        List<Image<Rgba32>> images = [];
        try
        {
            int totalHeight = 0;
            int width = 0;
            foreach (string path in imagePaths)
            {
                Image<Rgba32> image = Image.Load<Rgba32>(path);
                images.Add(image);
                totalHeight += image.Height;
                width = Math.Max(width, image.Width);
            }

            using Image<Rgba32> canvas = new(width, totalHeight);
            int y = 0;
            foreach (Image<Rgba32> image in images)
            {
                int offsetY = y;
                canvas.Mutate(ctx => ctx.DrawImage(image, new Point(0, offsetY), 1f));
                y += image.Height;
            }

            if (totalHeight > maxHeight)
            {
                double scale = (double)maxHeight / totalHeight;
                int scaledWidth = Math.Max(1, (int)Math.Round(width * scale));
                canvas.Mutate(ctx => ctx.Resize(scaledWidth, maxHeight));
            }

            canvas.SaveAsPng(outputPath);
        }
        finally
        {
            foreach (Image<Rgba32> image in images) image.Dispose();
        }
    }

    /// <summary>
    /// Natural-order string comparer: "ep9" &lt; "ep10", "(2)" &lt; "(10)".
    /// </summary>
    private sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int xStart = i, yStart = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    int cmp = x[xStart..i].Length.CompareTo(y[yStart..j].Length);
                    if (cmp != 0) return cmp;
                    cmp = string.Compare(x[xStart..i], y[yStart..j], StringComparison.Ordinal);
                    if (cmp != 0) return cmp;
                }
                else
                {
                    int cmp = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                    if (cmp != 0) return cmp;
                    i++; j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
