// =============================================================================
//  Auth — token vault (encrypted at rest) + Meta OAuth helpers + webhook verifier
// =============================================================================
using InnerShiftLab.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;

namespace InnerShiftLab.Auth;

public interface ITokenVault
{
    Task SaveTokensAsync(string provider, TokenSet tokens);
    Task<TokenSet?> LoadTokensAsync(string provider);
    Task<string?> LoadRawAsync(string provider);
}

/// <summary>
/// The OAuth scopes each provider's official API requires for publishing. Used to
/// validate what the vault stores and to tell the operator exactly what to grant
/// during the OAuth consent screen. Publishing never uses anything beyond these.
/// </summary>
public static class OAuthScopes
{
    public static readonly IReadOnlyDictionary<string, string[]> RequiredForPublish =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Meta Graph API — page + IG content publishing
            ["meta"] = new[]
            {
                "pages_show_list", "pages_manage_posts",
                "instagram_basic", "instagram_content_publish",
            },
            // TikTok Content Posting API
            ["tiktok"] = new[] { "user.info.basic", "video.publish" },
            // YouTube Data API v3 upload
            ["youtube"] = new[] { "https://www.googleapis.com/auth/youtube.upload" },
        };

    public static string[] For(string provider) =>
        RequiredForPublish.TryGetValue(provider, out var scopes) ? scopes : Array.Empty<string>();
}

/// <summary>
/// Encrypted-at-rest store for official OAuth tokens only. Tokens must be Bearer
/// tokens obtained via the platform's own OAuth flow; anything else (session
/// cookies, scraped credentials) is rejected. Refresh handling: refresh tokens
/// are stored alongside access tokens, expiry is tracked via ExpiresAt/IssuedAt,
/// and the providers/TokenRefreshJob use them to renew access through the
/// official token endpoints.
/// </summary>
public sealed class TokenVault : ITokenVault
{
    private readonly string _keyDir;
    private readonly byte[] _encryptionKey;
    private readonly ILogger<TokenVault>? _log;

