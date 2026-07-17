// =============================================================================
//  Quartz jobs — heartbeat, daily outbox, webhook sweep; plus the quarantined
//  auto-publish jobs (daily post, token refresh) that only run when
//  Features:AutoPublish is enabled.
// =============================================================================
using InnerShiftLab.Core;
using InnerShiftLab.Engine;
using InnerShiftLab.Monetization;
using InnerShiftLab.Outbox;
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

[DisallowConcurrentExecution]
public sealed class DailyOutboxJob : IJob
{
    private readonly IOutboxService _outbox;
    private readonly ILogger<DailyOutboxJob> _log;

    public DailyOutboxJob(IOutboxService outbox, ILogger<DailyOutboxJob> log)
    {
        _outbox = outbox; _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var packages = await _outbox.BuildDailyPackagesAsync(context.CancellationToken);
            if (packages.Count == 0)
                _log.LogWarning("DailyOutbox: no packages built (no hooks available or daily caps reached)");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "DailyOutbox job crashed");
        }
    }
}

[DisallowConcurrentExecution]
public sealed class RetentionJob : IJob
{
    private readonly IRepository _repo;
    private readonly IPackageExporter _exporter;
    private readonly OutboxSettings _settings;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<RetentionJob> _log;

    public RetentionJob(IRepository repo, IPackageExporter exporter, OutboxSettings settings,
        IWebHostEnvironment env, ILogger<RetentionJob> log)
    {
        _repo = repo; _exporter = exporter; _settings = settings; _env = env; _log = log;
    }

    private sealed record PackageSummary(string PackageId, string PackageDir, string? ExportRef,
        DateTimeOffset Earliest, bool AllTerminal, bool Pruned);

    public async Task Execute(IJobExecutionContext context)
    {
        var r = _settings.Retention;
        if (!r.Enabled) return;
        var ct = context.CancellationToken;

        try
        {
            var outboxRoot = Path.Combine(AppPaths.OutputDir(AppPaths.ResolveRoot(_env.ContentRootPath)), "outbox");
            var now = DateTimeOffset.UtcNow;
            var ageCutoff = now.AddDays(-Math.Max(0, r.KeepDays));

            var summaries = (await _repo.GetOutboxItemsAsync(status: null, limit: 5000))
                .GroupBy(i => i.PackageId)
                .Select(g => new PackageSummary(
                    g.Key,
                    g.Select(i => i.PackageDir).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "",
                    g.Select(i => i.ExportRef).FirstOrDefault(e => !string.IsNullOrEmpty(e)),
                    g.Min(i => i.CreatedAt),
                    g.All(i => i.Status is OutboxStatus.Posted or OutboxStatus.Skipped),
                    g.Any(i => i.MediaPruned)))
                .ToList();

            var prunedIds = new HashSet<string>();
            var reclaimed = 0L;
            var skippedUnposted = 0;

            // 1. AGE PASS — abandoned-content cap: prune anything older than KeepDays,
            //    regardless of posted status.
            foreach (var s in summaries.Where(s => !s.Pruned && s.Earliest < ageCutoff).OrderBy(s => s.Earliest))
            {
                reclaimed += await PrunePackageAsync(s, outboxRoot, ct);
                prunedIds.Add(s.PackageId);
            }

            // 2. SIZE PASS — FIFO under MaxTotalMegabytes.
            if (r.MaxTotalMegabytes > 0)
            {
                var capBytes = (long)r.MaxTotalMegabytes * 1024 * 1024;
                var currentBytes = OutboxRetention.DirectorySizeBytes(outboxRoot);
                if (currentBytes > capBytes)
                {
                    // Candidates the size pass may prune (respecting KeepUnpostedPackages),
                    // oldest first, excluding anything just pruned by the age pass.
                    var candidateIds = await _repo.GetPrunablePackageIdsAsync(
                        keepUnposted: r.KeepUnpostedPackages, olderThanUtc: ageCutoff);
                    var byId = summaries.ToDictionary(s => s.PackageId);
                    var oldestFirst = candidateIds
                        .Where(id => !prunedIds.Contains(id) && byId.ContainsKey(id))
                        .Select(id => (PackageId: id, Bytes: OutboxRetention.DirectorySizeBytes(byId[id].PackageDir)))
                        .ToList();
                    skippedUnposted = summaries.Count(s => !s.Pruned && !prunedIds.Contains(s.PackageId)
                        && !s.AllTerminal && r.KeepUnpostedPackages && s.Earliest >= ageCutoff);

                    foreach (var id in OutboxRetention.SelectForSizePass(oldestFirst, currentBytes, capBytes))
                    {
                        reclaimed += await PrunePackageAsync(byId[id], outboxRoot, ct);
                        prunedIds.Add(id);
                    }
                }
            }

            var remainingMb = OutboxRetention.DirectorySizeBytes(outboxRoot) / (1024 * 1024);
            _log.LogInformation(
                "Retention: pruned {Count} package(s), reclaimed {MB} MB, {Remaining} MB remaining, {Skipped} kept (unposted)",
                prunedIds.Count, reclaimed / (1024 * 1024), remainingMb, skippedUnposted);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Retention job crashed");
        }
    }

