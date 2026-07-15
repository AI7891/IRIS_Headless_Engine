// =============================================================================
//  Outbox settings — which platforms to package for, and where to export.
// =============================================================================
namespace InnerShiftLab.Outbox;

public sealed class OutboxSettings
{
    public static readonly string[] DefaultPlatforms = { "instagram", "facebook", "tiktok", "youtube" };

    /// <summary>
    /// Platforms that get a formatted variant in every daily package. Empty means
    /// all supported platforms. Deliberately NOT defaulted to a filled array: the
    /// configuration binder appends bound array elements to a non-empty default
    /// instead of replacing it, which would silently duplicate every platform.
    /// Consume via <see cref="EffectivePlatforms"/>.
    /// </summary>
    public string[] Platforms { get; set; } = Array.Empty<string>();

    /// <summary>The de-duplicated platform list to build for, falling back to all supported platforms.</summary>
    public string[] EffectivePlatforms => Platforms.Length == 0
        ? DefaultPlatforms
        : Platforms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Render an mp4 for video-first platforms (TikTok/YouTube) when ffmpeg is
    /// available. Falls back to the still image if rendering fails.
    /// </summary>
    public bool RenderVideo { get; set; } = true;

    public GoogleDriveSettings GoogleDrive { get; set; } = new();
}

/// <summary>
/// Google Drive pickup folder. Authenticates with a service-account key —
/// deliberately NOT user OAuth, so no long-lived personal tokens are stored
/// (the compliance concern that motivated retiring automated posting).
/// </summary>
public sealed class GoogleDriveSettings
{
    public bool Enabled { get; set; }

    /// <summary>Path to the service-account JSON key. Falls back to the GOOGLE_APPLICATION_CREDENTIALS env var when empty.</summary>
    public string ServiceAccountJsonPath { get; set; } = "";

    /// <summary>Drive folder the operator watches from the phone. Must be shared with the service-account email.</summary>
    public string FolderId { get; set; } = "";
}
