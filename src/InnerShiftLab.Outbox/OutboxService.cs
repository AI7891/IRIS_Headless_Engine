// =============================================================================
//  Outbox service — the daily human-in-the-loop cycle:
//  curate (IIrisEngine, untouched) -> build per-platform packages -> export to
//  the operator's pickup location -> wait for manual posting -> confirm.
//  Used by both the Quartz DailyOutboxJob and the /api/outbox endpoints.
//
//  Guarantees:
//  - MaxPostsPerDayPerPlatform is honored across the whole day, seeded from the
//    SQLite outbox table so container restarts don't reset the count.
//  - A failed export never loses content: items stay Pending and are retried
//    automatically at the start of every daily run, or on demand via
//    POST /api/outbox/{packageId}/export.
// =============================================================================
using InnerShiftLab.Core;
using InnerShiftLab.Engine;

namespace InnerShiftLab.Outbox;

public interface IOutboxService
{
    /// <summary>
    /// Retries any pending exports, then drains the queue into packages
    /// (auto-curating top hooks when empty) until the daily per-platform cap is
    /// reached. Empty when no hooks exist or every platform is already at cap.
    /// </summary>
    Task<IReadOnlyList<OutboxPackage>> BuildDailyPackagesAsync(CancellationToken ct = default);

    /// <summary>
    /// Re-exports a package whose export previously failed. Returns the export
    /// reference, or null when the package doesn't exist. Idempotent: an already
    /// exported package returns its existing reference.
    /// </summary>
    Task<string?> RetryExportAsync(string packageId, CancellationToken ct = default);

    /// <summary>Operator confirmation that one platform variant was posted manually. False when the variant doesn't exist.</summary>
    Task<bool> ConfirmPostedAsync(string packageId, string platform, string? postUrl, CancellationToken ct = default);
}

public sealed class OutboxService : IOutboxService
{
    private readonly IIrisEngine _engine;
    private readonly IOutboxPackageBuilder _builder;
    private readonly IPackageExporter _exporter;
    private readonly IRepository _repo;
    private readonly IrisSettings _irisSettings;
    private readonly OutboxSettings _settings;
    private readonly ILogger<OutboxService> _log;

    public OutboxService(IIrisEngine engine, IOutboxPackageBuilder builder, IPackageExporter exporter,
        IRepository repo, IrisSettings irisSettings, OutboxSettings settings, ILogger<OutboxService> log)
    {
        _engine = engine; _builder = builder; _exporter = exporter; _repo = repo;
        _irisSettings = irisSettings; _settings = settings; _log = log;
    }

    public async Task<IReadOnlyList<OutboxPackage>> BuildDailyPackagesAsync(CancellationToken ct = default)
    {
        await RetryPendingExportsAsync(ct);

        var queued = _engine.GetCurrentQueue();
        if (queued.Count == 0)
        {
            _log.LogInformation("Outbox: queue empty, auto-curating from top hooks");
            var top = _engine.GetAllHooks().OrderByDescending(h => h.Score).Take(3).ToList();
            foreach (var h in top)
                _engine.Enqueue(h.Id, h.PrimaryPillar, h.BestFor);
            queued = _engine.GetCurrentQueue();
        }

        if (queued.Count == 0)
        {
            _log.LogWarning("Outbox: nothing to package — no hooks available");
            return Array.Empty<OutboxPackage>();
        }

        // Per-platform daily cap, seeded from what the outbox already produced today
        // (SQLite, not memory — restarts must not reset it).
        var counts = new Dictionary<string, int>(
            await _repo.CountOutboxItemsForDayAsync(DateTimeOffset.UtcNow), StringComparer.OrdinalIgnoreCase);
        var max = Math.Max(0, _irisSettings.MaxPostsPerDayPerPlatform);

        var packages = new List<OutboxPackage>();
        foreach (var slot in queued)
        {
            ct.ThrowIfCancellationRequested();
            var allowed = _settings.EffectivePlatforms.Where(p => counts.GetValueOrDefault(p) < max).ToArray();
            if (allowed.Length == 0)
            {
                _log.LogInformation("Outbox: daily cap ({Max}/platform) reached; {Remaining} slot(s) stay queued",
                    max, queued.Count - packages.Count);
                break;
            }

            var package = await _builder.BuildAsync(slot, allowed, ct);
            await TryExportAsync(package, ct);

            // Track the slot in the posts table for continuity; it stays Queued until
            // the operator confirms every platform, then flips to Published.
            slot.Status = PostStatus.Queued;
            await _repo.SavePostAsync(slot);
            _engine.RemoveFromQueue(slot);

            foreach (var item in package.Items)
                counts[item.Platform] = counts.GetValueOrDefault(item.Platform) + 1;
            packages.Add(package);
        }
        return packages;
    }

