using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Kea.Web.Library;

/// <summary>One file or folder in the library.</summary>
public sealed record LibraryEntry(string Name, string Path, bool IsDirectory, long Size, DateTimeOffset Modified);

/// <summary>The contents of one folder, with a trail back to the root.</summary>
public sealed record LibraryListing(string Path, IReadOnlyList<LibraryEntry> Entries, IReadOnlyList<LibraryEntry> Breadcrumbs, bool CanRecheck, int RecheckIntervalDays);

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

            // Hide dotfiles (including .kea-meta.json).
            if (item.Name.StartsWith('.')) continue;

            bool isDirectory = item is DirectoryInfo;
            entries.Add(new LibraryEntry(
                item.Name,
                ToRelative(item.FullName),
                isDirectory,
                isDirectory ? 0 : ((FileInfo)item).Length,
                item.LastWriteTimeUtc));
        }

        // Folders first, then files, each in natural order (so "(10)" sorts after "(9)").
        entries = entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, NaturalComparer.Instance)
            .ToList();

        string metaFilePath = System.IO.Path.Combine(fullPath, ".kea-meta.json");
        bool canRecheck = File.Exists(metaFilePath);
        int recheckIntervalDays = 0;
        if (canRecheck && TryReadMetaFromPath(metaFilePath, out KeaMeta? m))
            recheckIntervalDays = m.RecheckIntervalDays;
        return new LibraryListing(ToRelative(fullPath), entries, BuildBreadcrumbs(fullPath), canRecheck, recheckIntervalDays);
    }

    /// <summary>Reads the recheck metadata for a folder, if it exists.</summary>
    public bool TryReadMeta(string? relativePath, [NotNullWhen(true)] out KeaMeta? meta)
    {
        meta = null;
        if (!TryResolve(relativePath, out string? fullPath)) return false;
        string metaPath = Path.Combine(fullPath, ".kea-meta.json");
        return TryReadMetaFromPath(metaPath, out meta);
    }

    private static bool TryReadMetaFromPath(string metaPath, [NotNullWhen(true)] out KeaMeta? meta)
    {
        meta = null;
        if (!File.Exists(metaPath)) return false;
        try
        {
            string json = File.ReadAllText(metaPath);
            meta = JsonSerializer.Deserialize<KeaMeta>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return meta is not null;
        }
        catch { return false; }
    }

    /// <summary>Writes updated meta back to disk. Returns false if the path is invalid.</summary>
    public bool WriteMetaFile(string? relativePath, KeaMeta meta)
    {
        if (!TryResolve(relativePath, out string? fullPath)) return false;
        if (!Directory.Exists(fullPath)) return false;
        string metaPath = Path.Combine(fullPath, ".kea-meta.json");
        try
        {
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta,
                new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch { return false; }
    }

    /// <summary>Enumerates all comic folders that have a .kea-meta.json with a non-zero recheck interval.</summary>
    public IEnumerable<(string RelativePath, KeaMeta Meta)> GetScheduledComics()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (string dir in Directory.EnumerateDirectories(_root))
        {
            string metaPath = Path.Combine(dir, ".kea-meta.json");
            if (TryReadMetaFromPath(metaPath, out KeaMeta? meta) && meta.RecheckIntervalDays > 0)
                yield return (Path.GetRelativePath(_root, dir).Replace(Path.DirectorySeparatorChar, '/'), meta);
        }
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

    /// <summary>
    /// Splits a string into alternating non-digit / digit chunks and compares them so that
    /// embedded numbers sort numerically rather than lexicographically.
    /// "(2) foo" &lt; "(10) foo", "ep9" &lt; "ep10", etc.
    /// </summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var cx = Chunks(x);
            var cy = Chunks(y);
            int len = Math.Min(cx.Count, cy.Count);

            for (int i = 0; i < len; i++)
            {
                int cmp;
                bool xNum = long.TryParse(cx[i], out long xVal);
                bool yNum = long.TryParse(cy[i], out long yVal);

                if (xNum && yNum)
                    cmp = xVal.CompareTo(yVal);
                else
                    cmp = string.Compare(cx[i], cy[i], StringComparison.OrdinalIgnoreCase);

                if (cmp != 0) return cmp;
            }

            return cx.Count.CompareTo(cy.Count);
        }

        private static List<string> Chunks(string s) =>
            Regex.Split(s, @"(\d+)").Where(c => c.Length > 0).ToList();
    }

    private string ToRelative(string fullPath)
    {
        string relative = Path.GetRelativePath(_root, fullPath);
        return relative == "." ? string.Empty : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Same natural comparer used internally, exposed for the repack endpoint.</summary>
    public sealed class NaturalFileComparer : IComparer<string>
    {
        public static readonly NaturalFileComparer Instance = new();
        private NaturalFileComparer() { }

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            return NaturalOrder(x, y);
        }

        internal static int NaturalOrder(string x, string y)
        {
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int xs = i, ys = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    int cmp = (i - xs).CompareTo(j - ys);
                    if (cmp != 0) return cmp;
                    cmp = string.Compare(x[xs..i], y[ys..j], StringComparison.Ordinal);
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
