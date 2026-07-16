// =============================================================================
//  Outbox models — the human-in-the-loop replacement for automated publishing.
//  IRIS still curates and renders daily, but instead of calling platform APIs it
//  writes one platform-formatted package per day to the outbox (SQLite = source
//  of truth) and exports it to Google Drive. The operator posts manually from
//  the phone and confirms via POST /api/outbox/{packageId}/{platform}/confirm.
// =============================================================================
namespace InnerShiftLab.Core;

/// <summary>Lifecycle of one platform variant inside an outbox package.</summary>
public enum OutboxStatus
{
    /// <summary>Rendered and stored locally, not yet exported.</summary>
    Pending,
    /// <summary>Exported to the operator's pickup location (Google Drive or local folder).</summary>
    Exported,
    /// <summary>Operator confirmed the variant was posted manually.</summary>
    Posted,
    /// <summary>Operator decided not to post this variant.</summary>
    Skipped,
}

/// <summary>
/// One platform-formatted variant of a daily content package. A package (one
/// hook, one day) fans out into one OutboxItem per target platform, each with
/// its own media dimensions, caption limit, and hashtag count already applied.
/// </summary>
public sealed class OutboxItem
{
    /// <summary>Groups the per-platform variants of one daily package. Matches the PostSlot.SlotId it was built from.</summary>
    public string PackageId { get; set; } = "";
    public string Platform { get; set; } = "";
    public string HookId { get; set; } = "";
    public Pillar Pillar { get; set; }
    /// <summary>Full caption, already trimmed to the platform limit. Contains the UTM link, so attribution survives manual posting.</summary>
    public string Caption { get; set; } = "";
    /// <summary>YouTube-style title where the platform needs one; empty otherwise.</summary>
    public string Title { get; set; } = "";
    /// <summary>Local path of the rendered media file (image or video).</summary>
    public string MediaPath { get; set; } = "";
    /// <summary>Root directory of the package on disk — persisted so a failed export can be retried without re-rendering.</summary>
    public string PackageDir { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    /// <summary>Where the exported copy lives — a Google Drive link or a local package directory.</summary>
    public string? ExportRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PostedAt { get; set; }
    /// <summary>Public URL of the manual post, supplied by the operator on confirm.</summary>
    public string? PostUrl { get; set; }
}
