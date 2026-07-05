// =============================================================================
//  Post drafts — the queue-and-approve lifecycle.
//  The pipeline no longer publishes anything on its own: every scheduled job
//  ends by producing a ready-to-post draft that a human must approve before
//  it can be published (via official platform APIs) or exported for manual
//  posting. This keeps the tool inside Meta/TikTok/YouTube platform terms.
// =============================================================================
namespace InnerShiftLab.Core;

public enum DraftStatus
{
    PendingApproval,
    Approved,
    Rejected,
    ReadyToPublish,
    Published,
    Failed,
}

public sealed class PostDraft
{
    public long Id { get; set; }
    public string Platform { get; set; } = "";           // instagram | facebook | tiktok | youtube
    public string Caption { get; set; } = "";            // formatted, platform-ready copy
    public string MediaReference { get; set; } = "";     // path or ID of the video/carousel asset
    public DateTimeOffset? ScheduledFor { get; set; }
    public DraftStatus Status { get; set; } = DraftStatus.PendingApproval;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? PlatformPostId { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// The only legal draft transitions. A draft can never reach Published without
/// having passed through Approved and ReadyToPublish — the state machine is
/// enforced here in code and atomically in SQL (guarded UPDATE) so no code
/// path can skip the human approval step.
/// </summary>
public static class DraftStateMachine
{
    private static readonly Dictionary<DraftStatus, DraftStatus[]> Allowed = new()
    {
        [DraftStatus.PendingApproval] = new[] { DraftStatus.Approved, DraftStatus.Rejected },
        [DraftStatus.Approved]        = new[] { DraftStatus.ReadyToPublish },
        [DraftStatus.ReadyToPublish]  = new[] { DraftStatus.Published, DraftStatus.Failed },
        // Failed drafts already passed approval; allow an explicit retry back into the
        // publish queue. Rejected and Published are terminal.
        [DraftStatus.Failed]          = new[] { DraftStatus.ReadyToPublish },
        [DraftStatus.Rejected]        = Array.Empty<DraftStatus>(),
        [DraftStatus.Published]       = Array.Empty<DraftStatus>(),
    };

    public static bool CanTransition(DraftStatus from, DraftStatus to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);
}