    /// <summary>Deletes the local media dir, prunes the remote copy, then flags the rows LAST.</summary>
    private async Task<long> PrunePackageAsync(PackageSummary s, string outboxRoot, CancellationToken ct)
    {
        try
        {
            long bytes = 0;
            if (!string.IsNullOrEmpty(s.PackageDir) && Directory.Exists(s.PackageDir))
            {
                // Never delete outside the outbox root, whatever the stored path says.
                if (!OutboxRetention.IsInsideRoot(outboxRoot, s.PackageDir))
                {
                    _log.LogWarning("Retention: refusing to delete {Dir} — outside outbox root {Root}",
                        s.PackageDir, outboxRoot);
                    return 0;
                }
                bytes = OutboxRetention.DirectorySizeBytes(s.PackageDir);
                Directory.Delete(s.PackageDir, recursive: true);
                RemoveEmptyParent(Path.GetDirectoryName(s.PackageDir), outboxRoot);
            }

            if (!string.IsNullOrEmpty(s.ExportRef))
                await _exporter.PruneAsync(s.ExportRef, ct);

            // Flag LAST: a crash before here leaves the package prunable again rather
            // than orphaning a flagged-but-present package.
            await _repo.MarkOutboxMediaPrunedAsync(s.PackageId);
            return bytes;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Retention: pruning package {PackageId} failed", s.PackageId);
            return 0;
        }
    }

    private static void RemoveEmptyParent(string? dateDir, string outboxRoot)
    {
        try
        {
            if (dateDir != null && OutboxRetention.IsInsideRoot(outboxRoot, dateDir)
                && !string.Equals(Path.GetFullPath(dateDir), Path.GetFullPath(outboxRoot), StringComparison.Ordinal)
                && Directory.Exists(dateDir) && !Directory.EnumerateFileSystemEntries(dateDir).Any())
            {
                Directory.Delete(dateDir);
            }
        }
        catch { /* best effort */ }
    }
}

[DisallowConcurrentExecution]
public sealed class ExportRetryJob : IJob
{
    private readonly IOutboxService _outbox;
    private readonly IRepository _repo;
    private readonly ILogger<ExportRetryJob> _log;

