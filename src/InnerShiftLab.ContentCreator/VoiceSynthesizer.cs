// =============================================================================
//  Stage 3 — text-to-speech voiceover
//  Sends the same script used for the image carousel to the ElevenLabs
//  text-to-speech API, concatenating the per-slide voiceover segments in
//  carousel order so the narration follows the slide sequence.
// =============================================================================
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InnerShiftLab.Core;

namespace InnerShiftLab.ContentCreator;

public interface IVoiceSynthesizer
{
    /// <summary>Returns the path of the synthesized mp3 voiceover.</summary>
    Task<string> SynthesizeAsync(ContentScript script, CancellationToken ct = default);
}

public sealed class ElevenLabsVoiceSynthesizer : IVoiceSynthesizer
{
    private const string ApiBase = "https://api.elevenlabs.io/v1";

    private readonly ContentCreatorSettings _settings;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ElevenLabsVoiceSynthesizer> _log;
    private readonly string _audioDir;

    public ElevenLabsVoiceSynthesizer(ContentCreatorSettings settings, IHttpClientFactory http,
        ILogger<ElevenLabsVoiceSynthesizer> log, IWebHostEnvironment env)
    {
        _settings = settings; _http = http; _log = log;
        var root = AppPaths.ResolveRoot(env.ContentRootPath);
        _audioDir = Path.Combine(AppPaths.OutputDir(root), "creator", "audio");
        Directory.CreateDirectory(_audioDir);
    }

    public async Task<string> SynthesizeAsync(ContentScript script, CancellationToken ct = default)
    {
        var apiKey = CreatorSecrets.Resolve(_settings.ElevenLabs.ApiKey, envFallback: "ELEVENLABS_API_KEY")
            ?? throw new InvalidOperationException(
                "ElevenLabs API key not configured (ContentCreator:ElevenLabs:ApiKey or ELEVENLABS_API_KEY env var).");

        // The narration follows the carousel: slide voiceovers concatenated in order.
        var text = string.Join(" ", script.Slides.Select(s => s.Voiceover.Trim()).Where(s => s.Length > 0));
        if (text.Length == 0)
            throw new InvalidOperationException($"Script {script.ScriptId} has no voiceover text to synthesize.");

        var body = JsonSerializer.Serialize(new
        {
            text,
            model_id = _settings.ElevenLabs.ModelId,
            voice_settings = new
            {
                stability = _settings.ElevenLabs.Stability,
                similarity_boost = _settings.ElevenLabs.SimilarityBoost,
            },
        });

        var client = _http.CreateClient("elevenlabs");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/text-to-speech/{_settings.ElevenLabs.VoiceId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("xi-api-key", apiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"ElevenLabs TTS failed ({(int)resp.StatusCode}): {err}");
        }

        var path = Path.Combine(_audioDir, $"{script.ScriptId}.mp3");
        await File.WriteAllBytesAsync(path, await resp.Content.ReadAsByteArrayAsync(ct), ct);
        _log.LogInformation("Synthesized voiceover for script {ScriptId}: {Path} ({Chars} chars)", script.ScriptId, path, text.Length);
        return path;
    }
}
