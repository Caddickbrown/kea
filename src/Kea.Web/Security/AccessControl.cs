using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Kea.Web.Security;

/// <summary>
/// Decides whether a request may use the service.
/// </summary>
/// <remarks>
/// The desktop builds needed nothing like this: they ran as the person sitting at the machine.
/// A hosted service is reachable by anyone who can route to it, and this one starts downloads and
/// serves files off disk, so it is closed by default. The rules are deliberately blunt:
/// <list type="bullet">
///   <item>A token is configured — every request must present it.</item>
///   <item>No token — only loopback is served, so it works out of the box locally but cannot be
///   exposed to a network by accident.</item>
///   <item><see cref="KeaOptions.AllowAnonymous"/> — open to everyone, which is only sensible
///   behind a reverse proxy that authenticates first. It has to be switched on deliberately.</item>
/// </list>
/// </remarks>
public sealed class AccessControl
{
    public const string HeaderName = "X-Kea-Token";
    public const string CookieName = "kea_token";

    private readonly KeaOptions _options;
    private readonly byte[]? _expectedHash;

    public AccessControl(IOptions<KeaOptions> options)
    {
        _options = options.Value;

        if (!string.IsNullOrEmpty(_options.AccessToken))
        {
            _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(_options.AccessToken));
        }
    }

    /// <summary>True when a token is configured and therefore required.</summary>
    public bool TokenRequired => _expectedHash is not null;

    /// <summary>True when the service will answer requests from outside this machine.</summary>
    public bool AllowsRemote => TokenRequired || _options.AllowAnonymous;

    /// <summary>Checks a candidate token against the configured one in constant time.</summary>
    public bool IsValidToken(string? candidate)
    {
        if (_expectedHash is null || string.IsNullOrEmpty(candidate)) return false;

        // Hashing both sides first keeps the comparison constant time and hides the token's length.
        byte[] candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        return CryptographicOperations.FixedTimeEquals(candidateHash, _expectedHash);
    }

    /// <summary>Decides one request.</summary>
    public AccessDecision Check(HttpContext context)
    {
        if (TokenRequired)
        {
            string? presented = context.Request.Headers[HeaderName].FirstOrDefault()
                                ?? context.Request.Cookies[CookieName];

            return IsValidToken(presented)
                ? AccessDecision.Allowed
                : AccessDecision.TokenRequired;
        }

        if (_options.AllowAnonymous) return AccessDecision.Allowed;

        return IsLoopback(context.Connection.RemoteIpAddress)
            ? AccessDecision.Allowed
            : AccessDecision.LoopbackOnly;
    }

    /// <summary>
    /// Whether the peer is this machine.
    /// </summary>
    /// <remarks>
    /// Judged from the transport address only. <c>X-Forwarded-For</c> is deliberately ignored:
    /// it is caller-supplied, so trusting it would let a remote request claim to be local.
    /// </remarks>
    internal static bool IsLoopback(IPAddress? address)
    {
        if (address is null) return false;

        if (IPAddress.IsLoopback(address)) return true;

        // A v4 address arriving over a dual-stack socket shows up mapped into v6.
        return address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4());
    }
}

public enum AccessDecision
{
    Allowed,

    /// <summary>A token is configured and the request did not present a valid one.</summary>
    TokenRequired,

    /// <summary>No token is configured, so only local requests are served.</summary>
    LoopbackOnly,
}
