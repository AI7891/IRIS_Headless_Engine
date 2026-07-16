// =============================================================================
//  Outbox package builder — turns one curated PostSlot into a ready-to-post
//  package: one platform-formatted media file + caption text file per platform,
//  plus a manifest.json, all under output/outbox/<date>/<packageId>/.
//  Every variant is also written to the SQLite outbox table (source of truth).
// =============================================================================
using InnerShiftLab.ContentCreator;
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

/// <summary>Pre-produced media (from the AI content pipeline) to package instead of text cards.</summary>
internal sealed record PackageMedia(string? ImagePath, string? VideoPath);

public interface IOutboxPackageBuilder
{
    /// <summary>
    /// Builds the package for one slot. <paramref name="platforms"/> restricts the
    /// variants; null means all configured platforms. When the content creator is
    /// enabled its composed media/caption/title are used; otherwise text cards are
    /// rendered.
    /// </summary>
    Task<OutboxPackage> BuildAsync(PostSlot slot, IReadOnlyCollection<string>? platforms = null,
        CancellationToken ct = default);
}

public sealed class OutboxPackageBuilder : IOutboxPackageBuilder
{
    private readonly IContentRenderer _renderer;
    private readonly IRepository _repo;
    private readonly OutboxSettings _settings;
    private readonly IrisSettings _iris;
    private readonly IContentCreationPipeline? _creator;
    private readonly string _outputRoot;
    private readonly ILogger<OutboxPackageBuilder> _log;

    public OutboxPackageBuilder(IContentRenderer renderer, IRepository repo, OutboxSettings settings,
        IrisSettings iris, IContentCreationPipeline? creator, string outputRoot, ILogger<OutboxPackageBuilder> log)
    {
        _renderer = renderer; _repo = repo; _settings = settings; _iris = iris;
        _creator = creator; _outputRoot = outputRoot; _log = log;
    }

    public async Task<OutboxPackage> BuildAsync(PostSlot slot, IReadOnlyCollection<string>? platforms = null,
        CancellationToken ct = default)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var packageDir = Path.Combine(_outputRoot, "outbox", createdAt.ToString("yyyy-MM-dd"), slot.SlotId);
        Directory.CreateDirectory(packageDir);

        var targets = _settings.EffectivePlatforms
            .Where(p => platforms == null || platforms.Contains(p, StringComparer.OrdinalIgnoreCase));
        var visualText = string.IsNullOrWhiteSpace(slot.HookText) ? slot.Caption : slot.HookText;
        var caption = slot.Caption;

        // AI content pipeline (opt-in). Any failure falls back to text-card rendering
        // — an external API outage must never sink the daily run.
        PackageMedia? media = null;
        if (_creator != null && _settings.UseContentCreator)
        {
            try
            {
                var content = await _creator.CreateAsync(keywords: slot.HookText, slideCount: null, ct);
                media = new PackageMedia(content.SlideImagePaths.FirstOrDefault(), content.VideoPath);
                if (!string.IsNullOrWhiteSpace(content.Caption))
                {
                    // Prefer the AI caption, but re-append the tracked link (shared format).
                    var link = UtmLinks.BuildTracked(_iris.LinktreeUrl, slot.HookId, slot.Pillar.ToString());
                    caption = $"{content.Caption}\n\n{link}";
                }
                if (!string.IsNullOrWhiteSpace(content.Title))
                    visualText = content.Title;
                _log.LogInformation("Outbox: using AI content {ScriptId} for package {PackageId}",
                    content.ScriptId, slot.SlotId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Content creator failed for {PackageId}; falling back to text cards", slot.SlotId);
                media = null;
            }
        }

        var items = new List<OutboxItem>();

        foreach (var platform in targets)
        {
            ct.ThrowIfCancellationRequested();
            var variant = PlatformFormatter.Format(platform, visualText, caption);
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
            // Non-clickable platforms (IG, TikTok): also drop the bare tracked URL as
            // link.txt so the operator can one-tap copy it into their bio/Linktree.
            // The caption keeps its "Link in bio →" cue and the URL below it.
            if (!format.LinksClickable && variant.Link.Length > 0)
                await File.WriteAllTextAsync(Path.Combine(platformDir, "link.txt"), variant.Link, ct);

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
                var format = PlatformFormats.Get(i.Platform);
                var titled = format.TitleMaxChars > 0;
                return new
                {
                    platform = i.Platform,
                    dimensions = $"{i.Width}x{i.Height}",
                    media = Path.GetRelativePath(packageDir, i.MediaPath).Replace('\\', '/'),
                    captionFile = titled ? null : $"{i.Platform}/caption.txt",
                    titleFile = i.Title.Length > 0 ? $"{i.Platform}/title.txt" : null,
                    descriptionFile = titled ? $"{i.Platform}/description.txt" : null,
                    linkFile = File.Exists(Path.Combine(packageDir, i.Platform, "link.txt")) ? $"{i.Platform}/link.txt" : null,
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
    /// Provides the platform's media. With AI content: video platforms get the
    /// composed video re-encoded to the profile, image platforms get the lead
    /// carousel slide cover-cropped to the profile. Otherwise (or on any resize
    /// failure) renders a text card, wrapping it into an mp4 for video platforms.
    /// </summary>
    private async Task<string> ProvideMediaAsync(PlatformFormat format, string platformDir,
        string visualText, PackageMedia? media)
    {
        if (media != null)
        {
            try
            {
                if (format.PrefersVideo && !string.IsNullOrEmpty(media.VideoPath) && File.Exists(media.VideoPath))
                    return await _renderer.ResizeVideoAsync(
                        media.VideoPath, Path.Combine(platformDir, "media.mp4"), format.Width, format.Height);

                var image = media.ImagePath ?? media.VideoPath;
                if (!format.PrefersVideo && !string.IsNullOrEmpty(image) && File.Exists(image))
                    return await _renderer.ResizeImageAsync(
                        image, Path.Combine(platformDir, "media.png"), format.Width, format.Height);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Resizing AI media for {Platform} failed; falling back to a text card", format.Platform);
            }
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
