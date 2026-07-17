using InnerShiftLab.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InnerShiftLab.Tests;

public class ApiKeyMiddlewareTests
{
    private const string RealKey = "0123456789abcdef0123456789abcdef"; // 32 chars

    private sealed class Probe
    {
        public bool NextInvoked;
        public Task Next(HttpContext _) { NextInvoked = true; return Task.CompletedTask; }
    }

    private static (ApiKeyMiddleware Middleware, Probe Probe) Build(bool requireApiKey = true)
    {
        var probe = new Probe();
        var settings = new SecuritySettings { RequireApiKey = requireApiKey, ApiKey = RealKey };
        return (new ApiKeyMiddleware(probe.Next, settings, NullLogger<ApiKeyMiddleware>.Instance), probe);
    }

    private static DefaultHttpContext Context(string path, string? key = null, string? query = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        if (query != null) ctx.Request.QueryString = new QueryString(query);
        if (key != null) ctx.Request.Headers[ApiKeyMiddleware.HeaderName] = key;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Fact]
    public async Task CorrectKey_InvokesNext_StatusUntouched()
    {
        var (mw, probe) = Build();
        var ctx = Context("/api/outbox", RealKey);

        await mw.InvokeAsync(ctx);

        Assert.True(probe.NextInvoked);
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]                                  // header absent
    [InlineData("")]                                    // header empty
    [InlineData("wrong-key-wrong-key-wrong-key-wrong")] // header wrong
    public async Task BadKey_401EmptyBody_NextNotInvoked(string? key)
    {
        var (mw, probe) = Build();
        var ctx = Context("/api/outbox", key);

        await mw.InvokeAsync(ctx);

        Assert.False(probe.NextInvoked);
        Assert.Equal(401, ctx.Response.StatusCode);
        // Empty body: no WWW-Authenticate, no JSON, no hint what was wrong.
        Assert.Equal(0, ctx.Response.Body.Length);
        Assert.False(ctx.Response.Headers.ContainsKey("WWW-Authenticate"));
    }

    [Fact]
    public async Task RequireApiKeyFalse_PassesWithoutHeader()
    {
        var (mw, probe) = Build(requireApiKey: false);
        var ctx = Context("/api/outbox");

        await mw.InvokeAsync(ctx);

        Assert.True(probe.NextInvoked);
    }

    [Theory]
    [InlineData("/webhook/skool")]
    [InlineData("/auth/meta/webhook")]
    public async Task ExemptPaths_PassWithoutHeader(string path)
    {
        var (mw, probe) = Build();
        var ctx = Context(path);

        await mw.InvokeAsync(ctx);

        Assert.True(probe.NextInvoked);
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ApiOutbox_IsNotExempt()
    {
        var (mw, probe) = Build();
        var ctx = Context("/api/outbox");

        await mw.InvokeAsync(ctx);

        Assert.False(probe.NextInvoked);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ExemptPathInQueryString_DoesNotSlipPast()
    {
        var (mw, probe) = Build();
        var ctx = Context("/api/outbox", query: "?next=/webhook/skool");

        await mw.InvokeAsync(ctx);

        Assert.False(probe.NextInvoked);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Healthz_RequiresKey_ItIsNotExempt()
    {
        // Deliberate: the heartbeat sends the header; an anonymous prober should not
        // be able to confirm what runs here. Do NOT "helpfully" exempt this later.
        var (mw, probe) = Build();
        var ctx = Context("/healthz");

        await mw.InvokeAsync(ctx);

        Assert.False(probe.NextInvoked);
        Assert.Equal(401, ctx.Response.StatusCode);

        var (mw2, probe2) = Build();
        var ok = Context("/healthz", RealKey);
        await mw2.InvokeAsync(ok);
        Assert.True(probe2.NextInvoked);
    }

    [Fact]
    public void KeysMatch_TrueOnlyForIdenticalKeys()
    {
        Assert.True(ApiKeyMiddleware.KeysMatch(RealKey, RealKey));
        // Differs only in the last byte.
        Assert.False(ApiKeyMiddleware.KeysMatch(RealKey[..^1] + "X", RealKey));
        // A prefix of the real key must not pass (length differences don't leak through).
        Assert.False(ApiKeyMiddleware.KeysMatch(RealKey[..16], RealKey));
    }
}
