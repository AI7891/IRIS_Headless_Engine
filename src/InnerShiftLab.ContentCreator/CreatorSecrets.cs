// =============================================================================
//  API key resolution for the content creator stages. A configured value only
//  counts when it isn't a placeholder — appsettings.json ships with REPLACE_ME*
//  markers, and sending those to a provider produces a confusing 401 instead of
//  a clear "not configured" error (or silently shadowing an env var).
// =============================================================================
namespace InnerShiftLab.ContentCreator;

public static class CreatorSecrets
{
    /// <summary>
    /// Returns the configured key, unless it is empty or a REPLACE_ME* placeholder,
    /// in which case the given environment variable (if any) is consulted.
    /// </summary>
    public static string? Resolve(string? configured, string? envFallback = null)
    {
        if (IsConfigured(configured)) return configured;
        return envFallback is null ? null : Environment.GetEnvironmentVariable(envFallback);
    }

    /// <summary>True when the value is non-empty and not a REPLACE_ME* placeholder.</summary>
    public static bool IsConfigured(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith("REPLACE_ME", StringComparison.OrdinalIgnoreCase);
}
