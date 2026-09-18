using System.IO.Compression;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Kea.Core.Tests;

/// <summary>
/// Exercises the output formats end to end against generated images, so the replacements for
/// System.Drawing and iTextSharp are proven to work on this platform rather than assumed to.
/// </summary>
public class PackagingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("kea-tests-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string WriteJpeg(string name, int width, int height, Rgba32 colour)
    {
        string path = Path.Combine(_dir, name);
        using Image<Rgba32> image = new(width, height, colour);
        image.SaveAsJpeg(path, new JpegEncoder { Quality = 90 });
        return path;
    }

    private string WritePng(string name, int width, int height, Rgba32 colour)
    {
        string path = Path.Combine(_dir, name);
        using Image<Rgba32> image = new(width, height, colour);
        image.SaveAsPng(path);
        return path;
    }

    [Fact]
    public void JpegReader_ReadsDimensionsAndColourLayout()
    {
        byte[] bytes = File.ReadAllBytes(WriteJpeg("a.jpg", 120, 340, Color.Red));

        JpegInfo? parsed = JpegReader.TryRead(bytes);

        Assert.NotNull(parsed);
        JpegInfo info = parsed.Value;
        Assert.Equal(120, info.Width);
        Assert.Equal(340, info.Height);
        Assert.Equal(3, info.Components);
        Assert.False(info.IsProgressive);
        Assert.True(info.CanEmbedDirectly);
        Assert.Equal("/DeviceRGB", info.PdfColorSpace);
    }

    [Fact]
    public void JpegReader_RejectsNonJpegData()
    {
        Assert.Null(JpegReader.TryRead(File.ReadAllBytes(WritePng("a.png", 10, 10, Color.Blue))));
        Assert.Null(JpegReader.TryRead("not an image"u8));
        Assert.Null(JpegReader.TryRead([]));
    }

    [Fact]
    public async Task Pdf_HasOnePagePerImageSizedToThatImage()
    {
        List<string> images =
        [
            WriteJpeg("1.jpg", 100, 200, Color.Red),
            WriteJpeg("2.jpg", 100, 250, Color.Green),
            WriteJpeg("3.jpg", 100, 300, Color.Blue),
        ];

        string pdf = Path.Combine(_dir, "chapter.pdf");
        await ImagePdfWriter.WriteAsync(images, pdf);

        string text = File.ReadAllText(pdf, Encoding.Latin1);

        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        Assert.Contains("/Count 3", text, StringComparison.Ordinal);
        Assert.Contains("/MediaBox [0 0 100 200]", text, StringComparison.Ordinal);
        Assert.Contains("/MediaBox [0 0 100 250]", text, StringComparison.Ordinal);
        Assert.Contains("/MediaBox [0 0 100 300]", text, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(text, "/Filter /DCTDecode"));
    }

    [Fact]
    public async Task Pdf_EmbedsJpegBytesWithoutReEncodingThem()
    {
        // The point of embedding through DCTDecode is that the original bytes survive intact,
        // so the PDF path costs no image quality.
        string image = WriteJpeg("only.jpg", 64, 64, Color.Orange);
        byte[] original = File.ReadAllBytes(image);

        string pdf = Path.Combine(_dir, "one.pdf");
        await ImagePdfWriter.WriteAsync([image], pdf);

        byte[] produced = File.ReadAllBytes(pdf);
        Assert.True(ContainsSequence(produced, original), "PDF should contain the source JPEG verbatim.");
    }

    [Fact]
    public async Task Pdf_ConvertsFormatsThatCannotBeEmbeddedDirectly()
    {
        string png = WritePng("p.png", 80, 90, Color.Purple);

        string pdf = Path.Combine(_dir, "png.pdf");
        await ImagePdfWriter.WriteAsync([png], pdf);

        string text = File.ReadAllText(pdf, Encoding.Latin1);
        Assert.Contains("/MediaBox [0 0 80 90]", text, StringComparison.Ordinal);
        Assert.Contains("/Filter /DCTDecode", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdf_XrefOffsetsPointAtTheirObjects()
    {
        // A wrong xref table is the classic way a hand-written PDF opens in one reader and not
        // another, so check every offset actually lands on its object header.
        List<string> images = [WriteJpeg("1.jpg", 10, 10, Color.Red), WriteJpeg("2.jpg", 10, 10, Color.Blue)];
        string pdf = Path.Combine(_dir, "xref.pdf");
        await ImagePdfWriter.WriteAsync(images, pdf);

        string text = File.ReadAllText(pdf, Encoding.Latin1);

        int startxref = text.LastIndexOf("startxref", StringComparison.Ordinal);
        int xrefOffset = int.Parse(text[(startxref + 9)..].Trim().Split('\n')[0].Trim());
        Assert.Equal("xref", text.Substring(xrefOffset, 4));

        string xrefSection = text[xrefOffset..text.LastIndexOf("trailer", StringComparison.Ordinal)];
        string[] lines = xrefSection.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // lines[0] is "xref", lines[1] the subsection header, lines[2] the free entry.
        for (int i = 3; i < lines.Length; i++)
        {
            int objectNumber = i - 2;
            int offset = int.Parse(lines[i][..10]);
            Assert.Equal($"{objectNumber} 0 obj", text.Substring(offset, $"{objectNumber} 0 obj".Length));
        }
    }

    [Fact]
    public async Task Pdf_RejectsAnEmptyChapter()
        => await Assert.ThrowsAsync<ArgumentException>(
            () => ImagePdfWriter.WriteAsync([], Path.Combine(_dir, "empty.pdf")));

    [Fact]
    public void Cbz_ContainsEveryImageInPageOrder()
    {
        string chapterDir = Path.Combine(_dir, "chapter");
        Directory.CreateDirectory(chapterDir);

        List<string> pages = [];
        for (int i = 0; i < 12; i++)
        {
            string path = Path.Combine(chapterDir, FileNaming.ImageFileName("c", 1, i, ".jpg"));
            using Image<Rgba32> image = new(10, 10, Color.Gray);
            image.SaveAsJpeg(path);
            pages.Add(path);
        }

        string cbz = Path.Combine(_dir, "chapter.cbz");
        ChapterPackager.WriteCbz(pages, cbz);

        using ZipArchive archive = ZipFile.OpenRead(cbz);
        List<string> names = archive.Entries.Select(e => e.FullName).ToList();

        // Archive order, not just entry names: readers that ignore names must still see page order.
        Assert.Equal(12, names.Count);
        Assert.Equal(pages.Select(Path.GetFileName), names);
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
        // Flat archive, no nested chapter folder: that is what comic readers expect.
        Assert.All(names, n => Assert.DoesNotContain('/', n));
    }

    [Fact]
    public void StitchedImage_StacksImagesVertically()
    {
        List<string> images =
        [
            WriteJpeg("1.jpg", 100, 50, Color.Red),
            WriteJpeg("2.jpg", 100, 70, Color.Blue),
        ];

        string output = Path.Combine(_dir, "stitched.png");
        ChapterPackager.WriteStitchedImage(images, output);

        using Image<Rgba32> result = Image.Load<Rgba32>(output);
        Assert.Equal(100, result.Width);
        Assert.Equal(120, result.Height);
    }

    [Fact]
    public void StitchedImage_SizesTheCanvasToTheWidestImage()
    {
        // The original used the first image's width and clipped anything wider.
        List<string> images =
        [
            WriteJpeg("narrow.jpg", 60, 40, Color.Red),
            WriteJpeg("wide.jpg", 140, 40, Color.Blue),
        ];

        string output = Path.Combine(_dir, "mixed.png");
        ChapterPackager.WriteStitchedImage(images, output);

        using Image<Rgba32> result = Image.Load<Rgba32>(output);
        Assert.Equal(140, result.Width);
        Assert.Equal(80, result.Height);
    }

    [Fact]
    public void StitchedImage_ScalesDownWhenItWouldExceedTheHeightLimit()
    {
        List<string> images = [WriteJpeg("tall.jpg", 100, 400, Color.Red)];

        string output = Path.Combine(_dir, "tall.png");
        ChapterPackager.WriteStitchedImage(images, output, maxHeight: 100);

        using Image<Rgba32> result = Image.Load<Rgba32>(output);
        Assert.Equal(100, result.Height);
        Assert.Equal(25, result.Width); // aspect ratio preserved
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/webp", ".webp")]
    [InlineData(null, ".jpg")]
    public void ImageExtension_FallsBackToTheContentTypeWhenBytesAreUnrecognised(string? contentType, string expected)
        => Assert.Equal(expected, KeaDownloader.ImageExtension(
            [0x00, 0x01, 0x02, 0x03], contentType, new Uri("https://example.com/a")));

    [Fact]
    public void ImageExtension_PrefersTheActualBytes()
    {
        byte[] jpeg = File.ReadAllBytes(WriteJpeg("x.jpg", 8, 8, Color.Red));
        // Content type says PNG, the bytes say JPEG. Trust the bytes.
        Assert.Equal(".jpg", KeaDownloader.ImageExtension(jpeg, "image/png", new Uri("https://example.com/a.png")));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        => haystack.IndexOf(needle) >= 0;
}
