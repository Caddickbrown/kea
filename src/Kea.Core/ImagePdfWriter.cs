using System.Globalization;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace Kea.Core;

/// <summary>
/// Writes a PDF in which every page is exactly one image, sized to that image.
/// </summary>
/// <remarks>
/// This replaces iTextSharp 5, which only ships for .NET Framework. Rather than swap in another
/// PDF library, JPEG bytes are embedded verbatim through the PDF DCTDecode filter. That keeps the
/// dependency list short and, unlike a decode/re-encode round trip, loses no image quality.
/// Anything that is not a baseline grayscale or RGB JPEG is converted to one first.
/// </remarks>
public static class ImagePdfWriter
{
    private static readonly byte[] Newline = "\n"u8.ToArray();

    /// <summary>Quality used when an image has to be re-encoded before embedding.</summary>
    public const int RecodeQuality = 92;

    public static async Task WriteAsync(
        IReadOnlyList<string> imagePaths,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        if (imagePaths.Count == 0)
            throw new ArgumentException("A PDF needs at least one image.", nameof(imagePaths));

        await using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await WriteAsync(imagePaths, output, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteAsync(
        IReadOnlyList<string> imagePaths,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        List<(byte[] Data, JpegInfo Info)> pages = [];
        foreach (string path in imagePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            pages.Add(PrepareForEmbedding(bytes));
        }

        WritePdf(pages, output);
    }

    /// <summary>
    /// Returns JPEG bytes that the DCTDecode filter can take as-is, re-encoding only when needed.
    /// </summary>
    private static (byte[] Data, JpegInfo Info) PrepareForEmbedding(byte[] bytes)
    {
        JpegInfo? info = JpegReader.TryRead(bytes);
        if (info is { CanEmbedDirectly: true } usable) return (bytes, usable);

        using Image image = Image.Load(bytes);
        using MemoryStream buffer = new();
        image.SaveAsJpeg(buffer, new JpegEncoder
        {
            Quality = RecodeQuality,
            // ImageSharp 2.x always emits baseline JPEG, which is what DCTDecode needs.
            ColorType = JpegColorType.YCbCrRatio444,
        });

        byte[] recoded = buffer.ToArray();
        JpegInfo? recodedInfo = JpegReader.TryRead(recoded)
            ?? throw new InvalidOperationException("Re-encoded image is not a readable JPEG.");

        return (recoded, recodedInfo.Value);
    }

    private static void WritePdf(List<(byte[] Data, JpegInfo Info)> pages, Stream output)
    {
        // Object 1 is the catalog, object 2 the page tree; each page then takes three objects.
        const int firstPageObject = 3;
        int objectCount = 2 + pages.Count * 3;
        long[] offsets = new long[objectCount + 1]; // 1-based; index 0 is the free entry

        CountingStream stream = new(output);

        Write(stream, "%PDF-1.4\n");
        // A comment of high bytes marks the file as binary for tools that sniff content.
        stream.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3]);
        stream.Write(Newline);

        offsets[1] = stream.Position;
        Write(stream, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = stream.Position;
        StringBuilder kids = new();
        for (int i = 0; i < pages.Count; i++)
        {
            if (i > 0) kids.Append(' ');
            kids.Append(CultureInfo.InvariantCulture, $"{firstPageObject + i * 3} 0 R");
        }

        Write(stream, $"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");

        for (int i = 0; i < pages.Count; i++)
        {
            (byte[] data, JpegInfo info) = pages[i];
            int pageObj = firstPageObject + i * 3;
            int contentObj = pageObj + 1;
            int imageObj = pageObj + 2;

            string width = info.Width.ToString(CultureInfo.InvariantCulture);
            string height = info.Height.ToString(CultureInfo.InvariantCulture);

            offsets[pageObj] = stream.Position;
            Write(stream,
                $"{pageObj} 0 obj\n" +
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] " +
                $"/Resources << /XObject << /Im0 {imageObj} 0 R >> >> " +
                $"/Contents {contentObj} 0 R >>\nendobj\n");

            // Scale the unit image square up to the page, then paint it.
            string content = $"q {width} 0 0 {height} 0 0 cm /Im0 Do Q\n";
            byte[] contentBytes = Encoding.ASCII.GetBytes(content);

            offsets[contentObj] = stream.Position;
            Write(stream, $"{contentObj} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
            stream.Write(contentBytes);
            Write(stream, "endstream\nendobj\n");

            offsets[imageObj] = stream.Position;
            Write(stream,
                $"{imageObj} 0 obj\n" +
                $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} " +
                $"/ColorSpace {info.PdfColorSpace} /BitsPerComponent 8 /Filter /DCTDecode " +
                $"/Length {data.Length} >>\nstream\n");
            stream.Write(data);
            Write(stream, "\nendstream\nendobj\n");
        }

        long xrefOffset = stream.Position;
        Write(stream, $"xref\n0 {objectCount + 1}\n");
        Write(stream, "0000000000 65535 f \n");
        for (int i = 1; i <= objectCount; i++)
        {
            Write(stream, offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        }

        Write(stream,
            $"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\n" +
            $"startxref\n{xrefOffset.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");

        stream.Flush();
    }

    private static void Write(Stream stream, string text) => stream.Write(Encoding.ASCII.GetBytes(text));

    /// <summary>
    /// Tracks how many bytes have been written, since the xref table needs byte offsets and the
    /// destination stream is not necessarily seekable.
    /// </summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        private long _position;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _position;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            _position += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            _position += buffer.Length;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
