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

/// <summary>
/// Pre-produced media to package instead of rendering text cards — used to route
/// the AI content pipeline's output (carousel image + composed video) to the outbox.
/// </summary>
public sealed record PackageMediaOverride(string? ImagePath, string? VideoPath);

public interface IOutboxPackageBuilder
{
    /// <summary>
    /// Builds the package for one slot. <paramref name="platforms"/> restricts the
    /// variants (used to honor the daily per-platform cap); null means all
    /// configured platforms. <paramref name="media"/> supplies pre-produced media;
    /// null renders text cards.
    /// </summary>
    Task<OutboxPackage> BuildAsync(PostSlot slot, IReadOnlyCollection<string>? platforms = null,
        PackageMediaOverride? media = null, CancellationToken ct = default);
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

    public async Task<OutboxPackage> BuildAsync(PostSlot slot, IReadOnlyCollection<string>? platforms = null,
        PackageMediaOverride? media = null, CancellationToken ct = default)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var packageDir = Path.Combine(_outputRoot, "outbox", createdAt.ToString("yyyy-MM-dd"), slot.SlotId);
        Directory.CreateDirectory(packageDir);

        var targets = _settings.EffectivePlatforms
            .Where(p => platforms == null || platforms.Contains(p, StringComparer.OrdinalIgnoreCase));
        var visualText = string.IsNullOrWhiteSpace(slot.HookText) ? slot.Caption : slot.HookText;
        var items = new List<OutboxItem>();

        foreach (var platform in targets)
        {
            ct.ThrowIfCancellationRequested();
            var variant = PlatformFormatter.Format(platform, visualText, slot.Caption);
            var format = variant.Format;
            var platformDir = Path.Combine(packageDir, format.Platform);
            Directory.CreateDirectory(platformDir);

            var mediaPath = await ProvideMediaAsync(format, platformDir, visualText, media);

            // Platforms with a separate title field (YouTube) get title.txt +
            // description.txt; everyone else gets caption.txt. The tracking link
            // lives inside the caption in all cases (below a bio cue where inert).
            var captionFileName = format.TitleMaxChars > 0 ? "description.txt" : "caption.txt";
            await File.WriteAllTextAsync(Path.Combine(platformDir, captionFileName), variant.Caption, ct);
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
                PackageDir = packageDir,
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
            items = items.Select(i =>
            {
                var titled = PlatformFormats.Get(i.Platform).TitleMaxChars > 0;
                return new
                {
                    platform = i.Platform,
                    dimensions = $"{i.Width}x{i.Height}",
                    media = Path.GetRelativePath(packageDir, i.MediaPath).Replace('\\', '/'),
                    captionFile = titled ? null : $"{i.Platform}/caption.txt",
                    titleFile = i.Title.Length > 0 ? $"{i.Platform}/title.txt" : null,
                    descriptionFile = titled ? $"{i.Platform}/description.txt" : null,
                    confirmEndpoint = $"/api/outbox/{slot.SlotId}/{i.Platform}/confirm",
                };
            }),
        };
        await File.WriteAllTextAsync(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented), ct);

        _log.LogInformation("Outbox package {PackageId} built with {Count} platform variants at {Dir}",
            slot.SlotId, items.Count, packageDir);

        return new OutboxPackage(slot.SlotId, slot.HookId, slot.HookText, slot.Pillar,
            createdAt, packageDir, manifestPath, items);
    }

    /// <summary>
    /// Copies pre-produced media into the platform folder when supplied (video-first
    /// platforms prefer the video, image platforms the image), otherwise renders a
    /// text card — and, for video platforms, wraps it into an mp4 when possible.
    /// </summary>
    private async Task<string> ProvideMediaAsync(PlatformFormat format, string platformDir,
        string visualText, PackageMediaOverride? media)
    {
        if (media != null)
        {
            var source = format.PrefersVideo
                ? (media.VideoPath ?? media.ImagePath)
                : (media.ImagePath ?? media.VideoPath);
            if (!string.IsNullOrEmpty(source) && File.Exists(source))
            {
                var dest = Path.Combine(platformDir, "media" + Path.GetExtension(source));
                File.Copy(source, dest, overwrite: true);
                return dest;
            }
            _log.LogWarning("Pre-produced media missing for {Platform} ({Source}); rendering a text card instead",
                format.Platform, source);
        }

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
                    "Video render failed for {Platform}; package ships the still image instead", format.Platform);
            }
        }
        return mediaPath;
    }
}