    public TokenVault(IWebHostEnvironment env, ILogger<TokenVault>? log = null)
    {
        _keyDir = AppPaths.DataDir(AppPaths.ResolveRoot(env.ContentRootPath));
        _encryptionKey = LoadOrCreateKey(Path.Combine(_keyDir, "vault.key"));
        _log = log;
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
            return Convert.FromBase64String(File.ReadAllText(path));
        var key = RandomNumberGenerator.GetBytes(32); // AES-256
        File.WriteAllText(path, Convert.ToBase64String(key));
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
        }
        return key;
    }

    public async Task SaveTokensAsync(string provider, TokenSet tokens)
    {
        // Only official OAuth bearer tokens are storable. This is the hard line that
        // keeps the tool inside platform terms: no session-token replay, no scraped
        // cookies, no impersonation credentials.
        if (string.IsNullOrWhiteSpace(tokens.AccessToken))
            throw new InvalidOperationException("Refusing to store an empty access token.");
        if (!string.Equals(tokens.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to store a '{tokens.TokenType}' credential for {provider}. " +
                "The vault only holds Bearer tokens issued by the platform's official OAuth flow.");

        var required = OAuthScopes.For(provider);
        if (required.Length > 0)
        {
            var missing = required.Where(s => !tokens.Scopes.Contains(s, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (tokens.Scopes.Length == 0)
                _log?.LogWarning("{Provider} token saved without scope metadata. Publishing requires: {Scopes}",
                    provider, string.Join(", ", required));
            else if (missing.Length > 0)
                _log?.LogWarning("{Provider} token is missing publish scopes: {Missing}. Re-run the OAuth flow granting them.",
                    provider, string.Join(", ", missing));
        }

        if (string.IsNullOrEmpty(tokens.RefreshToken))
            _log?.LogWarning("{Provider} token has no refresh token; access ends at {Expiry} until re-auth.",
                provider, tokens.ExpiresAt);

        var json = JsonConvert.SerializeObject(tokens);
        var encrypted = Encrypt(json);
        var path = Path.Combine(_keyDir, $"{provider}.tok");
        await File.WriteAllTextAsync(path, encrypted);
    }

    public async Task<TokenSet?> LoadTokensAsync(string provider)
    {
        var raw = await LoadRawAsync(provider);
        if (raw == null) return null;
        return JsonConvert.DeserializeObject<TokenSet>(raw);
    }

    public async Task<string?> LoadRawAsync(string provider)
    {
        var path = Path.Combine(_keyDir, $"{provider}.tok");
        if (!File.Exists(path)) return null;
        var encrypted = await File.ReadAllTextAsync(path);
        return Decrypt(encrypted);
    }

    private string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _encryptionKey;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        var combined = new byte[aes.IV.Length + encrypted.Length];
        Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
        Buffer.BlockCopy(encrypted, 0, combined, aes.IV.Length, encrypted.Length);
        return Convert.ToBase64String(combined);
    }

    private string Decrypt(string ciphertext)
    {
        var combined = Convert.FromBase64String(ciphertext);
        using var aes = Aes.Create();
        aes.Key = _encryptionKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        var iv = new byte[16];
        Buffer.BlockCopy(combined, 0, iv, 0, 16);
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(combined, 16, combined.Length - 16);
        return Encoding.UTF8.GetString(decrypted);
    }
}

public static class MetaOAuthHelper
{
    public static (string Code, string State) ParseCallback(IQueryCollection query)
    {
        var code = query["code"].ToString();
        var state = query["state"].ToString();
        if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("OAuth callback missing 'code' parameter");
        return (code, state);
    }

    public static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}

public interface IWebhookVerifier
{
    /// <summary>Returns the hub.challenge value to echo back when the subscription check passes, else null.</summary>
    string? VerifyMetaChallenge(IQueryCollection query);
    bool VerifyMetaSignature(IHeaderDictionary headers, string body);
    bool VerifySkoolSignature(IHeaderDictionary headers, string body);
}

public sealed class WebhookVerifier : IWebhookVerifier
{
    private readonly MonetizationSettings _mon;
    private readonly ILogger<WebhookVerifier> _log;

    public WebhookVerifier(IOptions<MonetizationSettings> mon, ILogger<WebhookVerifier> log)
    {
        _mon = mon.Value; _log = log;
    }

    public string? VerifyMetaChallenge(IQueryCollection query)
    {
        var mode = query["hub.mode"].ToString();
        var token = query["hub.verify_token"].ToString();
        var challenge = query["hub.challenge"].ToString();
        if (mode == "subscribe"
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_mon.MetaAppSecret))
            && !string.IsNullOrEmpty(challenge))
        {
            _log.LogInformation("Meta webhook challenge verified");
            return challenge;
        }
        // Don't log the presented token — it's a secret.
        _log.LogWarning("Meta webhook challenge failed: mode={Mode}", mode);
        return null;
    }

    public bool VerifyMetaSignature(IHeaderDictionary headers, string body)
    {
        if (!headers.TryGetValue("X-Hub-Signature-256", out var sig)) return false;
        var sigValue = sig.ToString();
        if (!sigValue.StartsWith("sha256=")) return false;
        var provided = sigValue.Substring(7);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_mon.MetaAppSecret));
        var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        var computedHex = Convert.ToHexString(computed).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(computedHex));
    }

    public bool VerifySkoolSignature(IHeaderDictionary headers, string body)
    {
        if (!headers.TryGetValue("x-skool-signature", out var sig) &&
            !headers.TryGetValue("X-Skool-Signature", out sig)) return false;
        var provided = sig.ToString();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_mon.SkoolWebhookSecret));
        var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        var computedHex = Convert.ToHexString(computed).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(computedHex));
    }
}

