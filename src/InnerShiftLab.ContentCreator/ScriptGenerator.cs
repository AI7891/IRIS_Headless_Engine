// =============================================================================
//  Stage 1 — AI script engine
//  Contacts the Claude API (official Anthropic C# SDK) and turns the configured
//  topic keywords into a structured carousel script: per-slide on-screen text,
//  an image search query, and a voiceover segment.
// =============================================================================
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using InnerShiftLab.Core;

namespace InnerShiftLab.ContentCreator;

public interface IScriptGenerator
{
    Task<ContentScript> GenerateAsync(string? keywords = null, int? slideCount = null, CancellationToken ct = default);
}

public sealed class AnthropicScriptGenerator : IScriptGenerator
{
    private readonly ContentCreatorSettings _settings;
    private readonly ILogger<AnthropicScriptGenerator> _log;
    private readonly AnthropicClient _client;

    public AnthropicScriptGenerator(ContentCreatorSettings settings, ILogger<AnthropicScriptGenerator> log)
    {
        _settings = settings;
        _log = log;
        _client = new AnthropicClient
        {
            ApiKey = CreatorSecrets.Resolve(settings.Anthropic.ApiKey, envFallback: "ANTHROPIC_API_KEY"),
        };
    }

    public async Task<ContentScript> GenerateAsync(string? keywords = null, int? slideCount = null, CancellationToken ct = default)
    {
        keywords ??= _settings.Keywords;
        var slides = slideCount ?? _settings.SlideCount;

        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = _settings.Anthropic.Model,
            MaxTokens = _settings.Anthropic.MaxTokens,
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = BuildSchema(slides) } },
            System = new List<TextBlockParam>
            {
                new()
                {
                    Text =
                        "You are the content engine for The Inner Shift Lab (@the_inner_shift_lab), a nervous-system " +
                        "and mind-body health brand built on the IRIS Method (Identify / Reprogram / Integrate / Stabilise). " +
                        "You write scientifically grounded, empathetic short-form social content. Never make medical claims " +
                        "or promise cures; frame everything as science-informed education and lived-experience support.",
                },
            },
            Messages =
            [
                new()
                {
                    Role = Role.User,
                    Content =
                        $"Write an Instagram/TikTok carousel script about the following topics: {keywords}.\n\n" +
                        $"Produce exactly {slides} slides. For each slide give:\n" +
                        "- onScreenText: a punchy on-image line (max 90 characters)\n" +
                        "- imageQuery: a 2-4 word stock-photo search query that visually matches the slide " +
                        "(concrete, photographable subjects only — e.g. 'skin closeup light', not abstract concepts)\n" +
                        "- voiceover: 1-2 spoken sentences continuing a single narrative across the slides, " +
                        "so the concatenated voiceovers read as one flowing 45-60 second script.\n\n" +
                        "Also give: title (internal working title), caption (the post caption, ending with a gentle " +
                        "call to action to the link in bio), and hashtags (8-12, without the # symbol).",
                },
            ],
        });

        var text = response.Content.Select(b => b.Value).OfType<TextBlock>().FirstOrDefault()?.Text
            ?? throw new InvalidOperationException("Claude returned no text content for the script request.");

        var script = JsonSerializer.Deserialize<ContentScript>(text, JsonOpts)
            ?? throw new InvalidOperationException("Failed to parse the generated script JSON.");
        script.Keywords = keywords;

        _log.LogInformation("Generated script {ScriptId} ('{Title}') with {Slides} slides", script.ScriptId, script.Title, script.Slides.Count);
        return script;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static Dictionary<string, JsonElement> BuildSchema(int slideCount) => new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            title = new { type = "string" },
            caption = new { type = "string" },
            hashtags = new { type = "array", items = new { type = "string" } },
            slides = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        onScreenText = new { type = "string" },
                        imageQuery = new { type = "string" },
                        voiceover = new { type = "string" },
                    },
                    required = new[] { "onScreenText", "imageQuery", "voiceover" },
                    additionalProperties = false,
                },
            },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "title", "caption", "hashtags", "slides" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };
}
