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

public sealed class TokenVault : ITokenVault
{
    private readonly string _keyDir;
    private readonly byte[] _encryptionKey;

    public TokenVault(IWebHostEnvironment env)
    {
        _keyDir = Path.Combine(env.ContentRootPath, "..", "data");
        if (!Directory.Exists(_keyDir)) Directory.CreateDirectory(_keyDir);
        _encryptionKey = LoadOrCreateKey(Path.Combine(_keyDir, "vault.key"));
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
            return Convert.FromBase64String(File.ReadAllText(path));
        var key = RandomNumberGenerator.GetBytes(32); // AES-256
        File.WriteAllText(path, Convert.ToBase64String(key));
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* Windows */ }
        return key;
    }

    public async Task SaveTokensAsync(string provider, TokenSet tokens)
    {
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
    bool VerifyMetaChallenge(IQueryCollection query);
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

    public bool VerifyMetaChallenge(IQueryCollection query)
    {
        var mode = query["hub.mode"].ToString();
        var token = query["hub.verify_token"].ToString();
        var challenge = query["hub.challenge"].ToString();
        if (mode == "subscribe" && token == _mon.MetaAppSecret && !string.IsNullOrEmpty(challenge))
        {
            _log.LogInformation("Meta webhook challenge verified");
            return true;
        }
        _log.LogWarning("Meta webhook challenge failed: mode={Mode} token={Token}", mode, token);
        return false;
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

