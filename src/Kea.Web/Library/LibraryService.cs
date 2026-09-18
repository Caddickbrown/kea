using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace Kea.Web.Library;

/// <summary>One file or folder in the library.</summary>
public sealed record LibraryEntry(string Name, string Path, bool IsDirectory, long Size, DateTimeOffset Modified);

/// <summary>The contents of one folder, with a trail back to the root.</summary>
public sealed record LibraryListing(string Path, IReadOnlyList<LibraryEntry> Entries, IReadOnlyList<LibraryEntry> Breadcrumbs);

/// <summary>
/// Browses and serves the downloaded files.
/// </summary>
/// <remarks>
/// Every path from a caller passes through <see cref="TryResolve"/> first. Nothing else in the
/// service turns caller input into a filesystem path, so that method is the whole trust boundary.
/// </remarks>
public sealed class LibraryService
{
    private readonly string _root;

    public LibraryService(IOptions<KeaOptions> options)
    {
        _root = Path.GetFullPath(options.Value.LibraryPath);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    /// <summary>
    /// Turns a caller-supplied relative path into an absolute one inside the library, or refuses.
    /// </summary>
    /// <remarks>
    /// Rejects, in order: absolute and rooted paths, anything that normalises outside the root,
    /// and any path crossing a symbolic link. The symlink check matters because
    /// <see cref="Path.GetFullPath(string)"/> only resolves <c>..</c> textually — a link inside the
    /// library would otherwise be a way straight back out of it.
    /// </remarks>
    public bool TryResolve(string? relativePath, [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;

        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == "/")
        {
            fullPath = _root;
            return true;
        }

        // A leading slash is stripped rather than rejected, so a path is read relative to the
        // library the way a web path is: "/Some Comic" means "<library>/Some Comic", and
        // "/etc/passwd" means "<library>/etc/passwd", not the real one.
        string candidate = relativePath.Replace('\\', '/').Trim('/');
        if (candidate.Length == 0)
        {
            fullPath = _root;
            return true;
        }

        // NUL is legal in a query string but never in a path; some APIs truncate at it.
        if (candidate.Contains('\0', StringComparison.Ordinal)) return false;

        // An absolute path, a drive letter or a UNC share must never be honoured.
        if (Path.IsPathRooted(candidate)) return false;

        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(_root, candidate));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        if (!IsInsideRoot(combined)) return false;
        if (CrossesSymlink(combined)) return false;

        fullPath = combined;
        return true;
    }

    private bool IsInsideRoot(string fullPath)
    {
        if (string.Equals(fullPath, _root, PathComparison)) return true;

        string rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootWithSeparator, PathComparison);
    }

    /// <summary>Walks every segment between the root and the target looking for a symbolic link.</summary>
    private bool CrossesSymlink(string fullPath)
    {
        string current = fullPath;

        while (!string.Equals(current, _root, PathComparison))
        {
            try
            {
                FileSystemInfo info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);

                if (info.Exists && info.LinkTarget is not null) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true; // cannot prove it is safe, so treat it as unsafe
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }

        return false;
    }

    /// <summary>Lists one folder. Returns null if the path is refused or is not a folder.</summary>
    public LibraryListing? List(string? relativePath)
    {
        if (!TryResolve(relativePath, out string? fullPath)) return null;
        if (!Directory.Exists(fullPath)) return null;

        DirectoryInfo directory = new(fullPath);
        List<LibraryEntry> entries = [];

        foreach (FileSystemInfo item in directory.EnumerateFileSystemInfos())
        {
            // Never show what we would refuse to serve.
            if (item.LinkTarget is not null) continue;

            bool isDirectory = item is DirectoryInfo;
            entries.Add(new LibraryEntry(
                item.Name,
                ToRelative(item.FullName),
                isDirectory,
                isDirectory ? 0 : ((FileInfo)item).Length,
                item.LastWriteTimeUtc));
        }

        // Folders first, then files, each alphabetically — the order people expect in a file list.
        entries = entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LibraryListing(ToRelative(fullPath), entries, BuildBreadcrumbs(fullPath));
    }

    /// <summary>Resolves a path that must be an existing file, for download.</summary>
    public bool TryResolveFile(string? relativePath, [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;
        if (!TryResolve(relativePath, out string? resolved)) return false;
        if (!File.Exists(resolved)) return false;

        fullPath = resolved;
        return true;
    }

    /// <summary>Deletes a file or folder inside the library. The root itself is never deleted.</summary>
    public bool Delete(string? relativePath)
    {
        if (!TryResolve(relativePath, out string? fullPath)) return false;
        if (string.Equals(fullPath, _root, PathComparison)) return false;

        try
        {
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
                return true;
            }

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private IReadOnlyList<LibraryEntry> BuildBreadcrumbs(string fullPath)
    {
        List<LibraryEntry> trail = [new LibraryEntry("library", string.Empty, true, 0, default)];

        string relative = ToRelative(fullPath);
        if (relative.Length == 0) return trail;

        string running = string.Empty;
        foreach (string segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            running = running.Length == 0 ? segment : running + "/" + segment;
            trail.Add(new LibraryEntry(segment, running, true, 0, default));
        }

        return trail;
    }

    private string ToRelative(string fullPath)
    {
        string relative = Path.GetRelativePath(_root, fullPath);
        return relative == "." ? string.Empty : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// macOS and Windows are case-insensitive by default, Linux is not. Comparing case-insensitively
    /// everywhere is the safe direction: it can only refuse more, never allow an escape.
    /// </summary>
    private static StringComparison PathComparison => OperatingSystem.IsLinux()
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase;

    /// <summary>Best-effort content type, used only to set a download's Content-Type header.</summary>
    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".cbz" => "application/vnd.comicbook+zip",
        ".zip" => "application/zip",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };
}
