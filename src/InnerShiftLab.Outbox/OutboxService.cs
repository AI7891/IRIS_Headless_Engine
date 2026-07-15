// =============================================================================
//  Outbox service — the daily human-in-the-loop cycle:
//  curate (IIrisEngine, untouched) -> build per-platform package -> export to
//  the operator's pickup location -> wait for manual posting -> confirm.
//  Used by both the Quartz DailyOutboxJob and the /api/outbox endpoints.
// =============================================================================
using InnerShiftLab.Core;
using InnerShiftLab.Engine;

namespace InnerShiftLab.Outbox;

public interface IOutboxService
{
    /// <summary>Builds and exports today's package from the queue (auto-curating top hooks when empty). Null when no hooks exist.</summary>
    Task<OutboxPackage?> BuildDailyPackageAsync(CancellationToken ct = default);

    /// <summary>Operator confirmation that one platform variant was posted manually. False when the variant doesn't exist.</summary>
    Task<bool> ConfirmPostedAsync(string packageId, string platform, string? postUrl, CancellationToken ct = default);
}

public sealed class OutboxService : IOutboxService
{
    private readonly IIrisEngine _engine;
    private readonly IOutboxPackageBuilder _builder;
    private readonly IPackageExporter _exporter;
    private readonly IRepository _repo;
    private readonly ILogger<OutboxService> _log;

    public OutboxService(IIrisEngine engine, IOutboxPackageBuilder builder, IPackageExporter exporter,
        IRepository repo, ILogger<OutboxService> log)
    {
        _engine = engine; _builder = builder; _exporter = exporter; _repo = repo; _log = log;
    }

    public async Task<OutboxPackage?> BuildDailyPackageAsync(CancellationToken ct = default)
    {
        var queued = _engine.GetCurrentQueue();
        if (queued.Count == 0)
        {
            _log.LogInformation("Outbox: queue empty, auto-curating from top hooks");
            var top = _engine.GetAllHooks().OrderByDescending(h => h.Score).Take(3).ToList();
            foreach (var h in top)
                _engine.Enqueue(h.Id, h.PrimaryPillar, h.BestFor);
            queued = _engine.GetCurrentQueue();
        }

        var slot = queued.FirstOrDefault();
        if (slot == null)
        {
            _log.LogWarning("Outbox: nothing to package — no hooks available");
            return null;
        }

        var package = await _builder.BuildAsync(slot, ct);

        try
        {
            var exportRef = await _exporter.ExportAsync(package, ct);
            await _repo.MarkOutboxExportedAsync(package.PackageId, exportRef);
            _log.LogInformation("Outbox package {PackageId} ready for pickup at {Ref}", package.PackageId, exportRef);
        }
        catch (Exception ex)
        {
            // The package is built and persisted; export can be retried via POST /api/outbox/build
            // tomorrow's run, or picked up from the local output/outbox folder directly.
            _log.LogError(ex, "Outbox export failed for {PackageId}; package remains local at {Dir}",
                package.PackageId, package.PackageDir);
        }

        // Track the slot in the posts table for continuity; it stays Queued until the
        // operator confirms every platform, then flips to Published.
        slot.Status = PostStatus.Queued;
        await _repo.SavePostAsync(slot);
        _engine.RemoveFromQueue(slot);
        return package;
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
}
