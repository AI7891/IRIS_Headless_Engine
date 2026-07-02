// =============================================================================
//  TikTok Provider — Content Posting API
//  Auth: client_key/secret -> 2h token -> direct post (no user timeline required for direct post)
// =============================================================================
using InnerShiftLab.Auth;
using InnerShiftLab.Core;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Text;

namespace InnerShiftLab.Providers;

public interface ITiktokProvider
{
    string BuildAuthorizationUrl(string? state = null);
    Task<TokenSet> ExchangeCodeAsync(string code);
    Task<TokenSet> RefreshTokenAsync(string refreshToken);
    Task<string> PublishVideoAsync(string accessToken, string videoUrl, string caption);
    Task<(string Status, DateTimeOffset? ExpiresAt)> GetTokenStatusAsync();
}

public sealed class TikTokProvider : ITiktokProvider
{
    private readonly SocialsSettings _socials;
    private readonly IHttpClientFactory _http;
    private readonly ITokenVault _vault;
    private readonly ILogger<TikTokProvider> _log;

    public TikTokProvider(IOptions<SocialsSettings> s, IHttpClientFactory http, ITokenVault vault, ILogger<TikTokProvider> log)
    {
        _socials = s.Value; _http = http; _vault = vault; _log = log;
    }

    public string BuildAuthorizationUrl(string? state = null)
    {
        var tt = _socials.Tiktok;
        if (string.IsNullOrEmpty(tt.ClientKey) || string.IsNullOrEmpty(tt.RedirectUri))
            throw new InvalidOperationException("TikTok ClientKey/RedirectUri not configured");
        var scope = "user.info.basic,video.publish,video.upload";
        var csrf = state ?? Guid.NewGuid().ToString("N");
        return $"https://www.tiktok.com/v2/auth/authorize/?client_key={Uri.EscapeDataString(tt.ClientKey)}" +
               $"&scope={Uri.EscapeDataString(scope)}" +
               $"&response_type=code" +
               $"&redirect_uri={Uri.EscapeDataString(tt.RedirectUri)}" +
               $"&state={csrf}";
    }

    public async Task<TokenSet> ExchangeCodeAsync(string code)
    {
        var tt = _socials.Tiktok;
        var url = "https://open.tiktokapis.com/v2/oauth/token/";
        var http = _http.CreateClient("tiktok");
        var body = new Dictionary<string, string>
        {
            ["client_key"] = tt.ClientKey,
            ["client_secret"] = tt.ClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = tt.RedirectUri,
        };
        var resp = await http.PostAsync(url, new FormUrlEncodedContent(body));
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"TikTok token exchange failed: {raw}");
        var tok = JsonConvert.DeserializeObject<TiktokTokenResponse>(raw)
            ?? throw new InvalidOperationException("TikTok token deserialization failed");
        return new TokenSet
        {
            AccessToken = tok.AccessToken ?? throw new InvalidOperationException("TikTok response missing access_token"),
            TokenType = "Bearer",
            RefreshToken = tok.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tok.ExpiresIn ?? 7200),
        };
    }

    public async Task<TokenSet> RefreshTokenAsync(string refreshToken)
    {
        var tt = _socials.Tiktok;
        var http = _http.CreateClient("tiktok");
        var body = new Dictionary<string, string>
        {
            ["client_key"] = tt.ClientKey,
            ["client_secret"] = tt.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };
        var resp = await http.PostAsync("https://open.tiktokapis.com/v2/oauth/token/", new FormUrlEncodedContent(body));
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"TikTok token refresh failed: {raw}");
        var tok = JsonConvert.DeserializeObject<TiktokTokenResponse>(raw)
            ?? throw new InvalidOperationException("TikTok refresh deserialization failed");
        return new TokenSet
        {
            AccessToken = tok.AccessToken ?? throw new InvalidOperationException("TikTok refresh missing access_token"),
            TokenType = "Bearer",
            // TikTok rotates the refresh token on each refresh; fall back to the old one if absent.
            RefreshToken = tok.RefreshToken ?? refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tok.ExpiresIn ?? 7200),
        };
    }

    public async Task<string> PublishVideoAsync(string accessToken, string videoUrl, string caption)
    {
        var http = _http.CreateClient("tiktok");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        // TikTok requires PULL_FROM_URL — but for testing we use FILE_UPLOAD; PULL is recommended.
        var init = new
        {
            post_info = new
            {
                title = caption.Length > 2200 ? caption.Substring(0, 2200) : caption,
                privacy_level = "SELF_ONLY",  // safe default; switch to PUBLIC after testing
                disable_duet = false,
                disable_comment = false,
                disable_stitch = false,
                video_cover_timestamp_ms = 1000,
            },
            source_info = new
            {
                source = "PULL_FROM_URL",
                video_url = videoUrl,
            },
        };
        var initJson = JsonConvert.SerializeObject(init);
        var initResp = await http.PostAsync("https://open.tiktokapis.com/v2/post/publish/video/init/",
            new StringContent(initJson, Encoding.UTF8, "application/json"));
        var initRaw = await initResp.Content.ReadAsStringAsync();
        if (!initResp.IsSuccessStatusCode) throw new InvalidOperationException($"TikTok init failed: {initRaw}");
        var initParsed = JsonConvert.DeserializeObject<TiktokPublishInitResponse>(initRaw)
            ?? throw new InvalidOperationException("TikTok init deserialization failed");
        return initParsed.Data?.PublishId ?? throw new InvalidOperationException("TikTok init missing publish_id");
    }

    public async Task<(string Status, DateTimeOffset? ExpiresAt)> GetTokenStatusAsync()
    {
        var set = await _vault.LoadTokensAsync("tiktok");
        if (set == null) return ("no_token", null);
        if (set.ExpiresAt < DateTimeOffset.UtcNow) return ("expired", set.ExpiresAt);
        return ("valid", set.ExpiresAt);
    }

    private sealed class TiktokTokenResponse
    {
        [JsonProperty("access_token")] public string? AccessToken { get; set; }
        [JsonProperty("refresh_token")] public string? RefreshToken { get; set; }
        [JsonProperty("expires_in")] public long? ExpiresIn { get; set; }
        [JsonProperty("open_id")] public string? OpenId { get; set; }
    }
    private sealed class TiktokPublishInitResponse
    {
        [JsonProperty("data")] public TiktokPublishData? Data { get; set; }
    }
    private sealed class TiktokPublishData
    {
        [JsonProperty("publish_id")] public string? PublishId { get; set; }
    }
}
