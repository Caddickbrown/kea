using Kea.Web;
using Kea.Web.Library;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kea.Web.Tests;

/// <summary>
/// The library endpoints turn caller-supplied text into filesystem paths, so this is the part of
/// the web app most worth attacking. Everything here tries to get outside the library root.
/// </summary>
public class LibraryServiceTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("kea-lib-").FullName;
    private readonly string _root;
    private readonly string _secret;
    private readonly LibraryService _library;

    public LibraryServiceTests()
    {
        _root = Path.Combine(_base, "library");
        Directory.CreateDirectory(Path.Combine(_root, "Some Comic"));
        File.WriteAllText(Path.Combine(_root, "Some Comic", "(1) First.pdf"), "pdf");

        // A file the service must never be able to reach: a sibling of the library root.
        _secret = Path.Combine(_base, "secret.txt");
        File.WriteAllText(_secret, "do not serve this");

        _library = new LibraryService(Options.Create(new KeaOptions { LibraryPath = _root }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("../../secret.txt")]
    [InlineData("Some Comic/../../secret.txt")]
    [InlineData("./../secret.txt")]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("\\..\\secret.txt")]
    [InlineData("Some Comic/../../")]
    public void TryResolve_RefusesTraversalOutOfTheRoot(string path)
        => Assert.False(_library.TryResolve(path, out _));

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("//etc/passwd")]
    [InlineData("/secret.txt")]
    public void TryResolve_TreatsALeadingSlashAsRootRelative(string path)
    {
        // A leading slash is stripped and the rest read relative to the library, the way a web
        // path works. "/etc/passwd" therefore means "<library>/etc/passwd" — which does not exist —
        // rather than the real one. Containment is what matters, and it holds.
        Assert.True(_library.TryResolve(path, out string? resolved));
        Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, resolved, StringComparison.Ordinal);
        Assert.False(File.Exists(resolved));
    }

    /// <summary>
    /// The invariant the whole trust boundary rests on: whatever a caller sends, a path that is
    /// accepted is always inside the library root.
    /// </summary>
    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("..%2f..%2fsecret.txt")]
    [InlineData("....//....//secret.txt")]
    [InlineData("Some Comic/../../secret.txt")]
    [InlineData("./././../secret.txt")]
    [InlineData("~/secret.txt")]
    [InlineData("C:\\Windows\\system32\\config\\sam")]
    [InlineData("\\\\server\\share\\file")]
    [InlineData(".")]
    [InlineData("Some Comic/.")]
    public void TryResolve_NeverYieldsAPathOutsideTheRoot(string path)
    {
        if (!_library.TryResolve(path, out string? resolved)) return; // refused outright is fine

        string root = Path.GetFullPath(_root);
        Assert.True(
            resolved == root || resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"'{path}' resolved to '{resolved}', which is outside '{root}'.");
    }

    [Fact]
    public void TryResolve_RefusesEmbeddedNulls()
        => Assert.False(_library.TryResolve("Some Comic/(1) First.pdf\0.png", out _));

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData(null)]
    public void TryResolve_TreatsEmptyPathAsTheRoot(string? path)
    {
        Assert.True(_library.TryResolve(path, out string? resolved));
        Assert.Equal(Path.GetFullPath(_root), resolved);
    }

    [Fact]
    public void TryResolve_AllowsPathsInsideTheRoot()
    {
        Assert.True(_library.TryResolve("Some Comic/(1) First.pdf", out string? resolved));
        Assert.StartsWith(Path.GetFullPath(_root), resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_RefusesAPathThatCrossesASymlink()
    {
        // Path.GetFullPath only resolves ".." textually, so without an explicit check a symlink
        // planted inside the library would be a way straight back out of it.
        string link = Path.Combine(_root, "escape");
        try
        {
            Directory.CreateSymbolicLink(link, _base);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // the filesystem will not let us create one; nothing to prove here
        }

        Assert.False(_library.TryResolve("escape/secret.txt", out _));
        Assert.False(_library.TryResolve("escape", out _));
    }

    [Fact]
    public void List_HidesSymlinks()
    {
        string link = Path.Combine(_root, "linked.txt");
        try
        {
            File.CreateSymbolicLink(link, _secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        LibraryListing? listing = _library.List("");

        Assert.NotNull(listing);
        Assert.DoesNotContain(listing.Entries, e => e.Name == "linked.txt");
    }

    [Fact]
    public void List_ShowsFoldersBeforeFiles()
    {
        File.WriteAllText(Path.Combine(_root, "aaa.pdf"), "x");

        LibraryListing? listing = _library.List("");

        Assert.NotNull(listing);
        Assert.True(listing.Entries[0].IsDirectory);
        Assert.Equal("Some Comic", listing.Entries[0].Name);
    }

    [Fact]
    public void List_ReturnsNullForAPathOutsideTheRoot()
        => Assert.Null(_library.List("../"));

    [Fact]
    public void List_BuildsABreadcrumbTrail()
    {
        LibraryListing? listing = _library.List("Some Comic");

        Assert.NotNull(listing);
        Assert.Equal(["library", "Some Comic"], listing.Breadcrumbs.Select(b => b.Name));
        Assert.Equal("Some Comic", listing.Path);
    }

    [Fact]
    public void TryResolveFile_RequiresAnExistingFile()
    {
        Assert.True(_library.TryResolveFile("Some Comic/(1) First.pdf", out _));
        Assert.False(_library.TryResolveFile("Some Comic", out _));          // a folder
        Assert.False(_library.TryResolveFile("Some Comic/nope.pdf", out _)); // missing
        Assert.False(_library.TryResolveFile("../secret.txt", out _));
    }

    [Fact]
    public void Delete_RefusesToDeleteTheRootItself()
    {
        Assert.False(_library.Delete(""));
        Assert.False(_library.Delete("/"));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void Delete_RefusesAnythingOutsideTheRoot()
    {
        Assert.False(_library.Delete("../secret.txt"));
        Assert.True(File.Exists(_secret));
    }

    [Fact]
    public void Delete_RemovesFilesAndFoldersInsideTheRoot()
    {
        Assert.True(_library.Delete("Some Comic/(1) First.pdf"));
        Assert.True(_library.Delete("Some Comic"));
        Assert.False(Directory.Exists(Path.Combine(_root, "Some Comic")));
    }

    [Theory]
    [InlineData("a.pdf", "application/pdf")]
    [InlineData("a.cbz", "application/vnd.comicbook+zip")]
    [InlineData("a.png", "image/png")]
    [InlineData("a.unknown", "application/octet-stream")]
    public void ContentTypeFor_MapsTheFormatsKeaProduces(string name, string expected)
        => Assert.Equal(expected, LibraryService.ContentTypeFor(name));
}
