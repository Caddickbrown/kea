using System.Net;
using Kea.Web;
using Kea.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kea.Web.Tests;

public class AccessControlTests
{
    private static AccessControl Build(string token = "", bool allowAnonymous = false)
        => new(Options.Create(new KeaOptions
        {
            LibraryPath = Path.GetTempPath(),
            AccessToken = token,
            AllowAnonymous = allowAnonymous,
        }));

    private static DefaultHttpContext Request(string? remoteIp, string? header = null, string? cookie = null)
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = remoteIp is null ? null : IPAddress.Parse(remoteIp);

        if (header is not null) context.Request.Headers[AccessControl.HeaderName] = header;
        if (cookie is not null) context.Request.Headers.Cookie = $"{AccessControl.CookieName}={cookie}";

        return context;
    }

    // --- no token configured: local only ---

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("127.0.0.5")]
    public void WithoutAToken_LocalRequestsAreAllowed(string ip)
        => Assert.Equal(AccessDecision.Allowed, Build().Check(Request(ip)));

    [Theory]
    [InlineData("192.168.1.50")]
    [InlineData("10.0.0.3")]
    [InlineData("203.0.113.9")]
    public void WithoutAToken_RemoteRequestsAreRefused(string ip)
        => Assert.Equal(AccessDecision.LoopbackOnly, Build().Check(Request(ip)));

    [Fact]
    public void WithoutAToken_AnUnknownPeerIsRefused()
        => Assert.Equal(AccessDecision.LoopbackOnly, Build().Check(Request(null)));

    [Fact]
    public void WithoutAToken_AForwardedHeaderCannotFakeBeingLocal()
    {
        // X-Forwarded-For is caller-supplied. If it were trusted, any remote request could
        // claim to be loopback and walk straight in.
        DefaultHttpContext context = Request("203.0.113.9");
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";

        Assert.Equal(AccessDecision.LoopbackOnly, Build().Check(context));
    }

    // --- token configured ---

    [Fact]
    public void WithAToken_TheCorrectHeaderIsAccepted()
        => Assert.Equal(AccessDecision.Allowed, Build("s3cret").Check(Request("203.0.113.9", header: "s3cret")));

    [Fact]
    public void WithAToken_TheCorrectCookieIsAccepted()
        => Assert.Equal(AccessDecision.Allowed, Build("s3cret").Check(Request("203.0.113.9", cookie: "s3cret")));

    [Theory]
    [InlineData("wrong")]
    [InlineData("s3cre")]
    [InlineData("s3cret ")]
    [InlineData("S3CRET")]
    [InlineData("")]
    public void WithAToken_AnythingElseIsRefused(string presented)
        => Assert.Equal(AccessDecision.TokenRequired, Build("s3cret").Check(Request("203.0.113.9", header: presented)));

    [Fact]
    public void WithAToken_EvenLocalRequestsMustPresentIt()
    {
        // Otherwise anything else running on the same host could drive the service.
        Assert.Equal(AccessDecision.TokenRequired, Build("s3cret").Check(Request("127.0.0.1")));
    }

    [Fact]
    public void IsValidToken_RejectsNullAndEmpty()
    {
        AccessControl access = Build("s3cret");
        Assert.False(access.IsValidToken(null));
        Assert.False(access.IsValidToken(""));
        Assert.True(access.IsValidToken("s3cret"));
    }

    [Fact]
    public void IsValidToken_IsAlwaysFalseWhenNoTokenIsConfigured()
        => Assert.False(Build().IsValidToken("anything"));

    // --- explicit anonymous opt-out ---

    [Fact]
    public void AllowAnonymous_ServesEveryone()
        => Assert.Equal(AccessDecision.Allowed, Build(allowAnonymous: true).Check(Request("203.0.113.9")));

    [Fact]
    public void AllowAnonymous_DoesNotOverrideAConfiguredToken()
    {
        // A token is the stronger statement of intent, so it still wins.
        AccessControl access = Build("s3cret", allowAnonymous: true);
        Assert.Equal(AccessDecision.TokenRequired, access.Check(Request("203.0.113.9")));
    }

    [Fact]
    public void AllowsRemote_ReportsWhetherTheServiceIsReachableFromOutside()
    {
        Assert.False(Build().AllowsRemote);
        Assert.True(Build("s3cret").AllowsRemote);
        Assert.True(Build(allowAnonymous: true).AllowsRemote);
    }

    [Fact]
    public void IsLoopback_HandlesIPv4MappedAddresses()
    {
        // A v4 client on a dual-stack socket arrives as ::ffff:127.0.0.1.
        Assert.True(AccessControl.IsLoopback(IPAddress.Parse("127.0.0.1").MapToIPv6()));
        Assert.False(AccessControl.IsLoopback(IPAddress.Parse("203.0.113.9").MapToIPv6()));
    }
}
