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
            var dir = Path.Combine(_env.ContentRootPath, "..", "data");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
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

[DisallowConcurrentExecution]
public sealed class DailyPostJob : IJob
{
    private readonly IIrisEngine _engine;
    private readonly IProviderRouter _router;
    private readonly IMonetizationLogger _mon;
    private readonly IRepository _repo;
    private readonly ILogger<DailyPostJob> _log;

    public DailyPostJob(IIrisEngine engine, IProviderRouter router, IMonetizationLogger mon, IRepository repo, ILogger<DailyPostJob> log)
    {
        _engine = engine; _router = router; _mon = mon; _repo = repo; _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var queued = _engine.GetCurrentQueue();
            if (queued.Count == 0)
            {
                _log.LogInformation("DailyPost: no queued slots, auto-curating from top hooks");
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
            foreach (var slot in queued)
            {
                try
                {
                    var req = new PublishRequest(slot.HookId, slot.Pillar.ToString(), slot.Caption, slot.MediaUrl);
                    foreach (var platform in slot.Platforms)
                    {
                        try
                        {
                            var published = await _router.PublishAsync(platform, req);
                            slot.PerPlatformPostIds = slot.PerPlatformPostIds.Concat(published.PerPlatformPostIds).ToArray();
                            slot.PerPlatformUrls = slot.PerPlatformUrls.Concat(published.PerPlatformUrls).ToArray();
                            _log.LogInformation("Published to {Platform}: {Url}",
                                platform, published.PerPlatformUrls.FirstOrDefault());
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "Publish to {Platform} failed for hook {Hook}",
                                platform, slot.HookId);
                        }
                    }
                    slot.Status = slot.PerPlatformUrls.Length > 0 ? PostStatus.Published : PostStatus.Failed;
                    await _mon.LogPostAsync(slot);
                    await _repo.SavePostAsync(slot);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Slot {Slot} failed end-to-end", slot.SlotId);
                    await _engine.MarkFailed(slot, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "DailyPost job crashed");
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

