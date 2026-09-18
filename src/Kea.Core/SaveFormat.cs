namespace Kea.Core;

/// <summary>How a downloaded chapter is written to disk.</summary>
public enum SaveFormat
{
    /// <summary>One PDF per chapter, each image a page.</summary>
    Pdf,

    /// <summary>One comic book archive (zipped images) per chapter.</summary>
    Cbz,

    /// <summary>Every image kept as its own file.</summary>
    MultipleImages,

    /// <summary>All images of a chapter stitched vertically into one tall PNG.</summary>
    OneImage,
}

public static class SaveFormats
{
    /// <summary>The label shown in the UI, kept identical to the original Windows build.</summary>
    public static string DisplayName(this SaveFormat format) => format switch
    {
        SaveFormat.Pdf => "PDF file",
        SaveFormat.Cbz => "CBZ file",
        SaveFormat.MultipleImages => "multiple images",
        SaveFormat.OneImage => "one image (may be lower in quality)",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>The short token accepted by the command line front-end.</summary>
    public static string CliName(this SaveFormat format) => format switch
    {
        SaveFormat.Pdf => "pdf",
        SaveFormat.Cbz => "cbz",
        SaveFormat.MultipleImages => "images",
        SaveFormat.OneImage => "one-image",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public static bool TryParse(string? value, out SaveFormat format)
    {
        format = SaveFormat.Pdf;
        if (string.IsNullOrWhiteSpace(value)) return false;

        foreach (SaveFormat candidate in Enum.GetValues<SaveFormat>())
        {
            if (string.Equals(value, candidate.CliName(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, candidate.DisplayName(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                format = candidate;
                return true;
            }
        }

        return false;
    }
}
