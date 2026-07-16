// =============================================================================
//  API key middleware — the front door.
//
//  The phone workflow forces port 5000 public (the Termux heartbeat must reach
//  /healthz from outside the Codespace), which would otherwise expose revenue
//  data, credit-burning creator endpoints, and attribution-corrupting confirms
//  to the open internet. Every request must carry X-Iris-Key; /healthz and
//  /readyz are protected too — our own heartbeat sends the header, and an
//  anonymous prober should not be able to confirm what is running here.
// =============================================================================
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace InnerShiftLab.Security;

public sealed class ApiKeyMiddleware
{
    public const string HeaderName = "X-Iris-Key";

    // Third-party callers cannot send our header. These verify callers by their own means:
    // the Skool webhook by shared-secret/HMAC, the Meta webhook by signature + verify token.
    private static readonly string[] ExemptPaths = { "/webhook/skool", "/auth/meta/webhook" };

    private readonly RequestDelegate _next;
    private readonly SecuritySettings _settings;
    private readonly ILogger<ApiKeyMiddleware> _log;

    // Throttle rejection logging to once per minute per IP so an attacker cannot
    // flood the disk through our own 14-day rolling log file.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRejectionLog = new();

    public ApiKeyMiddleware(RequestDelegate next, SecuritySettings settings, ILogger<ApiKeyMiddleware> log)
    {
        _next = next; _settings = settings; _log = log;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Local development only; startup validation guarantees a deployed Codespace
        // cannot reach this state with a missing/weak key.
        if (!_settings.RequireApiKey)
        {
            await _next(context);
            return;
        }

        // Segment match, never Contains: /api/x?next=/webhook/skool must not slip past.
        foreach (var exempt in ExemptPaths)
        {
            if (context.Request.Path.StartsWithSegments(exempt, StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }
        }

        var provided = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(provided) || !KeysMatch(provided, _settings.ApiKey))
        {
            LogRejection(context);
            // 401 with an empty body: no WWW-Authenticate, no JSON, no hint whether the
            // header was absent or merely wrong.
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Constant-time comparison. A plain == returns early on the first differing byte
    /// and leaks, over many requests, how much of a guess was right. Hashing both
    /// first gives FixedTimeEquals the equal lengths it requires without leaking the
    /// expected key's length.
    /// </summary>
    internal static bool KeysMatch(string provided, string expected)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private void LogRejection(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTimeOffset.UtcNow;
        if (_lastRejectionLog.TryGetValue(ip, out var last) && now - last < TimeSpan.FromMinutes(1))
            return;
        // Bound the dictionary so rotating source IPs can't grow it without limit.
        if (_lastRejectionLog.Count > 10_000) _lastRejectionLog.Clear();
        _lastRejectionLog[ip] = now;
        // Path and IP only — never the supplied key, not even truncated.
        _log.LogWarning("API key rejected for {Path} from {RemoteIp}", context.Request.Path, ip);
    }
}
