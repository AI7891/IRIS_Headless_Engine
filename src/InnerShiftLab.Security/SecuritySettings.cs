// =============================================================================
//  Security settings — the API key that closes the front door.
//
//  The code repository is public (deliberately, as a portfolio piece), so no
//  secret may ever live in a committed file. The key arrives via the
//  IRIS_API_KEY environment variable — in Codespaces, a Codespaces secret.
// =============================================================================
namespace InnerShiftLab.Security;

public sealed class SecuritySettings
{
    /// <summary>Master switch. True everywhere except local development.</summary>
    public bool RequireApiKey { get; set; } = true;

    /// <summary>NEVER set in appsettings.json — this repo is public. Supplied via the
    /// IRIS_API_KEY environment variable (a Codespaces secret).</summary>
    public string ApiKey { get; set; } = "";

    public int MinKeyLength { get; set; } = 32;

    // Values that look like someone forgot to set a real key. An app that boots with
    // one of these on a public port is worse than an app that does not boot.
    private static readonly string[] Placeholders = { "change-me", "change-me-in-env", "secret", "test" };

    /// <summary>
    /// Throws when <see cref="RequireApiKey"/> is true and the key is missing, shorter
    /// than <see cref="MinKeyLength"/>, or a known placeholder. Never echoes the value.
    /// </summary>
    public static void Validate(SecuritySettings settings)
    {
        if (!settings.RequireApiKey) return;

        var key = settings.ApiKey;
        var invalid = string.IsNullOrWhiteSpace(key)
            || key.Length < settings.MinKeyLength
            || Placeholders.Contains(key.Trim(), StringComparer.OrdinalIgnoreCase);
        if (invalid)
            throw new InvalidOperationException(
                "Security:ApiKey missing or too weak. Set the IRIS_API_KEY environment variable " +
                $"(>={settings.MinKeyLength} chars). Generate one with: openssl rand -base64 32. " +
                "In Codespaces: repo Settings → Secrets and variables → Codespaces.");
    }
}
