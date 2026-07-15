// =============================================================================
//  Outbox settings — which platforms to package for, and where to export.
// =============================================================================
namespace InnerShiftLab.Outbox;

public sealed class OutboxSettings
{
    /// <summary>Platforms that get a formatted variant in every daily package.</summary>
    public string[] Platforms { get; set; } = { "instagram", "facebook", "tiktok", "youtube" };

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
