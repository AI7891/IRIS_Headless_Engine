// =============================================================================
//  Draft service — the human-in-the-loop layer between content prep and any
//  platform API call. Approval is the only way a draft becomes publishable,
//  and publishing uses exclusively the official platform APIs (Meta Graph,
//  TikTok Content Posting, YouTube Data v3) through the existing providers
//  with vault-held OAuth tokens. When official API access isn't configured,
//  the draft is exported as a package for manual posting instead.
// =============================================================================
using System.Text.Json;
using InnerShiftLab.Auth;
using InnerShiftLab.Core;
using InnerShiftLab.Monetization;
using InnerShiftLab.Providers;

namespace InnerShiftLab.Drafts;

public sealed record DraftActionResult(PostDraft Draft, string Action, string? Detail = null);

public interface IDraftService
{
    Task<IReadOnlyList<PostDraft>> ListAsync(DraftStatus? status, int limit);
    Task<PostDraft?> GetAsync(long id);
    Task<DraftActionResult> ApproveAsync(long id);
    Task<DraftActionResult> RejectAsync(long id);
    Task<DraftActionResult> RetryAsync(long id);
    Task<DraftActionResult> PublishAsync(long id);
    Task<DraftActionResult> ExportAsync(long id);
}

public sealed class DraftService : IDraftService
{
    private readonly IDraftRepository _drafts;
    private readonly ITokenVault _vault;
    private readonly IProviderRouter _router;
    private readonly IMonetizationLogger _monetization;
    private readonly ILogger<DraftService> _log;
    private readonly string _exportDir;

    public DraftService(IDraftRepository drafts, ITokenVault vault, IProviderRouter router,
        IMonetizationLogger monetization, ILogger<DraftService> log, IWebHostEnvironment env)
    {
        _drafts = drafts; _vault = vault; _router = router; _monetization = monetization; _log = log;
        _exportDir = Path.Combine(AppPaths.OutputDir(AppPaths.ResolveRoot(env.ContentRootPath)), "exports");
        Directory.CreateDirectory(_exportDir);
    }

    public Task<IReadOnlyList<PostDraft>> ListAsync(DraftStatus? status, int limit) => _drafts.ListAsync(status, limit);
    public Task<PostDraft?> GetAsync(long id) => _drafts.GetAsync(id);

    public async Task<DraftActionResult> ApproveAsync(long id)
    {
        var draft = await Require(id);
        // Two explicit, legal steps — approval is recorded (ApprovedAt), then the
        // draft enters the publish queue. Neither step can be skipped.
        if (!await _drafts.TransitionAsync(id, DraftStatus.PendingApproval, DraftStatus.Approved))
            throw new InvalidOperationException($"Draft {id} is {draft.Status}, not PendingApproval.");
        await _drafts.TransitionAsync(id, DraftStatus.Approved, DraftStatus.ReadyToPublish);
        var updated = (await _drafts.GetAsync(id))!;
        _log.LogInformation("Draft {Id} ({Platform}) approved -> ReadyToPublish", id, updated.Platform);
        return new DraftActionResult(updated, "approved");
    }

    public async Task<DraftActionResult> RejectAsync(long id)
    {
        var draft = await Require(id);
        if (!await _drafts.TransitionAsync(id, DraftStatus.PendingApproval, DraftStatus.Rejected))
            throw new InvalidOperationException($"Draft {id} is {draft.Status}, not PendingApproval.");
        _log.LogInformation("Draft {Id} ({Platform}) rejected", id, draft.Platform);
        return new DraftActionResult((await _drafts.GetAsync(id))!, "rejected");
    }

    public async Task<DraftActionResult> RetryAsync(long id)
    {
        var draft = await Require(id);
        if (!await _drafts.TransitionAsync(id, DraftStatus.Failed, DraftStatus.ReadyToPublish))
            throw new InvalidOperationException($"Draft {id} is {draft.Status}, not Failed — nothing to retry.");
        return new DraftActionResult((await _drafts.GetAsync(id))!, "requeued");
    }

    public async Task<DraftActionResult> PublishAsync(long id)
    {
        var draft = await Require(id);
        if (draft.Status != DraftStatus.ReadyToPublish)
            throw new InvalidOperationException(
                $"Draft {id} is {draft.Status}. Only ReadyToPublish drafts can be published — approve it first.");

        // Publishing happens only through the official platform APIs with vault-held
        // OAuth tokens. No token -> no API call; export for manual posting instead.
        var provider = ProviderFor(draft.Platform);
        var tokens = await _vault.LoadTokensAsync(provider);
        if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
        {
            var path = await WriteExportAsync(draft,
                $"No official {provider} OAuth token configured — visit /auth/{provider}/login to connect the account.");
            _log.LogWarning("Draft {Id}: no {Provider} OAuth token; exported for manual posting: {Path}", id, provider, path);
            return new DraftActionResult(draft, "exported", path);
        }

        try
        {
            var post = await _router.PublishAsync(draft.Platform, new PublishRequest(
                HookId: $"draft-{draft.Id}",
                Pillar: nameof(Pillar.Integrate),
                Caption: draft.Caption,
                MediaUrl: draft.MediaReference));
            var postId = post.PerPlatformPostIds.FirstOrDefault();
            await _drafts.TransitionAsync(id, DraftStatus.ReadyToPublish, DraftStatus.Published, platformPostId: postId);
            await _monetization.LogPostAsync(post);
            _log.LogInformation("Draft {Id} published to {Platform}: {PostId}", id, draft.Platform, postId);
            return new DraftActionResult((await _drafts.GetAsync(id))!, "published", post.PerPlatformUrls.FirstOrDefault());
        }
        catch (Exception ex)
        {
            await _drafts.TransitionAsync(id, DraftStatus.ReadyToPublish, DraftStatus.Failed, errorMessage: ex.Message);
            _log.LogError(ex, "Draft {Id} publish to {Platform} failed", id, draft.Platform);
            return new DraftActionResult((await _drafts.GetAsync(id))!, "failed", ex.Message);
        }
    }

    public async Task<DraftActionResult> ExportAsync(long id)
    {
        var draft = await Require(id);
        if (draft.Status is not (DraftStatus.ReadyToPublish or DraftStatus.Published))
            throw new InvalidOperationException($"Draft {id} is {draft.Status} — only approved drafts can be exported.");
        var path = await WriteExportAsync(draft, "Manual export requested.");
        return new DraftActionResult(draft, "exported", path);
    }

    private async Task<string> WriteExportAsync(PostDraft draft, string reason)
    {
        var path = Path.Combine(_exportDir, $"draft-{draft.Id}-{draft.Platform}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            exportedAt = DateTimeOffset.UtcNow,
            reason,
            howTo = "Post this manually in the platform's own app/studio: copy the caption, attach the media file.",
            draft.Id,
            draft.Platform,
            draft.Caption,
            mediaFile = draft.MediaReference,
            draft.ScheduledFor,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private async Task<PostDraft> Require(long id) =>
        await _drafts.GetAsync(id) ?? throw new KeyNotFoundException($"Draft {id} not found.");

    private static string ProviderFor(string platform) => platform.ToLowerInvariant() switch
    {
        "instagram" or "facebook" or "meta" => "meta",
        "tiktok" => "tiktok",
        "youtube" => "youtube",
        _ => throw new ArgumentException($"Unknown platform '{platform}'"),
    };
}
