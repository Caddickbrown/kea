using System.IO.Compression;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Kea.Core;

/// <summary>Collapses a folder of downloaded images into the chosen output format.</summary>
public static class ChapterPackager
{
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
}
