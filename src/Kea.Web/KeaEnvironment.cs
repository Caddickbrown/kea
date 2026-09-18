using System.Collections;
using System.Reflection;

namespace Kea.Web;

/// <summary>
/// Maps friendly <c>KEA_*</c> environment variables onto the <c>Kea:*</c> configuration section.
/// </summary>
/// <remarks>
/// .NET's own prefixed provider strips the prefix and leaves the rest at the configuration root,
/// so <c>KEA_ACCESSTOKEN</c> becomes the key <c>ACCESSTOKEN</c> and never reaches
/// <c>Kea:AccessToken</c>. Setting a token that way would be silently ignored — which for an
/// access token is exactly the kind of failure nobody notices. The canonical
/// <c>Kea__AccessToken</c> form works on its own; this adds the shorter one that is far more
/// natural in a Dockerfile or a systemd unit.
/// </remarks>
public static class KeaEnvironment
{
    public const string Prefix = "KEA_";

    /// <summary>
    /// Turns the process environment into <c>Kea:Name</c> settings. Names are matched against the
    /// properties of <see cref="KeaOptions"/>, ignoring case, so only real settings are mapped and
    /// a typo cannot quietly invent one.
    /// </summary>
    public static Dictionary<string, string?> Map(IDictionary? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables();

        Dictionary<string, string> known = typeof(KeaOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToDictionary(p => p.Name, p => p.Name, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string?> mapped = new(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is not string key) continue;
            if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;

            string name = key[Prefix.Length..];

            // Leave the canonical KEA_Kea__AccessToken style to the built-in provider.
            if (name.Contains("__", StringComparison.Ordinal)) continue;

            if (!known.TryGetValue(name, out string? property)) continue;

            mapped[$"{KeaOptions.SectionName}:{property}"] = entry.Value as string;
        }

        return mapped;
    }
}
