// =============================================================================
//  Outbox package builder — turns one curated PostSlot into a ready-to-post
//  package: one platform-formatted media file + caption text file per platform,
//  plus a manifest.json, all under output/outbox/<date>/<packageId>/.
//  Every variant is also written to the SQLite outbox table (source of truth).
// =============================================================================
using InnerShiftLab.Core;
using InnerShiftLab.Engine;
using Newtonsoft.Json;

namespace InnerShiftLab.Outbox;

/// <summary>Everything produced for one daily package, on disk and in SQLite.</summary>
public sealed record OutboxPackage(
    string PackageId,
    string HookId,
    string HookText,
    Pillar Pillar,
    DateTimeOffset CreatedAt,
    string PackageDir,
    string ManifestPath,
    IReadOnlyList<OutboxItem> Items);

public interface IOutboxPackageBuilder
{
    Task<OutboxPackage> BuildAsync(PostSlot slot, CancellationToken ct = default);
}

public sealed class OutboxPackageBuilder : IOutboxPackageBuilder
{
    private readonly IContentRenderer _renderer;
    private readonly IRepository _repo;
    private readonly OutboxSettings _settings;
    private readonly string _outputRoot;
    private readonly ILogger<OutboxPackageBuilder> _log;

    public OutboxPackageBuilder(IContentRenderer renderer, IRepository repo, OutboxSettings settings,
        string outputRoot, ILogger<OutboxPackageBuilder> log)
    {
        _renderer = renderer; _repo = repo; _settings = settings; _outputRoot = outputRoot; _log = log;
    }

    public async Task<OutboxPackage> BuildAsync(PostSlot slot, CancellationToken ct = default)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var packageDir = Path.Combine(_outputRoot, "outbox", createdAt.ToString("yyyy-MM-dd"), slot.SlotId);
        Directory.CreateDirectory(packageDir);

        var visualText = string.IsNullOrWhiteSpace(slot.HookText) ? slot.Caption : slot.HookText;
        var items = new List<OutboxItem>();

        foreach (var platform in _settings.Platforms)
        {
            ct.ThrowIfCancellationRequested();
            var variant = PlatformFormatter.Format(platform, visualText, slot.Caption);
            var format = variant.Format;
            var platformDir = Path.Combine(packageDir, format.Platform);
            Directory.CreateDirectory(platformDir);

            var mediaPath = await _renderer.RenderImageAsync(
                visualText, Path.Combine(platformDir, "media.png"),
                width: format.Width, height: format.Height);

            if (format.PrefersVideo && _settings.RenderVideo)
            {
                try
                {
                    mediaPath = await _renderer.RenderVideoAsync(
                        visualText, mediaPath, Path.Combine(platformDir, "media.mp4"));
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "Video render failed for {Platform}; package ships the still image instead", platform);
                }
            }

            await File.WriteAllTextAsync(Path.Combine(platformDir, "caption.txt"), variant.Caption, ct);
            if (variant.Title.Length > 0)
                await File.WriteAllTextAsync(Path.Combine(platformDir, "title.txt"), variant.Title, ct);

            var item = new OutboxItem
            {
                PackageId = slot.SlotId,
                Platform = format.Platform,
                HookId = slot.HookId,
                Pillar = slot.Pillar,
                Caption = variant.Caption,
                Title = variant.Title,
                MediaPath = mediaPath,
                Width = format.Width,
                Height = format.Height,
                Status = OutboxStatus.Pending,
                CreatedAt = createdAt,
            };
            await _repo.SaveOutboxItemAsync(item);
            items.Add(item);
        }

        var manifestPath = Path.Combine(packageDir, "manifest.json");
        var manifest = new
        {
            packageId = slot.SlotId,
            hookId = slot.HookId,
            hookText = slot.HookText,
            pillar = slot.Pillar.ToString(),
            createdAt,
            instructions = "Post each platform folder manually, then confirm with: POST /api/outbox/{packageId}/{platform}/confirm",
            items = items.Select(i => new
            {
                platform = i.Platform,
                dimensions = $"{i.Width}x{i.Height}",
                media = Path.GetRelativePath(packageDir, i.MediaPath).Replace('\\', '/'),
                captionFile = $"{i.Platform}/caption.txt",
                titleFile = i.Title.Length > 0 ? $"{i.Platform}/title.txt" : null,
                confirmEndpoint = $"/api/outbox/{slot.SlotId}/{i.Platform}/confirm",
            }),
        };
        await File.WriteAllTextAsync(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented), ct);

        _log.LogInformation("Outbox package {PackageId} built with {Count} platform variants at {Dir}",
            slot.SlotId, items.Count, packageDir);

        return new OutboxPackage(slot.SlotId, slot.HookId, slot.HookText, slot.Pillar,
            createdAt, packageDir, manifestPath, items);
    }
}
