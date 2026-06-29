// =============================================================================
//  Meta Provider — Instagram Graph + Facebook Pages
//  Full OAuth: dialog -> callback -> long-lived token -> IG user ID -> page token fanout
// =============================================================================
using InnerShiftLab.Core;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace InnerShiftLab.Providers;

public interface IMetaProvider
{
    string BuildAuthorizationUrl(string? state = null);
    Task<TokenSet> ExchangeCodeAsync(string code);
    Task<TokenSet> ExchangeForLongLivedAsync(string shortLivedToken);
    Task<InstagramUser> ResolveInstagramUserAsync(string accessToken);
    Task<PageInfo> ResolvePageAsync(string pageId, string accessToken);
    Task<string> PublishInstagramMediaAsync(string igUserId, string accessToken, string imageUrl, string caption, string? videoUrl = null);
    Task<string> PublishFacebookPostAsync(string pageId, string pageAccessToken, string message, string? link = null, string? imageUrl = null);
    Task<(string Status, DateTimeOffset? ExpiresAt)> GetTokenStatusAsync();
}

public sealed class MetaProvider : IMetaProvider
{
    private readonly SocialsSettings _socials;
    private readonly MonetizationSettings _mon;
    private readonly IHttpClientFactory _http;
    private readonly ITokenVault _vault;
    private readonly ILogger<MetaProvider> _log;

    public MetaProvider(IOptions<SocialsSettings> s, IOptions<MonetizationSettings> m,
        IHttpClientFactory http, ITokenVault vault, ILogger<MetaProvider> log)
    {
        _socials = s.Value;
        _mon = m.Value;
        _http = http;
        _vault = vault;
        _log = log;
    }

    public string BuildAuthorizationUrl(string? state = null)
    {
        var ig = _socials.Instagram;
        if (string.IsNullOrEmpty(ig.AppId) || string.IsNullOrEmpty(ig.RedirectUri))
            throw new InvalidOperationException("Instagram AppId/RedirectUri not configured");

        // Scopes: pages_show_list + business_management + instagram_basic + instagram_content_publish
        // + pages_manage_posts + pages_read_engagement (for analytics)
        var scope = "public_profile,pages_show_list,pages_read_engagement,pages_manage_posts,business_management,instagram_basic,instagram_content_publish";
        var qb = HttpUtility.ParseQueryString(string.Empty);
        qb["client_id"] = ig.AppId;
        qb["redirect_uri"] = ig.RedirectUri;
        qb["state"] = state ?? Guid.NewGuid().ToString("N");
        qb["scope"] = scope;
        qb["response_type"] = "code";
        return $"https://www.facebook.com/v21.0/dialog/oauth?{qb}";
    }

    public async Task<TokenSet> ExchangeCodeAsync(string code)
    {
        var ig = _socials.Instagram;
        if (string.IsNullOrEmpty(ig.AppId) || string.IsNullOrEmpty(ig.AppSecret) || string.IsNullOrEmpty(ig.RedirectUri))
            throw new InvalidOperationException("Instagram credentials not fully configured");

        var url = $"https://graph.facebook.com/v21.0/oauth/access_token" +
                  $"?client_id={Uri.EscapeDataString(ig.AppId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(ig.RedirectUri)}" +
                  $"&client_secret={Uri.EscapeDataString(ig.AppSecret)}" +
                  $"&code={Uri.EscapeDataString(code)}";
        var http = _http.CreateClient("meta");
        var resp = await http.GetStringAsync(url);
        var tok = JsonConvert.DeserializeObject<TokenResponse>(resp)
            ?? throw new InvalidOperationException("Meta token response deserialization failed");
        return tok.ToTokenSet();
    }

    public async Task<TokenSet> ExchangeForLongLivedAsync(string shortLivedToken)
    {
        var ig = _socials.Instagram;
        var url = $"https://graph.facebook.com/v21.0/oauth/access_token" +
                  $"?grant_type=fb_exchange_token" +
                  $"&client_id={Uri.EscapeDataString(ig.AppId)}" +
                  $"&client_secret={Uri.EscapeDataString(ig.AppSecret)}" +
                  $"&fb_exchange_token={Uri.EscapeDataString(shortLivedToken)}";
        var http = _http.CreateClient("meta");
        var resp = await http.GetStringAsync(url);
        var tok = JsonConvert.DeserializeObject<TokenResponse>(resp)
            ?? throw new InvalidOperationException("Long-lived token exchange failed");
        return tok.ToTokenSet();
    }

