// =============================================================================
//  Content Creator — models & settings
//  Four-stage pipeline: AI script -> stock-image carousel -> text-to-speech
//  voiceover -> composed output ready for the existing publish backend.
// =============================================================================
namespace InnerShiftLab.ContentCreator;

public sealed class ContentCreatorSettings
{
    /// <summary>Default topic keywords the AI script engine writes about.</summary>
    public string Keywords { get; set; } =
        "psycho-dermatology, holistic Vitiligo regulation, somatic and epigenetic science proven impacts on the body's overall health";

    /// <summary>Number of carousel slides per piece of content.</summary>
    public int SlideCount { get; set; } = 5;

    public AnthropicSettings  Anthropic  { get; set; } = new();
    public PexelsSettings     Pexels     { get; set; } = new();
    public ElevenLabsSettings ElevenLabs { get; set; } = new();
}

public sealed class AnthropicSettings
{
    /// <summary>Claude API key. Falls back to the ANTHROPIC_API_KEY env var when empty.</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-opus-4-8";
    public int MaxTokens { get; set; } = 16000;
}

public sealed class PexelsSettings
{
    /// <summary>
    /// Pexels API key (https://www.pexels.com/api/). Chosen for the image stage because
    /// Pexels GmbH is a German (EU) company — GDPR-compliant by jurisdiction — and its
    /// free tier (200 req/h, 20k req/month) is the most generous of the stock photo APIs.
    /// </summary>
    public string ApiKey { get; set; } = "";
    public string Orientation { get; set; } = "square";  // matches the 1080x1080 IG format
    public string Size { get; set; } = "large";
}

public sealed class ElevenLabsSettings
{
    /// <summary>ElevenLabs API key (free tier: ~10k credits/month).</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>Voice to synthesize with. Default is the pre-made "Rachel" voice.</summary>
    public string VoiceId { get; set; } = "21m00Tcm4TlvDq8ikWAM";
    public string ModelId { get; set; } = "eleven_multilingual_v2";
    public double Stability { get; set; } = 0.5;
    public double SimilarityBoost { get; set; } = 0.75;
}

// -----------------------------------------------------------------------------
//  Pipeline artifacts
// -----------------------------------------------------------------------------

/// <summary>The structured script produced by the AI content engine (stage 1).</summary>
public sealed class ContentScript
{
    public string ScriptId { get; set; } = Guid.NewGuid().ToString("N");
    public string Keywords { get; set; } = "";
    public string Title { get; set; } = "";
    public string Caption { get; set; } = "";
    public string[] Hashtags { get; set; } = Array.Empty<string>();
    public List<ScriptSlide> Slides { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One carousel slide: what's shown, what image to fetch, what's spoken.</summary>
public sealed class ScriptSlide
{
    public string OnScreenText { get; set; } = "";
    /// <summary>Stock-photo search query matching this slide's message.</summary>
    public string ImageQuery { get; set; } = "";
    /// <summary>Voiceover segment for this slide (spoken in carousel order).</summary>
    public string Voiceover { get; set; } = "";
}

/// <summary>One fetched carousel image (stage 2).</summary>
public sealed class CarouselSlide
{
    public int Index { get; set; }
    public string ImagePath { get; set; } = "";
    public string? SourceUrl { get; set; }
    public string? PhotographerCredit { get; set; }
    /// <summary>True when the image came from the local ContentRenderer fallback instead of the API.</summary>
    public bool IsFallback { get; set; }
}

/// <summary>Final bound output (stage 4) — everything the existing backend needs to publish.</summary>
public sealed class ComposedContent
{
    public string ScriptId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Caption { get; set; } = "";
    /// <summary>Carousel image paths in slide order (for image-based platforms).</summary>
    public string[] SlideImagePaths { get; set; } = Array.Empty<string>();
    /// <summary>Voiceover audio track (mp3).</summary>
    public string AudioPath { get; set; } = "";
    /// <summary>Carousel slideshow + voiceover bound into one mp4 (for video platforms).</summary>
    public string VideoPath { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string ScriptPath { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
