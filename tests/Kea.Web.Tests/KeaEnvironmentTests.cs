using System.Collections;
using Kea.Web;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kea.Web.Tests;

/// <summary>
/// The Dockerfile, compose file and systemd unit all configure the service with short
/// <c>KEA_*</c> variables. If that mapping breaks, an access token set that way is ignored and
/// the service comes up less protected than its operator believes.
/// </summary>
public class KeaEnvironmentTests
{
    private static Dictionary<string, string?> Map(params (string Key, string Value)[] variables)
    {
        Hashtable environment = [];
        foreach ((string key, string value) in variables) environment[key] = value;
        return KeaEnvironment.Map(environment);
    }

    [Fact]
    public void Map_BindsTheAccessToken()
    {
        Dictionary<string, string?> mapped = Map(("KEA_ACCESSTOKEN", "s3cret"));
        Assert.Equal("s3cret", mapped["Kea:AccessToken"]);
    }

    [Fact]
    public void Map_BindsTheLibraryPath()
    {
        Dictionary<string, string?> mapped = Map(("KEA_LIBRARYPATH", "/library"));
        Assert.Equal("/library", mapped["Kea:LibraryPath"]);
    }

    [Theory]
    [InlineData("KEA_MAXCONCURRENTJOBS", "Kea:MaxConcurrentJobs")]
    [InlineData("KEA_MAXPARALLELDOWNLOADS", "Kea:MaxParallelDownloads")]
    [InlineData("KEA_ALLOWANONYMOUS", "Kea:AllowAnonymous")]
    [InlineData("KEA_JOBHISTORYLIMIT", "Kea:JobHistoryLimit")]
    public void Map_BindsEveryDocumentedSetting(string variable, string expectedKey)
        => Assert.True(Map((variable, "7")).ContainsKey(expectedKey));

    [Fact]
    public void Map_IsCaseInsensitive()
    {
        Assert.Equal("x", Map(("kea_accesstoken", "x"))["Kea:AccessToken"]);
        Assert.Equal("x", Map(("KEA_AccessToken", "x"))["Kea:AccessToken"]);
    }

    [Fact]
    public void Map_IgnoresVariablesThatAreNotSettings()
    {
        // A typo must not invent a setting that then looks configured.
        Assert.Empty(Map(("KEA_ACCESS_TOKEN", "x")));
        Assert.Empty(Map(("KEA_NOT_A_SETTING", "x")));
        Assert.Empty(Map(("PATH", "/usr/bin")));
        Assert.Empty(Map(("ASPNETCORE_URLS", "http://+:8080")));
    }

    [Fact]
    public void Map_LeavesTheCanonicalDoubleUnderscoreFormAlone()
        => Assert.Empty(Map(("KEA_Kea__AccessToken", "x")));

    [Fact]
    public void Map_ResultBindsOntoKeaOptions()
    {
        // The real check: the mapping actually populates the options object.
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Map(
                ("KEA_ACCESSTOKEN", "s3cret"),
                ("KEA_LIBRARYPATH", Path.GetTempPath()),
                ("KEA_MAXCONCURRENTJOBS", "4")))
            .Build();

        KeaOptions options = new();
        configuration.GetSection(KeaOptions.SectionName).Bind(options);

        Assert.Equal("s3cret", options.AccessToken);
        Assert.Equal(4, options.MaxConcurrentJobs);
        Assert.Equal(Path.GetTempPath(), options.LibraryPath);
    }
}