    public async Task<InstagramUser> ResolveInstagramUserAsync(string accessToken)
    {
        // Resolves the IG business account ID that owns this user
        var url = "https://graph.facebook.com/v21.0/me/accounts?fields=instagram_business_account{id,username},access_token&access_token="
                  + Uri.EscapeDataString(accessToken);
        var http = _http.CreateClient("meta");
        var resp = await http.GetStringAsync(url);
        var parsed = JsonConvert.DeserializeObject<PagesListResponse>(resp)
            ?? throw new InvalidOperationException("Pages list deserialization failed");
        var firstPage = parsed.Data?.FirstOrDefault(p => p.InstagramBusinessAccount != null)
            ?? throw new InvalidOperationException("No Instagram business account linked to any managed page");
        return new InstagramUser(
            firstPage.InstagramBusinessAccount!.Id,
            firstPage.InstagramBusinessAccount.Username,
            firstPage.AccessToken ?? accessToken,
            firstPage.Id);
    }

    public async Task<PageInfo> ResolvePageAsync(string pageId, string accessToken)
    {
        var url = $"https://graph.facebook.com/v21.0/{Uri.EscapeDataString(pageId)}" +
                  $"?fields=id,name,access_token,instagram_business_account{{id,username}}" +
                  $"&access_token={Uri.EscapeDataString(accessToken)}";
        var http = _http.CreateClient("meta");
        var resp = await http.GetStringAsync(url);
        var page = JsonConvert.DeserializeObject<PageInfo>(resp)
            ?? throw new InvalidOperationException("Page info deserialization failed");
        return page;
    }

    public async Task<string> PublishInstagramMediaAsync(string igUserId, string accessToken, string imageUrl, string caption, string? videoUrl = null)
    {
        var http = _http.CreateClient("meta");
        // Step 1: create container
        var containerParams = new Dictionary<string, string>
        {
            ["caption"] = caption,
            ["access_token"] = accessToken,
        };
        if (!string.IsNullOrEmpty(videoUrl))
        {
            containerParams["media_type"] = "REELS";
            containerParams["video_url"] = videoUrl;
            containerParams["share_to_feed"] = "true";
        }
        else
        {
            containerParams["image_url"] = imageUrl;
        }

        var createUrl = $"https://graph.facebook.com/v21.0/{Uri.EscapeDataString(igUserId)}/media";
        var createResp = await http.PostAsync(createUrl, new FormUrlEncodedContent(containerParams));
        var createBody = await createResp.Content.ReadAsStringAsync();
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"IG container creation failed: {createBody}");

        var container = JsonConvert.DeserializeObject<MediaContainerResponse>(createBody)
            ?? throw new InvalidOperationException("Container response deserialization failed");
        if (string.IsNullOrEmpty(container.Id))
            throw new InvalidOperationException("Container response missing ID");

