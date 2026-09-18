namespace Kea.Core;

/// <summary>Dimensions and colour layout read straight from a JPEG's frame header.</summary>
public readonly record struct JpegInfo(int Width, int Height, int Components, bool IsProgressive)
{
    /// <summary>The PDF colour space matching this image's component count.</summary>
    public string? PdfColorSpace => Components switch
    {
        1 => "/DeviceGray",
        3 => "/DeviceRGB",
        4 => "/DeviceCMYK",
        _ => null,
    };

    /// <summary>
    /// Whether the raw bytes can be embedded in a PDF as-is. Progressive JPEGs are excluded
    /// because the PDF DCTDecode filter only guarantees baseline support, and CMYK is excluded
    /// because Adobe-inverted CMYK needs a /Decode array to render correctly.
    /// </summary>
    public bool CanEmbedDirectly => !IsProgressive && Components is 1 or 3;
}

/// <summary>Minimal JPEG header reader — enough to size a page and pick a colour space.</summary>
public static class JpegReader
{
    /// <summary>Reads the frame header, or returns <see langword="null"/> if this is not a JPEG.</summary>
    public static JpegInfo? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null; // SOI

        int i = 2;
        while (i + 3 < data.Length)
        {
            if (data[i] != 0xFF) { i++; continue; }      // resynchronise on fill bytes
            byte marker = data[i + 1];
            i += 2;

            // Standalone markers carry no payload.
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (marker == 0xD9) break;                    // EOI
            if (i + 1 >= data.Length) break;

            int segmentLength = (data[i] << 8) | data[i + 1];
            if (segmentLength < 2 || i + segmentLength > data.Length) break;

            // SOF0..SOF15, excluding DHT (C4), JPG (C8) and DAC (CC).
            bool isStartOfFrame = marker >= 0xC0 && marker <= 0xCF
                                  && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

            if (isStartOfFrame)
            {
                if (segmentLength < 8) return null;
                int height = (data[i + 3] << 8) | data[i + 4];
                int width = (data[i + 5] << 8) | data[i + 6];
                int components = data[i + 7];
                bool progressive = marker is 0xC2 or 0xC6 or 0xCA or 0xCE;

                if (width <= 0 || height <= 0 || components <= 0) return null;
                return new JpegInfo(width, height, components, progressive);
            }

            if (marker == 0xDA) break;                    // start of scan: no header left to read
            i += segmentLength;
        }

        return null;
    }
}
