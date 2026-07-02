// =============================================================================
//  Provider Router — unified facade over Meta + TikTok + YouTube
// =============================================================================
using InnerShiftLab.Auth;
using InnerShiftLab.Core;
using InnerShiftLab.Engine;
using Microsoft.Extensions.Options;

namespace InnerShiftLab.Providers;

public interface IProviderRouter
{
    Task<ProviderStatus> GetStatusAsync();
    Task<PostSlot> PublishAsync(string platform, PublishRequest req);
    Task<PostSlot> DryRunAsync(PostSlot slot);
}

public sealed class ProviderRouter : IProviderRouter
{
    private readonly IMetaProvider _meta;
    private readonly ITiktokProvider _tt;
    private readonly IYoutubeProvider _yt;
    private readonly ITokenVault _vault;
    private readonly IContentRenderer _renderer;
    private readonly SocialsSettings _socials;
    private readonly ILogger<ProviderRouter> _log;

    public ProviderRouter(IMetaProvider meta, ITiktokProvider tt, IYoutubeProvider yt, ITokenVault vault, IContentRenderer renderer, IOptions<SocialsSettings> socials, ILogger<ProviderRouter> log)
    {
        _meta = meta; _tt = tt; _yt = yt; _vault = vault; _renderer = renderer; _socials = socials.Value; _log = log;
    }

    public async Task<ProviderStatus> GetStatusAsync()
    {
        var metaTask = SafeGet(_meta.GetTokenStatusAsync);
        var ttTask   = SafeGet(_tt.GetTokenStatusAsync);
        var ytTask   = SafeGet(_yt.GetTokenStatusAsync);
        await Task.WhenAll(metaTask, ttTask, ytTask);
        return new ProviderStatus
        {
            Meta     = metaTask.Result,
            Tiktok   = ttTask.Result,
            Youtube  = ytTask.Result,
        };
    }

    public async Task<PostSlot> PublishAsync(string platform, PublishRequest req)
    {
        platform = platform.ToLowerInvariant();
        return platform switch
        {
            "instagram" => await PublishInstagramAsync(await LoadTokens("meta") ?? throw new InvalidOperationException("Meta not authenticated."), req),
            "facebook"  => await PublishFacebookAsync(await LoadTokens("meta") ?? throw new InvalidOperationException("Meta not authenticated."), req),
            "tiktok"    => await PublishTikTokAsync(req),
            "youtube"   => await PublishYouTubeAsync(req),
            _ => throw new ArgumentException($"Unknown platform '{platform}'"),
        };
    }

    public Task<PostSlot> DryRunAsync(PostSlot slot)
    {
        // Returns a preview without actually publishing
        return Task.FromResult(slot);
    }

    private async Task<PostSlot> PublishInstagramAsync(TokenSet tokens, PublishRequest req)
    {
        if (string.IsNullOrEmpty(tokens.IgBusinessId) || string.IsNullOrEmpty(tokens.PageAccessToken))
            throw new InvalidOperationException("Meta tokens missing IG business id or page token. Re-authenticate via /auth/meta/login.");
        var media = await EnsureMediaAsync(req.MediaUrl, req.Caption, "instagram");
        // IG Graph publishing must use the page access token, not the user token.
        var id = await _meta.PublishInstagramMediaAsync(tokens.IgBusinessId, tokens.PageAccessToken, media, req.Caption);
        return new PostSlot
        {
            HookId = req.HookId, HookText = req.Caption, Caption = req.Caption,
            Pillar = Enum.TryParse<Pillar>(req.Pillar, true, out var p) ? p : Pillar.Integrate,
            Platforms = new[] { "instagram" },
            PerPlatformPostIds = new[] { id },
            PerPlatformUrls = new[] { $"https://instagram.com/p/{id}" },
            Status = PostStatus.Published,
            MediaUrl = media,
        };
    }

    private async Task<PostSlot> PublishFacebookAsync(TokenSet tokens, PublishRequest req)
    {
        if (string.IsNullOrEmpty(tokens.PageId) || string.IsNullOrEmpty(tokens.PageAccessToken))
            throw new InvalidOperationException("Meta page token missing. Re-authenticate via /auth/meta/login.");
        var id = await _meta.PublishFacebookPostAsync(tokens.PageId, tokens.PageAccessToken, req.Caption, link: null, imageUrl: req.MediaUrl);
        return new PostSlot
        {
            HookId = req.HookId, HookText = req.Caption, Caption = req.Caption,
            Pillar = Enum.TryParse<Pillar>(req.Pillar, true, out var p) ? p : Pillar.Integrate,
            Platforms = new[] { "facebook" },
            PerPlatformPostIds = new[] { id },
            PerPlatformUrls = new[] { $"https://facebook.com/{id}" },
            Status = PostStatus.Published,
        };
    }