    public ExportRetryJob(IOutboxService outbox, IRepository repo, ILogger<ExportRetryJob> log)
    {
        _outbox = outbox; _repo = repo; _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var pending = await _repo.GetUnexportedPackageIdsAsync();
            foreach (var packageId in pending)
            {
                try
                {
                    var exportRef = await _outbox.ExportPackageAsync(packageId, context.CancellationToken);
                    if (exportRef != null)
                        _log.LogInformation("ExportRetry: package {PackageId} exported to {Ref}", packageId, exportRef);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One stuck package must not abort the sweep.
                    _log.LogError(ex, "ExportRetry: package {PackageId} still failing", packageId);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ExportRetry job crashed");
        }
    }
}

// -----------------------------------------------------------------------------
//  QUARANTINED: DailyPostJob and TokenRefreshJob belong to the retired
//  auto-publish pipeline. They are only scheduled when Features:AutoPublish is
//  true (see Program.cs) — kept compiling so the pipeline can be re-enabled if
//  the platform apps ever get verified.
// -----------------------------------------------------------------------------

[DisallowConcurrentExecution]
public sealed class DailyPostJob : IJob
{
    private readonly IIrisEngine _engine;
    private readonly IProviderRouter _router;
    private readonly IMonetizationLogger _mon;
    private readonly IRepository _repo;
    private readonly IrisSettings _settings;
    private readonly ILogger<DailyPostJob> _log;

    public DailyPostJob(IIrisEngine engine, IProviderRouter router, IMonetizationLogger mon, IRepository repo, IrisSettings settings, ILogger<DailyPostJob> log)
    {
        _engine = engine; _router = router; _mon = mon; _repo = repo; _settings = settings; _log = log;
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

            // Enforce MaxPostsPerDayPerPlatform: seed today's counts from what was already published.
            var publishedToday = await _repo.GetPublishedTodayAsync();
            var perPlatformCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in publishedToday)
                foreach (var plat in p.Platforms)
                    perPlatformCount[plat] = perPlatformCount.GetValueOrDefault(plat) + 1;
            var max = Math.Max(0, _settings.MaxPostsPerDayPerPlatform);

            foreach (var slot in queued)
            {
                try
                {
                    var req = new PublishRequest(slot.HookId, slot.Pillar.ToString(), slot.Caption, slot.MediaUrl);
                    var anyPublished = false;
                    var anySkippedByCap = false;
                    foreach (var platform in slot.Platforms)
                    {
                        if (perPlatformCount.GetValueOrDefault(platform) >= max)
                        {
                            anySkippedByCap = true;
                            _log.LogInformation("Skipping {Platform} for hook {Hook}: daily cap ({Max}) reached",
                                platform, slot.HookId, max);
                            continue;
                        }
                        try
                        {
                            var published = await _router.PublishAsync(platform, req);
                            slot.PerPlatformPostIds = slot.PerPlatformPostIds.Concat(published.PerPlatformPostIds).ToArray();
                            slot.PerPlatformUrls = slot.PerPlatformUrls.Concat(published.PerPlatformUrls).ToArray();
                            perPlatformCount[platform] = perPlatformCount.GetValueOrDefault(platform) + 1;
                            anyPublished = true;
                            _log.LogInformation("Published to {Platform}: {Url}",
                                platform, published.PerPlatformUrls.FirstOrDefault());
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "Publish to {Platform} failed for hook {Hook}",
                                platform, slot.HookId);
                        }
                    }

                    if (anyPublished)
                    {
                        slot.Status = PostStatus.Published;
                        _engine.MarkPublished(slot);
                        // Slot is done — remove from the in-memory queue so it isn't re-published next run.
                        _engine.RemoveFromQueue(slot);
                    }
                    else if (anySkippedByCap)
                    {
                        // Nothing published only because of the daily cap: leave queued for a later run.
                        slot.Status = PostStatus.Queued;
                    }
                    else
                    {
                        slot.Status = PostStatus.Failed;
                        _engine.MarkFailed(slot, "All platform publishes failed");
                        _engine.RemoveFromQueue(slot);
                    }

                    await _mon.LogPostAsync(slot);
                    await _repo.SavePostAsync(slot);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Slot {Slot} failed end-to-end", slot.SlotId);
                    _engine.MarkFailed(slot, ex.Message);
                    _engine.RemoveFromQueue(slot);
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

