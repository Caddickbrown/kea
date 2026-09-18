using Xunit;

namespace Kea.Core.Tests;

public class FileNamingTests
{
    [Theory]
    // These are all legal on Linux but not on Windows, so downloads made on Linux would otherwise
    // produce folders and archives that cannot be extracted on Windows.
    [InlineData("Ep 3: The Fall?", "Ep 3 The Fall")]
    [InlineData("a/b\\c", "a b c")]
    [InlineData("star*name", "star name")]
    [InlineData("pipe|name", "pipe name")]
    [InlineData("quote\"name", "quote name")]
    [InlineData("<angle>", "angle")]
    public void Sanitize_RemovesCharactersIllegalOnAnySupportedPlatform(string input, string expected)
        => Assert.Equal(expected, FileNaming.Sanitize(input));

    [Fact]
    public void Sanitize_StripsTrailingDotsAndSpaces()
        => Assert.Equal("Chapter One", FileNaming.Sanitize("Chapter One.  "));

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    public void Sanitize_EscapesWindowsReservedNames(string input)
        => Assert.StartsWith("_", FileNaming.Sanitize(input), StringComparison.Ordinal);

    [Fact]
    public void Sanitize_FallsBackWhenNothingUsableRemains()
        => Assert.Equal("untitled", FileNaming.Sanitize("///"));

    [Fact]
    public void Sanitize_RemovesControlCharacters()
        => Assert.Equal("ab", FileNaming.Sanitize("ab"));

    [Fact]
    public void Sanitize_TruncatesLongNames()
        => Assert.True(FileNaming.Sanitize(new string('x', 500)).Length <= FileNaming.MaxLength);

    [Fact]
    public void ImageFileName_ZeroPadsSoLexicalOrderMatchesPageOrder()
    {
        // The original produced "Ch1.0 ... Ch1.10", which sorts 10 before 2 and therefore shuffled
        // the pages of any chapter with more than ten images in both CBZ and stitched output.
        List<string> names = Enumerable.Range(0, 12)
            .Select(i => FileNaming.ImageFileName("comic", 1, i, ".jpg"))
            .ToList();

        List<string> sorted = names.OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(names, sorted);
    }

    [Fact]
    public void ChapterLabel_MatchesTheOriginalLayout()
        => Assert.Equal("(7) The Big Fight", FileNaming.ChapterLabel(7, "The Big Fight"));

    [Fact]
    public void EnsureUnique_DoesNotOverwriteAnExistingFile()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string taken = Path.Combine(dir, "a.pdf");
            File.WriteAllText(taken, "x");

            string free = FileNaming.EnsureUnique(taken);

            Assert.NotEqual(taken, free);
            Assert.False(File.Exists(free));
            Assert.Equal("a (2).pdf", Path.GetFileName(free));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
