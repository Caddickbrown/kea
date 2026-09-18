using System.Globalization;
using System.Text;

namespace Kea.Core;

/// <summary>
/// Builds file and directory names that are legal on Windows, macOS and Linux alike.
/// </summary>
/// <remarks>
/// The original build sanitised with <see cref="Path.GetInvalidFileNameChars"/>, but that set is
/// platform dependent: on Linux it is only '/' and NUL, so a chapter called "Ch 1: What?" would be
/// written verbatim and then be unreadable once the folder or CBZ was opened on Windows. Downloads
/// are meant to be portable, so this sanitises against the union of all three platforms' rules.
/// </remarks>
public static class FileNaming
{
    private static readonly char[] AlwaysInvalid =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    // Windows refuses these stems regardless of extension.
    private static readonly HashSet<string> ReservedStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Longest name this will emit, leaving room for extensions and parent paths.</summary>
    public const int MaxLength = 120;

    /// <summary>
    /// Strips characters that any supported platform rejects and collapses the result to something
    /// safe to place on disk. Returns <paramref name="fallback"/> if nothing usable is left.
    /// </summary>
    public static string Sanitize(string? name, string fallback = "untitled")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        StringBuilder builder = new(name.Length);
        foreach (char c in name.Trim())
        {
            // Control characters are illegal on Windows and a nuisance everywhere else.
            if (char.IsControl(c)) continue;
            builder.Append(Array.IndexOf(AlwaysInvalid, c) >= 0 ? ' ' : c);
        }

        // Collapse the runs of whitespace left behind by removed characters.
        string cleaned = string.Join(' ', builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // Windows silently drops trailing dots and spaces, which turns "a." and "a" into the same file.
        cleaned = cleaned.TrimEnd('.', ' ');

        if (cleaned.Length > MaxLength) cleaned = cleaned[..MaxLength].TrimEnd('.', ' ');
        if (cleaned.Length == 0) return fallback;

        string stem = Path.GetFileNameWithoutExtension(cleaned);
        if (ReservedStems.Contains(stem)) cleaned = "_" + cleaned;

        return cleaned;
    }

    /// <summary>
    /// Names a chapter folder or file, e.g. <c>(7) The Big Fight</c>, matching the original layout.
    /// </summary>
    public static string ChapterLabel(int chapterNumber, string? chapterName)
    {
        string safeName = Sanitize(chapterName, $"chapter {chapterNumber}");
        return Sanitize($"({chapterNumber}) {safeName}", $"chapter {chapterNumber}");
    }

    /// <summary>
    /// Names a single downloaded image. The index is zero padded so that lexical order — which is
    /// what CBZ readers and directory listings use — matches page order.
    /// </summary>
    /// <remarks>
    /// The original named images <c>Ch1.0 … Ch1.10</c>, which sorts 10 before 2 and shuffled the
    /// pages of any chapter with more than ten images in both CBZ and stitched-image output.
    /// </remarks>
    public static string ImageFileName(string comicName, int chapterNumber, int imageIndex, string extension)
    {
        if (!extension.StartsWith('.')) extension = "." + extension;
        string safeComic = Sanitize(comicName, "comic");
        string index = imageIndex.ToString("D4", CultureInfo.InvariantCulture);
        return Sanitize($"{safeComic} Ch{chapterNumber}.{index}", $"Ch{chapterNumber}.{index}") + extension;
    }

    /// <summary>
    /// Appends " (2)", " (3)" … until the path is free, so a rerun never silently overwrites.
    /// </summary>
    public static string EnsureUnique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        string? dir = Path.GetDirectoryName(path);
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        for (int i = 2; i < 10_000; i++)
        {
            string candidate = Path.Combine(dir ?? string.Empty, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        throw new IOException($"Could not find a free filename near '{path}'.");
    }
}