    public async Task<string?> RetryExportAsync(string packageId, CancellationToken ct = default)
    {
        var items = await _repo.GetOutboxPackageAsync(packageId);
        if (items.Count == 0) return null;

        var existingRef = items.Select(i => i.ExportRef).FirstOrDefault(r => !string.IsNullOrEmpty(r));
        if (items.All(i => i.Status != OutboxStatus.Pending) && existingRef != null)
            return existingRef; // already exported — nothing to retry

        // Reconstruct the package location from the persisted items:
        // media lives at <packageDir>/<platform>/media.*
        var first = items[0];
        var packageDir = Path.GetDirectoryName(Path.GetDirectoryName(first.MediaPath));
        if (packageDir == null || !Directory.Exists(packageDir))
            throw new InvalidOperationException(
                $"Package files for '{packageId}' no longer exist on disk ({packageDir}). " +
                "Run POST /api/outbox/build to produce a fresh package.");

        var package = new OutboxPackage(packageId, first.HookId, "", first.Pillar,
            first.CreatedAt, packageDir, Path.Combine(packageDir, "manifest.json"), items);
        var exportRef = await _exporter.ExportAsync(package, ct);
        await _repo.MarkOutboxExportedAsync(packageId, exportRef);
        _log.LogInformation("Outbox package {PackageId} re-exported to {Ref}", packageId, exportRef);
        return exportRef;
    }

    public async Task<bool> ConfirmPostedAsync(string packageId, string platform, string? postUrl, CancellationToken ct = default)
    {
        if (!await _repo.MarkOutboxPostedAsync(packageId, platform, postUrl))
            return false;

        var items = await _repo.GetOutboxPackageAsync(packageId);
        var posted = items.Where(i => i.Status == OutboxStatus.Posted).ToList();
        var allDone = items.All(i => i.Status is OutboxStatus.Posted or OutboxStatus.Skipped);

        // Mirror progress into the posts table so existing reporting keeps working.
        var first = items[0];
        await _repo.SavePostAsync(new PostSlot
        {
            SlotId = packageId,
            HookId = first.HookId,
            Pillar = first.Pillar,
            Platforms = items.Select(i => i.Platform).ToArray(),
            Caption = first.Caption,
            ScheduledAt = first.CreatedAt,
            Status = allDone ? PostStatus.Published : PostStatus.Publishing,
            PerPlatformUrls = posted.Where(i => !string.IsNullOrEmpty(i.PostUrl)).Select(i => i.PostUrl!).ToArray(),
        });

        _log.LogInformation("Outbox confirm: {PackageId}/{Platform} posted ({Posted}/{Total} platforms done)",
            packageId, platform, posted.Count, items.Count);
        return true;
    }

    /// <summary>Re-exports every package that still has Pending items (i.e. a previous export failed).</summary>
    private async Task RetryPendingExportsAsync(CancellationToken ct)
    {
        var pending = await _repo.GetOutboxItemsAsync(OutboxStatus.Pending, limit: 500);
        foreach (var packageId in pending.Select(i => i.PackageId).Distinct())
        {
            try
            {
                await RetryExportAsync(packageId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Outbox: re-export of {PackageId} failed; will retry on the next run", packageId);
            }
        }
    }

    private async Task TryExportAsync(OutboxPackage package, CancellationToken ct)
    {
        try
        {
            var exportRef = await _exporter.ExportAsync(package, ct);
            await _repo.MarkOutboxExportedAsync(package.PackageId, exportRef);
            _log.LogInformation("Outbox package {PackageId} ready for pickup at {Ref}", package.PackageId, exportRef);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex,
                "Outbox export failed for {PackageId}; package remains local at {Dir}. It will be retried " +
                "automatically on the next daily run, or on demand via POST /api/outbox/{PackageId}/export",
                package.PackageId, package.PackageDir, package.PackageId);
        }
    }
}
