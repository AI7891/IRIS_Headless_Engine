// =============================================================================
//  Stage 2 — carousel image fetcher
//  Fetches subject-related stock photos from the Pexels API (free tier,
//  Pexels GmbH is EU/Germany-based — GDPR compliant) using the image queries
//  embedded in the AI-generated script, building one image per carousel slide.
//  Falls back to the existing ContentRenderer (Canva replacement) when the API
//  is unavailable or returns no match, so the pipeline never stalls on media.
// =============================================================================
using System.Text.Json;
using InnerShiftLab.Core;
using InnerShiftLab.Engine;

namespace InnerShiftLab.ContentCreator;

public interface IImageFetcher
{
    Task<IReadOnlyList<CarouselSlide>> FetchCarouselAsync(ContentScript script, CancellationToken ct = default);
}

public sealed class PexelsImageFetcher : IImageFetcher
{
    private const string SearchUrl = "https://api.pexels.com/v1/search";

    private readonly ContentCreatorSettings _settings;
    private readonly IHttpClientFactory _http;
    private readonly IContentRenderer _renderer;
    private readonly ILogger<PexelsImageFetcher> _log;
    private readonly string _imagesDir;

    public PexelsImageFetcher(ContentCreatorSettings settings, IHttpClientFactory http, IContentRenderer renderer,
        ILogger<PexelsImageFetcher> log, IWebHostEnvironment env)
    {
        _settings = settings; _http = http; _renderer = renderer; _log = log;
        var root = AppPaths.ResolveRoot(env.ContentRootPath);
        _imagesDir = Path.Combine(AppPaths.OutputDir(root), "creator", "images");
        Directory.CreateDirectory(_imagesDir);
    }

    public async Task<IReadOnlyList<CarouselSlide>> FetchCarouselAsync(ContentScript script, CancellationToken ct = default)
    {
        var results = new List<CarouselSlide>();
        var usedPhotoIds = new HashSet<long>();  // avoid the same photo on two slides

        for (var i = 0; i < script.Slides.Count; i++)
        {
            var slide = script.Slides[i];
            CarouselSlide? fetched = null;
            try
            {
                fetched = await FetchOneAsync(script.ScriptId, i, slide.ImageQuery, usedPhotoIds, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Pexels fetch failed for slide {Index} ('{Query}'); using rendered fallback", i, slide.ImageQuery);
            }

            // Never let a missing image break the carousel — render the slide text locally.
            fetched ??= new CarouselSlide
            {
                Index = i,
                ImagePath = await _renderer.RenderImageAsync(slide.OnScreenText,
                    Path.Combine(_imagesDir, $"{script.ScriptId}-{i}.png")),
                IsFallback = true,
            };
            results.Add(fetched);
        }
        return results;
    }

    private async Task<CarouselSlide?> FetchOneAsync(string scriptId, int index, string query,
        HashSet<long> usedPhotoIds, CancellationToken ct)
    {
        var apiKey = CreatorSecrets.Resolve(_settings.Pexels.ApiKey, envFallback: "PEXELS_API_KEY");
        if (apiKey is null)
        {
            _log.LogWarning("Pexels API key not configured; slide {Index} will use the rendered fallback", index);
            return null;
        }

        var client = _http.CreateClient("pexels");
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{SearchUrl}?query={Uri.EscapeDataString(query)}&per_page=5&orientation={_settings.Pexels.Orientation}");
        req.Headers.TryAddWithoutValidation("Authorization", apiKey);

        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("photos", out var photos) || photos.GetArrayLength() == 0)
            return null;

        foreach (var photo in photos.EnumerateArray())
        {
            var id = photo.GetProperty("id").GetInt64();
            if (!usedPhotoIds.Add(id)) continue;

            var src = photo.GetProperty("src");
            var url = src.TryGetProperty(_settings.Pexels.Size, out var sized)
                ? sized.GetString()
                : src.GetProperty("original").GetString();
            if (string.IsNullOrEmpty(url)) continue;

            var path = Path.Combine(_imagesDir, $"{scriptId}-{index}.jpg");
            var bytes = await client.GetByteArrayAsync(url, ct);
            await File.WriteAllBytesAsync(path, bytes, ct);

            var photographer = photo.TryGetProperty("photographer", out var p) ? p.GetString() : null;
            _log.LogInformation("Slide {Index}: fetched Pexels photo {PhotoId} for '{Query}'", index, id, query);
            return new CarouselSlide
            {
                Index = index,
                ImagePath = path,
                SourceUrl = photo.TryGetProperty("url", out var u) ? u.GetString() : null,
                PhotographerCredit = photographer is null ? null : $"Photo by {photographer} on Pexels",
            };
        }
        return null;
    }
}
