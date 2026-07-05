// =============================================================================
//  Quartz jobs — heartbeat, daily post, token refresh, webhook sweep
// =============================================================================
using InnerShiftLab.Core;
using InnerShiftLab.Engine;
using InnerShiftLab.Monetization;
using InnerShiftLab.Providers;
using Microsoft.Extensions.Options;
using Quartz;

namespace InnerShiftLab.Scheduling;

[DisallowConcurrentExecution]
public sealed class HeartbeatJob : IJob
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<HeartbeatJob> _log;

    public HeartbeatJob(IWebHostEnvironment env, ILogger<HeartbeatJob> log)
    {
        _env = env; _log = log;
    }

    public Task Execute(IJobExecutionContext context)
    {
        // Writes a heartbeat file so external monitors (Tasker) can confirm the app is alive
        // and that Quartz is actually firing (Codespaces idle-timeout bypass relies on this)
        try
        {
            var dir = AppPaths.DataDir(AppPaths.ResolveRoot(_env.ContentRootPath));
            var path = Path.Combine(dir, "heartbeat.txt");
            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Heartbeat write failed");
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// The scheduled content job. It keeps all the content-prep value — auto-curation,
/// per-platform caption formatting (UTM links from the IRIS engine), media rendering,
/// daily caps — but it never publishes. Its final stage produces a ready-to-post
/// draft per (slot, platform) and marks it PendingApproval in SQLite; a human must
/// approve each draft before anything can reach a platform API.
/// </summary>
[DisallowConcurrentExecution]
public sealed class DailyDraftJob : IJob
{
    private readonly IIrisEngine _engine;
    private readonly IDraftRepository _drafts;
    private readonly IContentRenderer _renderer;
    private readonly IRepository _repo;
    private readonly IrisSettings _settings;
    private readonly ILogger<DailyDraftJob> _log;

    public DailyDraftJob(IIrisEngine engine, IDraftRepository drafts, IContentRenderer renderer,
        IRepository repo, IrisSettings settings, ILogger<DailyDraftJob> log)
    {
        _engine = engine; _drafts = drafts; _renderer = renderer; _repo = repo; _settings = settings; _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var queued = _engine.GetCurrentQueue();
            if (queued.Count == 0)
            {
                _log.LogInformation("DailyDraft: no queued slots, auto-curating from top hooks");
                var top = _engine.GetAllHooks()
                    .OrderByDescending(h => h.Score)
                    .Take(3)
                    .ToList();
                foreach (var h in top)
                {
                    _engine.Enqueue(h.Id, h.PrimaryPillar, h.BestFor);
                }
                queued = _engine.GetCurrentQueue();
            }

            var max = Math.Max(0, _settings.MaxPostsPerDayPerPlatform);

            foreach (var slot in queued.ToList())
            {
                try
                {
                    // Content prep is unchanged: make sure the slot has media before drafting.
                    slot.MediaUrl ??= await _renderer.RenderImageAsync(slot.Caption);

                    var anyDrafted = false;
                    var anySkippedByCap = false;
                    foreach (var platform in slot.Platforms)
                    {
                        // The daily cap now limits drafts created per platform per day,
                        // so the approval queue can't silently pile up.
                        if (await _drafts.CountCreatedTodayAsync(platform) >= max)
                        {
                            anySkippedByCap = true;
                            _log.LogInformation("Skipping {Platform} draft for hook {Hook}: daily cap ({Max}) reached",
                                platform, slot.HookId, max);
                            continue;
                        }

                        var draft = await _drafts.CreateAsync(new PostDraft
                        {
                            Platform = platform,
                            Caption = slot.Caption,
                            MediaReference = slot.MediaUrl ?? "",
                            ScheduledFor = slot.ScheduledAt,
                            Status = DraftStatus.PendingApproval,
                        });
                        anyDrafted = true;
                        _log.LogInformation("Draft {Id} created for {Platform} (hook {Hook}) — awaiting approval",
                            draft.Id, platform, slot.HookId);
                    }

                    if (anyDrafted)
                    {
                        // The slot's job is done once drafts exist; publishing is a human decision.
                        slot.Status = PostStatus.Draft;
                        _engine.RemoveFromQueue(slot);
                        await _repo.SavePostAsync(slot);
                    }
                    else if (anySkippedByCap)
                    {
                        // Drafting only blocked by the daily cap: leave queued for a later run.
                        slot.Status = PostStatus.Queued;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Slot {Slot} draft prep failed", slot.SlotId);
                    _engine.MarkFailed(slot, ex.Message);
                    _engine.RemoveFromQueue(slot);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "DailyDraft job crashed");
        }
    }
}

[DisallowConcurrentExecution]
public sealed class TokenRefreshJob : IJob
{
    private readonly IMetaProvider _meta;
    private readonly ITiktokProvider _tt;
    private readonly IYoutubeProvider _yt;
    private readonly ILogger<TokenRefreshJob> _log;

    public TokenRefreshJob(IMetaProvider meta, ITiktokProvider tt, IYoutubeProvider yt, ILogger<TokenRefreshJob> log)
    {
        _meta = meta; _tt = tt; _yt = yt; _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var (status, expiresAt) = await _meta.GetTokenStatusAsync();
            if (status == "valid" && expiresAt.HasValue && expiresAt.Value < DateTimeOffset.UtcNow.AddDays(7))
                _log.LogWarning("Meta token expiring soon at {Expiry}, manual re-auth recommended", expiresAt);
        }
        catch (Exception ex) { _log.LogError(ex, "Meta token check failed"); }

        try
        {
            var (status, expiresAt) = await _tt.GetTokenStatusAsync();
            if (status == "valid" && expiresAt.HasValue && expiresAt.Value < DateTimeOffset.UtcNow.AddHours(2))
                _log.LogWarning("TikTok token expiring soon at {Expiry}", expiresAt);
        }
        catch (Exception ex) { _log.LogError(ex, "TikTok token check failed"); }

        try
        {
            var (status, expiresAt) = await _yt.GetTokenStatusAsync();
            if (status == "valid" && expiresAt.HasValue && expiresAt.Value < DateTimeOffset.UtcNow.AddMinutes(10))
                _log.LogWarning("YouTube token expiring soon at {Expiry}", expiresAt);
        }
        catch (Exception ex) { _log.LogError(ex, "YouTube token check failed"); }
    }
}

[DisallowConcurrentExecution]
public sealed class WebhookSweepJob : IJob
{
    private readonly IMonetizationLogger _mon;
    private readonly ILogger<WebhookSweepJob> _log;
    public WebhookSweepJob(IMonetizationLogger mon, ILogger<WebhookSweepJob> log) { _mon = mon; _log = log; }

    public async Task Execute(IJobExecutionContext context)
    {
        _log.LogDebug("Webhook sweep @ {Time}", DateTimeOffset.UtcNow);
        await Task.CompletedTask;
    }
}

