// =============================================================================
//  YouTube Provider — Data API v3 (resumable upload)
//  Auth: OAuth2 (client_id/secret) -> token.json -> refresh
// =============================================================================
using InnerShiftLab.Core;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Text;

namespace InnerShiftLab.Providers;

public interface IYoutubeProvider
{
    string BuildAuthorizationUrl(string? state = null);
    Task<TokenSet> ExchangeCodeAsync(string code);
    Task<TokenSet> RefreshTokenAsync(string refreshToken);
    Task<string> UploadVideoAsync(string accessToken, string refreshToken, string videoPath, string title, string description, string[]? tags = null);
    Task<(string Status, DateTimeOffset? ExpiresAt)> GetTokenStatusAsync();
}

public sealed class YouTubeProvider : IYoutubeProvider
{
    private readonly SocialsSettings _socials;
    private readonly ITokenVault _vault;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<YouTubeProvider> _log;

    public YouTubeProvider(IOptions<SocialsSettings> s, ITokenVault vault, IHttpClientFactory http, ILogger<YouTubeProvider> log)
    {
        _socials = s.Value; _vault = vault; _http = http; _log = log;
    }

    public string BuildAuthorizationUrl(string? state = null)
    {
        var yt = _socials.Youtube;
        if (string.IsNullOrEmpty(yt.ClientId) || string.IsNullOrEmpty(yt.RedirectUri))
            throw new InvalidOperationException("YouTube ClientId/RedirectUri not configured");
        // Standard YouTube upload scope + force SSL
        var scope = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.force-ssl";
        var oauth = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = yt.ClientId, ClientSecret = yt.ClientSecret },
            Scopes = new[] { YouTubeService.Scope.YoutubeUpload, YouTubeService.Scope.YoutubeForceSsl },
        });
        var redirect = oauth.CreateAuthorizationCodeRequest(yt.RedirectUri).Build();
        return redirect.ToString();
    }

    public async Task<TokenSet> ExchangeCodeAsync(string code)
    {
        var yt = _socials.Youtube;
        var oauth = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = yt.ClientId, ClientSecret = yt.ClientSecret },
            Scopes = new[] { YouTubeService.Scope.YoutubeUpload, YouTubeService.Scope.YoutubeForceSsl },
        });
        var token = await oauth.ExchangeCodeForTokenAsync("user", code, yt.RedirectUri, CancellationToken.None);
        return new TokenSet
        {
            AccessToken = token.AccessToken ?? throw new InvalidOperationException("YouTube exchange missing access_token"),
            TokenType = "Bearer",
            ExpiresAt = token.ExpiresInSeconds.HasValue
                ? DateTimeOffset.UtcNow.AddSeconds(token.ExpiresInSeconds.Value)
                : DateTimeOffset.UtcNow.AddHours(1),
        };
    }

    public async Task<TokenSet> RefreshTokenAsync(string refreshToken)
    {
        var yt = _socials.Youtube;
        var oauth = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = yt.ClientId, ClientSecret = yt.ClientSecret },
            Scopes = new[] { YouTubeService.Scope.YoutubeUpload, YouTubeService.Scope.YoutubeForceSsl },
        });
        var token = await oauth.RefreshTokenAsync("user", refreshToken, CancellationToken.None);
        return new TokenSet
        {
            AccessToken = token.AccessToken ?? throw new InvalidOperationException("YouTube refresh missing access_token"),
            TokenType = "Bearer",
            ExpiresAt = token.ExpiresInSeconds.HasValue
                ? DateTimeOffset.UtcNow.AddSeconds(token.ExpiresInSeconds.Value)
                : DateTimeOffset.UtcNow.AddHours(1),
        };
    }

    public async Task<string> UploadVideoAsync(string accessToken, string refreshToken, string videoPath, string title, string description, string[]? tags = null)
    {
        var yt = _socials.Youtube;
        if (!File.Exists(videoPath)) throw new FileNotFoundException("Video file not found", videoPath);

        // If the access token is within 5 min of expiry, refresh first.
        var vaultJson = await _vault.LoadRawAsync("youtube");
        TokenSet? stored = null;
        if (!string.IsNullOrEmpty(vaultJson))
            stored = JsonConvert.DeserializeObject<TokenSet>(vaultJson);
        var token = accessToken;
        if (stored != null && stored.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(5) && !string.IsNullOrEmpty(stored.AccessToken))
        {
            // We don't have a refresh token stored currently (TokenSet has no RefreshToken field).
            // Caller must re-auth via /auth/youtube/login if expired.
            _log.LogWarning("YouTube token near expiry ({Expiry}); refresh not yet implemented at vault level", stored.ExpiresAt);
        }

        var initializer = new BaseClientService.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = yt.ClientId, ClientSecret = yt.ClientSecret },
            HttpClientInitializer = new GoogleCredentialFromToken(token),
            ApplicationName = "InnerShiftLab-IRIS",
        };
        using var service = new YouTubeService(initializer);

        var video = new Video
        {
            Snippet = new VideoSnippet
            {
                Title = title.Length > 100 ? title.Substring(0, 100) : title,
                Description = description,
                Tags = tags ?? new[] { "IRIS Method", "The Inner Shift Lab", "Vitiligo Healing", "Trauma" },
                CategoryId = "22", // People & Blogs
                DefaultLanguage = "en",
                DefaultAudioLanguage = "en",
            },
            Status = new VideoStatus { PrivacyStatus = "unlisted" },  // safer start; flip to public after verification
        };

        using var fs = new FileStream(videoPath, FileMode.Open, FileAccess.Read);
        var upload = service.Videos.Insert(video, "snippet,status", fs, "video/*");
        upload.ProgressChanged += (p) => _log.LogDebug("YouTube upload progress: {Status} {BytesSent}", p.Status, p.BytesSent);
        upload.ResponseReceived += (v) => _log.LogInformation("YouTube upload complete: {Id} {Url}", v.Id, $"https://youtu.be/{v.Id}");

        var result = await upload.UploadAsync();
        if (result.Status == UploadStatus.Failed)
            throw new InvalidOperationException($"YouTube upload failed: {result.Exception?.Message}");
        if (result.Status == UploadStatus.NotStarted) throw new InvalidOperationException("YouTube upload did not start");
        return result.Id ?? throw new InvalidOperationException("YouTube upload returned no ID");
    }

    public async Task<(string Status, DateTimeOffset? ExpiresAt)> GetTokenStatusAsync()
    {
        var tokensJson = await _vault.LoadTokensAsync("youtube");
        if (string.IsNullOrEmpty(tokensJson)) return ("no_token", null);
        var set = JsonConvert.DeserializeObject<TokenSet>(tokensJson);
        if (set == null) return ("invalid", null);
        if (set.ExpiresAt < DateTimeOffset.UtcNow) return ("expired", set.ExpiresAt);
        return ("valid", set.ExpiresAt);
    }

    // Custom credential that just wraps an access token
    private sealed class GoogleCredentialFromToken : Google.Apis.Http.IConfigurableHttpClientInitializer
    {
        private readonly string _token;
        public GoogleCredentialFromToken(string token) { _token = token; }
        public void Initialize(Google.Apis.Http.ConfigurableHttpClient httpClient)
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        }
    }
}