        // Step 2: poll until ready (REELS require processing). Exponential backoff up to 5 min total.
        if (!string.IsNullOrEmpty(videoUrl))
        {
            var totalWaited = TimeSpan.Zero;
            var maxWait = TimeSpan.FromMinutes(5);
            var delay = TimeSpan.FromSeconds(3);
            while (totalWaited < maxWait)
            {
                await Task.Delay(delay);
                totalWaited += delay;
                var statusUrl = $"https://graph.facebook.com/v21.0/{container.Id}?fields=status_code&access_token={Uri.EscapeDataString(accessToken)}";
                var statusResp = await http.GetStringAsync(statusUrl);
                var status = JsonConvert.DeserializeObject<StatusResponse>(statusResp);
                if (status?.StatusCode == "FINISHED") break;
                if (status?.StatusCode == "ERROR") throw new InvalidOperationException("IG reels processing failed");
                delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 1.5));  // exponential backoff
            }
            if (totalWaited >= maxWait) throw new InvalidOperationException("IG reels processing timeout (5 min)");
        }

        // Step 3: publish
        var publishParams = new Dictionary<string, string>
        {
            ["creation_id"] = container.Id,
            ["access_token"] = accessToken,
        };
        var pubResp = await http.PostAsync(
            $"https://graph.facebook.com/v21.0/{Uri.EscapeDataString(igUserId)}/media_publish",
            new FormUrlEncodedContent(publishParams));
        var pubBody = await pubResp.Content.ReadAsStringAsync();
        if (!pubResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"IG publish failed: {pubBody}");

        var pub = JsonConvert.DeserializeObject<MediaPublishResponse>(pubBody)
            ?? throw new InvalidOperationException("Publish response deserialization failed");
        return pub.Id ?? throw new InvalidOperationException("Publish response missing ID");
    }

    public async Task<string> PublishFacebookPostAsync(string pageId, string pageAccessToken, string message, string? link = null, string? imageUrl = null)
    {
        var http = _http.CreateClient("meta");
        var fields = new Dictionary<string, string>
        {
            ["message"] = message,
            ["access_token"] = pageAccessToken,
        };
        if (!string.IsNullOrEmpty(link)) fields["link"] = link;
        if (!string.IsNullOrEmpty(imageUrl)) fields["picture"] = imageUrl;

        // Retry policy: 3 attempts on 5xx or 429 (rate limit), with exponential backoff.
        Exception? last = null;
        for (var i = 0; i < 3; i++)
        {
            try
            {
                var resp = await http.PostAsync(
                    $"https://graph.facebook.com/v21.0/{Uri.EscapeDataString(pageId)}/feed",
                    new FormUrlEncodedContent(fields));
                var body = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                {
                    var parsed = JsonConvert.DeserializeObject<MediaPublishResponse>(body);
                    return parsed?.Id ?? throw new InvalidOperationException("FB post missing ID");
                }
                var code = (int)resp.StatusCode;
                if (code == 429 || code >= 500)
                {
                    // Honor Retry-After header if present, else exponential backoff
                    var retryAfter = resp.Headers.RetryAfter?.Delta?.TotalSeconds ?? Math.Pow(2, i + 1);
                    _log.LogWarning("FB publish got {Code}, backing off {Sec}s", code, retryAfter);
                    await Task.Delay(TimeSpan.FromSeconds(retryAfter));
                    continue;
                }
                throw new InvalidOperationException($"FB post failed: {body}");
            }
            catch (Exception ex) when (i < 2) { last = ex; await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i + 1))); }
        }
        throw last ?? new InvalidOperationException("FB publish exhausted retries");
    }

    public async Task<(string Status, DateTimeOffset? ExpiresAt)> GetTokenStatusAsync()
    {
        var tokensJson = await _vault.LoadTokensAsync("meta");
        if (string.IsNullOrEmpty(tokensJson)) return ("no_token", null);
        var set = JsonConvert.DeserializeObject<TokenSet>(tokensJson);
        if (set == null) return ("invalid", null);
        if (set.ExpiresAt < DateTimeOffset.UtcNow) return ("expired", set.ExpiresAt);
        return ("valid", set.ExpiresAt);
    }

    // ----- Internal DTOs -----
    private sealed class TokenResponse
    {
        [JsonProperty("access_token")] public string? AccessToken { get; set; }
        [JsonProperty("token_type")]    public string? TokenType { get; set; }
        [JsonProperty("expires_in")]    public long? ExpiresIn { get; set; }
        public TokenSet ToTokenSet() => new()
        {
            AccessToken = AccessToken ?? throw new InvalidOperationException("Meta response missing access_token"),
            TokenType = TokenType ?? "Bearer",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(ExpiresIn ?? 3600),
        };
    }
    private sealed class PagesListResponse
    {
        [JsonProperty("data")] public List<PageEntry>? Data { get; set; }
    }
    private sealed class PageEntry
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("access_token")] public string? AccessToken { get; set; }
        [JsonProperty("instagram_business_account")] public IgBusinessRef? InstagramBusinessAccount { get; set; }
    }
    private sealed class IgBusinessRef
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("username")] public string Username { get; set; } = "";
    }
    private sealed class MediaContainerResponse { [JsonProperty("id")] public string? Id { get; set; } }
    private sealed class MediaPublishResponse   { [JsonProperty("id")] public string? Id { get; set; } }
    private sealed class StatusResponse          { [JsonProperty("status_code")] public string? StatusCode { get; set; } }
}

public sealed class TokenSet
{
    public string AccessToken { get; set; } = "";
    public string TokenType { get; set; } = "Bearer";
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(1);
    public string? PageAccessToken { get; set; }
    public string? PageId { get; set; }
    public string? IgBusinessId { get; set; }
    public string? IgUsername { get; set; }
}

public sealed class InstagramUser
{
    public string Id { get; }
    public string Username { get; }
    public string AccessToken { get; }
    public string PageId { get; }
    public InstagramUser(string id, string username, string accessToken, string pageId)
    {
        Id = id; Username = username; AccessToken = accessToken; PageId = pageId;
    }
}

public sealed class PageInfo
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("name")] public string? Name { get; set; }
    [JsonProperty("access_token")] public string? AccessToken { get; set; }
    [JsonProperty("instagram_business_account")] public IgBusinessRef? InstagramBusinessAccount { get; set; }
    private sealed class IgBusinessRef
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("username")] public string Username { get; set; } = "";
    }
}