    private async Task<PostSlot> PublishTikTokAsync(PublishRequest req)
    {
        var tokens = await LoadTokens("tiktok");
        if (tokens == null) throw new InvalidOperationException("TikTok not authenticated. Visit /auth/tiktok/login first.");
        var media = await EnsureMediaAsync(req.MediaUrl, req.Caption, "tiktok");
        // For TikTok we wrap the image into a short video. The ContentRenderer has RenderVideoAsync
        // but it requires an ffmpeg-installed env. If ffmpeg is missing we fall back to image URL.
        string videoUrl = media;
        if (File.Exists(media) && (media.EndsWith(".png") || media.EndsWith(".jpg")))
        {
            try
            {
                videoUrl = await _renderer.RenderVideoAsync(req.Caption, media, outPath: null, durationSec: 15);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Video render failed; falling back to image URL (TikTok may reject)");
            }
        }
        var id = await _tt.PublishVideoAsync(tokens.AccessToken, videoUrl, req.Caption);
        return new PostSlot
        {
            HookId = req.HookId, HookText = req.Caption, Caption = req.Caption,
            Pillar = Enum.TryParse<Pillar>(req.Pillar, true, out var p) ? p : Pillar.Integrate,
            Platforms = new[] { "tiktok" },
            PerPlatformPostIds = new[] { id },
            PerPlatformUrls = new[] { $"https://tiktok.com/@{(_socials.Tiktok.Handle?.TrimStart('@') ?? "theinnershiftlab")}/{id}" },
            Status = PostStatus.Published,
            MediaUrl = videoUrl,
        };
    }

    private async Task<PostSlot> PublishYouTubeAsync(PublishRequest req)
    {
        var tokens = await LoadTokens("youtube");
        if (tokens == null) throw new InvalidOperationException("YouTube not authenticated. Visit /auth/youtube/login first.");
        var media = await EnsureMediaAsync(req.MediaUrl, req.Caption, "youtube");
        // YouTube needs a video file path. If we have an image, render it to a video first.
        string videoPath = media;
        if (File.Exists(media) && (media.EndsWith(".png") || media.EndsWith(".jpg")))
        {
            try { videoPath = await _renderer.RenderVideoAsync(req.Caption, media, outPath: null, durationSec: 30); }
            catch (Exception ex) { _log.LogError(ex, "YouTube video render failed"); throw; }
        }
        if (!File.Exists(videoPath))
            throw new InvalidOperationException($"YouTube publish requires a video file at {videoPath}");
        var title = string.IsNullOrWhiteSpace(req.HookId) ? "Inner Shift Lab" : req.HookId;
        var id = await _yt.UploadVideoAsync(tokens.AccessToken, tokens.RefreshToken ?? "", videoPath, title, req.Caption);
        return new PostSlot
        {
            HookId = req.HookId, HookText = req.Caption, Caption = req.Caption,
            Pillar = Enum.TryParse<Pillar>(req.Pillar, true, out var p) ? p : Pillar.Integrate,
            Platforms = new[] { "youtube" },
            PerPlatformPostIds = new[] { id },
            PerPlatformUrls = new[] { $"https://youtu.be/{id}" },
            Status = PostStatus.Published,
            MediaUrl = videoPath,
        };
    }

    private async Task<TokenSet?> LoadTokens(string provider)
    {
        return await _vault.LoadTokensAsync(provider);
    }

    // Auto-generate media for platforms that require it. Falls back to a Canva-replacement
    // image render if no MediaUrl is provided — this is the "missing core logic" the user
    // warned about: never let a publish path fail because media is missing.
    private async Task<string> EnsureMediaAsync(string? existingMediaUrl, string caption, string platform)
    {
        if (!string.IsNullOrEmpty(existingMediaUrl)) return existingMediaUrl;
        // Render an image and return the path. For YouTube/TikTok we'll wrap it into a video downstream.
        return await _renderer.RenderImageAsync(caption);
    }

    private async Task<ProviderTokenStatus> SafeGet(Func<Task<(string Status, DateTimeOffset? ExpiresAt)>> f)
    {
        try
        {
            var (status, expiresAt) = await f();
            return new ProviderTokenStatus { Status = status, ExpiresAt = expiresAt };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Provider status check failed");
            return new ProviderTokenStatus { Status = "error" };
        }
    }
}

public sealed class ProviderTokenStatus
{
    public string Status { get; set; } = "unknown";
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class ProviderStatus
{
    // System.Text.Json does not serialize ValueTuple members, so use plain properties.
    public ProviderTokenStatus Meta { get; set; } = new();
    public ProviderTokenStatus Tiktok { get; set; } = new();
    public ProviderTokenStatus Youtube { get; set; } = new();
}






