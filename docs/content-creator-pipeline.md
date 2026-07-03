# Content Creator Pipeline

Fully automated content creation, layered on top of the existing IRIS broadcast backend.
Four stages, each a separate dependency under `src/InnerShiftLab.ContentCreator/`:

| Stage | Dependency | API | Output |
|---|---|---|---|
| 1. Script | `AnthropicScriptGenerator` (`IScriptGenerator`) | Claude API (official Anthropic C# SDK, `claude-opus-4-8`, structured JSON output) | `ContentScript` — title, caption, hashtags, and per-slide `{onScreenText, imageQuery, voiceover}` |
| 2. Images | `PexelsImageFetcher` (`IImageFetcher`) | Pexels (free tier: 200 req/h, 20k/month; Pexels GmbH is a German/EU company — GDPR compliant) | One subject-related photo per slide, matched to each slide's `imageQuery`. Falls back to the local `ContentRenderer` if the API has no match |
| 3. Voice | `ElevenLabsVoiceSynthesizer` (`IVoiceSynthesizer`) | ElevenLabs text-to-speech (free tier ~10k credits/month) | mp3 voiceover — the same script's per-slide voiceovers concatenated in carousel order |
| 4. Compose | `FfmpegContentComposer` (`IContentComposer`) | ffmpeg (local) | `ComposedContent` — the carousel images + voiceover bound into one 1080×1080 mp4 (each slide shown for an equal share of the narration), plus the raw images/audio and the persisted script JSON |

`ContentCreationPipeline` orchestrates all four stages and hands the result to the
existing backend: `ProviderRouter.PublishAsync` (Meta / TikTok / YouTube) and
`MonetizationLogger.LogPostAsync` (UTM/conversion tracking). Image-first platforms
(Instagram, Facebook) receive the lead carousel image; video platforms (TikTok,
YouTube) receive the composed slideshow video with the voiceover track.

## Configuration (`appsettings.json` → `ContentCreator`)

```jsonc
"ContentCreator": {
  "Keywords": "psycho-dermatology, holistic Vitiligo regulation, somatic and epigenetic science proven impacts on the body's overall health",
  "SlideCount": 5,
  "Anthropic":  { "ApiKey": "...", "Model": "claude-opus-4-8", "MaxTokens": 16000 },
  "Pexels":     { "ApiKey": "...", "Orientation": "square", "Size": "large" },
  "ElevenLabs": { "ApiKey": "...", "VoiceId": "21m00Tcm4TlvDq8ikWAM", "ModelId": "eleven_multilingual_v2" }
}
```

- **Anthropic key**: leave the config value as-is and set the `ANTHROPIC_API_KEY`
  environment variable instead (recommended; the generator falls back to it).
- **Pexels key**: free at https://www.pexels.com/api/
- **ElevenLabs key**: free at https://elevenlabs.io (profile → API keys)

## Endpoints

| Endpoint | Body | Does |
|---|---|---|
| `POST /api/creator/script` | `{ "keywords"?, "slideCount"? }` | Stage 1 only — returns the generated script |
| `POST /api/creator/run` | `{ "keywords"?, "slideCount"? }` | Full pipeline — returns `ComposedContent` (no publishing) |
| `POST /api/creator/publish` | `{ "keywords"?, "platforms": ["instagram","tiktok",...] }` | Full pipeline, then publishes via the existing provider backend and logs monetization |

Omitted `keywords` fall back to the configured default topic.

## Artifacts on disk

Everything lands under `output/creator/` (resolved via `AppPaths`, same as the rest
of the pipeline): `scripts/{id}.json`, `images/{id}-{n}.jpg`, `audio/{id}.mp3`,
`final/{id}.mp4`.
