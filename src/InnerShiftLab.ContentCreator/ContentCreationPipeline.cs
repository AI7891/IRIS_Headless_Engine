// =============================================================================
//  Content creation pipeline — orchestrates the four stages end to end:
//  1. AI script (Claude)  2. carousel images (Pexels)  3. voiceover (ElevenLabs)
//  4. compose (ffmpeg) — then hands the bound output to the approval queue as
//  PendingApproval drafts. Nothing produced here is ever published without an
//  explicit human approval (see InnerShiftLab.Drafts.DraftService).
// =============================================================================
using InnerShiftLab.Core;

namespace InnerShiftLab.ContentCreator;

public interface IContentCreationPipeline
{
    Task<ComposedContent> CreateAsync(string? keywords = null, int? slideCount = null, CancellationToken ct = default);
    Task<IReadOnlyList<PostDraft>> CreateDraftsAsync(string? keywords, string[] platforms, CancellationToken ct = default);
}

public sealed class ContentCreationPipeline : IContentCreationPipeline
{
    private readonly IScriptGenerator _scripts;
    private readonly IImageFetcher _images;
    private readonly IVoiceSynthesizer _voice;
    private readonly IContentComposer _composer;
    private readonly IDraftRepository _drafts;
    private readonly ILogger<ContentCreationPipeline> _log;

    public ContentCreationPipeline(IScriptGenerator scripts, IImageFetcher images, IVoiceSynthesizer voice,
        IContentComposer composer, IDraftRepository drafts, ILogger<ContentCreationPipeline> log)
    {
        _scripts = scripts; _images = images; _voice = voice; _composer = composer;
        _drafts = drafts; _log = log;
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

    public async Task<IReadOnlyList<PostDraft>> CreateDraftsAsync(string? keywords, string[] platforms, CancellationToken ct = default)
    {
        var content = await CreateAsync(keywords, ct: ct);
        var created = new List<PostDraft>();
        foreach (var platform in platforms)
        {
            // Image-first platforms get the lead carousel image; video platforms get
            // the composed slideshow with the voiceover track.
            var media = platform.ToLowerInvariant() is "instagram" or "facebook"
                ? content.SlideImagePaths.FirstOrDefault() ?? content.VideoPath
                : content.VideoPath;

            var draft = await _drafts.CreateAsync(new PostDraft
            {
                Platform = platform,
                Caption = content.Caption,
                MediaReference = media,
                Status = DraftStatus.PendingApproval,
            });
            created.Add(draft);
            _log.LogInformation("Creator content {ScriptId} drafted for {Platform} as draft {Id} — awaiting approval",
                content.ScriptId, platform, draft.Id);
        }
        return created;
    }
}
