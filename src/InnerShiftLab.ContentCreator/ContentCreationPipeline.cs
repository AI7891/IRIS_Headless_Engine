// =============================================================================
//  Content creation pipeline — orchestrates the four stages end to end:
//  1. AI script (Claude)  2. carousel images (Pexels)  3. voiceover (ElevenLabs)
//  4. compose (ffmpeg) — and hands the bound output to the existing publish
//  backend (ProviderRouter + MonetizationLogger).
// =============================================================================
using InnerShiftLab.Core;
using InnerShiftLab.Monetization;
using InnerShiftLab.Providers;

namespace InnerShiftLab.ContentCreator;

public interface IContentCreationPipeline
{
    Task<ComposedContent> CreateAsync(string? keywords = null, int? slideCount = null, CancellationToken ct = default);
    Task<IReadOnlyList<PostSlot>> CreateAndPublishAsync(string? keywords, string[] platforms, CancellationToken ct = default);
}

public sealed class ContentCreationPipeline : IContentCreationPipeline
{
    private readonly IScriptGenerator _scripts;
    private readonly IImageFetcher _images;
    private readonly IVoiceSynthesizer _voice;
    private readonly IContentComposer _composer;
    private readonly IProviderRouter? _router;
    private readonly IMonetizationLogger _monetization;
    private readonly ILogger<ContentCreationPipeline> _log;

    // The router is null when Features:AutoPublish is off — content creation still
    // works, but CreateAndPublishAsync is quarantined along with the providers.
    public ContentCreationPipeline(IScriptGenerator scripts, IImageFetcher images, IVoiceSynthesizer voice,
        IContentComposer composer, IProviderRouter? router, IMonetizationLogger monetization,
        ILogger<ContentCreationPipeline> log)
    {
        _scripts = scripts; _images = images; _voice = voice; _composer = composer;
        _router = router; _monetization = monetization; _log = log;
    }

    public async Task<ComposedContent> CreateAsync(string? keywords = null, int? slideCount = null, CancellationToken ct = default)
    {
        // 1. AI script from the topic keywords
        var script = await _scripts.GenerateAsync(keywords, slideCount, ct);
        // 2. subject-related images matching the injected script, one per slide
        var slides = await _images.FetchCarouselAsync(script, ct);
        // 3. the same script, spoken — voiceover follows the carousel sequence
        var audio = await _voice.SynthesizeAsync(script, ct);
        // 4. bind carousel + voiceover into the final output
        return await _composer.ComposeAsync(script, slides, audio, ct);
    }

    public async Task<IReadOnlyList<PostSlot>> CreateAndPublishAsync(string? keywords, string[] platforms, CancellationToken ct = default)
    {
        if (_router == null)
            throw new InvalidOperationException(
                "Automated publishing is disabled (Features:AutoPublish=false). Use the outbox workflow: POST /api/outbox/build.");

        var content = await CreateAsync(keywords, ct: ct);
        var posts = new List<PostSlot>();
        foreach (var platform in platforms)
        {
            // Image-first platforms get the lead carousel image; video platforms get
            // the composed slideshow with the voiceover track.
            var media = platform.ToLowerInvariant() is "instagram" or "facebook"
                ? content.SlideImagePaths.FirstOrDefault() ?? content.VideoPath
                : content.VideoPath;

            var post = await _router.PublishAsync(platform, new PublishRequest(
                HookId: $"creator-{content.ScriptId}",
                Pillar: nameof(Pillar.Integrate),
                Caption: content.Caption,
                MediaUrl: media));
            await _monetization.LogPostAsync(post);
            posts.Add(post);
            _log.LogInformation("Published creator content {ScriptId} to {Platform}: {PostId}",
                content.ScriptId, platform, post.PerPlatformPostIds.FirstOrDefault());
        }
        return posts;
    }
}
