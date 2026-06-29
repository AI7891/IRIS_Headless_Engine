// =============================================================================
//  The Inner Shift Lab — IRIS Broadcast Pipeline
//  File: src/Program.cs
//  Stack: ASP.NET Core 8 minimal API · Quartz scheduler · SQLite
//  Targets: Meta (IG+FB), TikTok Content Posting, YouTube Data API v3
//  IRIS: 4-pillar content engine (Identify / Reprogram / Integrate / Stabilise)
// =============================================================================

using InnerShiftLab.Auth;
using InnerShiftLab.Core;
using InnerShiftLab.Engine;
using InnerShiftLab.Monetization;
using InnerShiftLab.Providers;
using InnerShiftLab.Scheduling;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Serilog;
using System.Text.Json;
using System.Text.Json.Serialization;

// -----------------------------------------------------------------------------
// 0. Bootstrap logging FIRST so startup errors are visible
// -----------------------------------------------------------------------------
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("data/iris.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // -----------------------------------------------------------------------------
    // 1. JSON options — case-insensitive, allow comments, write indented
    // -----------------------------------------------------------------------------
    builder.Services.Configure<JsonOptions>(o =>
    {
        o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.SerializerOptions.PropertyNameCaseInsensitive = true;
        o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.SerializerOptions.WriteIndented = true;
    });

    // -----------------------------------------------------------------------------
    // 2. Bind strongly-typed config sections (fail fast if missing)
    // -----------------------------------------------------------------------------
    var irisSection = builder.Configuration.GetSection("Iris");
    builder.Services.Configure<IrisSettings>(irisSection);

    var socialsSection = builder.Configuration.GetSection("Socials");
    builder.Services.Configure<SocialsSettings>(socialsSection);

    var monetizationSection = builder.Configuration.GetSection("Monetization");
    builder.Services.Configure<MonetizationSettings>(monetizationSection);

    // Validate required config at startup — fail loud, not silent
    var irisSettings = irisSection.Get<IrisSettings>()
        ?? throw new InvalidOperationException("Missing [Iris] config section in appsettings.json");
    var socialsSettings = socialsSection.Get<SocialsSettings>()
        ?? throw new InvalidOperationException("Missing [Socials] config section in appsettings.json");
    var monetizationSettings = monetizationSection.Get<MonetizationSettings>()
        ?? throw new InvalidOperationException("Missing [Monetization] config section in appsettings.json");

    // -----------------------------------------------------------------------------
    // 3. Singletons — engine, providers, repositories, scheduler
    // -----------------------------------------------------------------------------
    builder.Services.AddSingleton(irisSettings);
    builder.Services.AddSingleton(socialsSettings);
    builder.Services.AddSingleton(monetizationSettings);

    // SQLite repo for posts, tokens, conversions, webhook events
    builder.Services.AddSingleton<IRepository>(_ =>
        new SqliteRepository(builder.Configuration.GetConnectionString("Default")
            ?? "Data Source=data/iris.db"));

    // HTTP clients (named, so we can apply per-provider policies)
    builder.Services.AddHttpClient("meta",     c => { c.Timeout = TimeSpan.FromSeconds(30); });
    builder.Services.AddHttpClient("tiktok",   c => { c.Timeout = TimeSpan.FromSeconds(60); });
    builder.Services.AddHttpClient("youtube",  c => { c.Timeout = TimeSpan.FromSeconds(60); });
    builder.Services.AddHttpClient("linktree", c => { c.Timeout = TimeSpan.FromSeconds(15); });

    // Token vault — encrypted at rest, refresh-aware
    builder.Services.AddSingleton<ITokenVault, TokenVault>();

    // Provider adapters — all four
    builder.Services.AddSingleton<IMetaProvider,     MetaProvider>();
    builder.Services.AddSingleton<ITiktokProvider,   TikTokProvider>();
    builder.Services.AddSingleton<IYoutubeProvider,  YouTubeProvider>();
    builder.Services.AddSingleton<IProviderRouter>(sp => new ProviderRouter(
        sp.GetRequiredService<IMetaProvider>(),
        sp.GetRequiredService<ITiktokProvider>(),
        sp.GetRequiredService<IYoutubeProvider>(),
        sp.GetRequiredService<ITokenVault>(),
        sp.GetRequiredService<IContentRenderer>(),
        sp.GetRequiredService<IOptions<SocialsSettings>>(),
        sp.GetRequiredService<ILogger<ProviderRouter>>()
    ));

    // IRIS engine — reads hooks.json + pillars.json, scores and selects
    builder.Services.AddSingleton<IIrisEngine, IrisEngine>();

    // Content renderer — replaces Canva (ImageSharp + QuestPDF + FFmpeg)
    builder.Services.AddSingleton<IContentRenderer, ContentRenderer>();

    // Monetization logger — links UTMs to conversions
    builder.Services.AddSingleton<IMonetizationLogger, MonetizationLogger>();

    // Webhook verifier — for Skool join events
    builder.Services.AddSingleton<IWebhookVerifier, WebhookVerifier>();

    // -----------------------------------------------------------------------------
    // 4. Quartz scheduler — heartbeat, daily post, token refresh, webhook sweep
    // -----------------------------------------------------------------------------
    builder.Services.AddQuartz(q =>
    {
        // Heartbeat: every 10 min — proves Codespaces is alive (Tasker pings /healthz)
        var heartbeat = JobKey.Create("heartbeat");
        q.AddJob<HeartbeatJob>(h => h.WithIdentity(heartbeat).StoreDurably());
        q.AddTrigger(t => t
            .ForJob(heartbeat)
            .WithIdentity("heartbeat-trigger")
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(10).RepeatForever()));

        // Daily content slot: 09:00 UTC (≈ 11:00 CET — peak European engagement window)
        var dailyPost = JobKey.Create("daily-post");
        q.AddJob<DailyPostJob>(h => h.WithIdentity(dailyPost).StoreDurably());
        q.AddTrigger(t => t
            .ForJob(dailyPost)
            .WithIdentity("daily-post-trigger")
            .WithCronSchedule("0 0 9 * * ?", b => b.InTimeZone(TimeZoneInfo.Utc)));

        // Token refresh sweep: hourly — refresh any expiring Meta/TikTok/YouTube tokens
        var tokenRefresh = JobKey.Create("token-refresh");
        q.AddJob<TokenRefreshJob>(h => h.WithIdentity(tokenRefresh).StoreDurably());
        q.AddTrigger(t => t
            .ForJob(tokenRefresh)
            .WithIdentity("token-refresh-trigger")
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(60).RepeatForever()));

        // Webhook sweep: every 5 min — reconcile missed Skool join events
        var webhookSweep = JobKey.Create("webhook-sweep");
        q.AddJob<WebhookSweepJob>(h => h.WithIdentity(webhookSweep).StoreDurably());
        q.AddTrigger(t => t
            .ForJob(webhookSweep)
            .WithIdentity("webhook-sweep-trigger")
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(5).RepeatForever()));
    });

    builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

    // -----------------------------------------------------------------------------
    // 5. Build app + middleware
    // -----------------------------------------------------------------------------
    var app = builder.Build();

    // Auto-create DB on first run
    using (var scope = app.Services.CreateScope())
    {
        var repo = scope.ServiceProvider.GetRequiredService<IRepository>();
        await repo.InitAsync();
    }

    app.UseSerilogRequestLogging();

    // -----------------------------------------------------------------------------
    // 6. HTTP surface — control plane for phone operator
    // -----------------------------------------------------------------------------

    // Healthchecks (Tasker pings these)
    app.MapGet("/healthz", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));
    app.MapGet("/readyz",  async (IRepository r) =>
    {
        var ok = await r.PingAsync();
        return ok ? Results.Ok(new { ready = true })
                  : Results.StatusCode(503);
    });

    // IRIS engine endpoints
    app.MapGet("/api/iris/hooks", (IIrisEngine e) =>
        Results.Ok(e.GetAllHooks()));

    app.MapGet("/api/iris/queue", (IIrisEngine e) =>
        Results.Ok(e.GetCurrentQueue()));

    app.MapPost("/api/iris/enqueue", (EnqueueRequest req, IIrisEngine e) =>
    {
        var slot = e.Enqueue(req.HookId, req.Pillar, req.Platforms);
        return Results.Ok(slot);
    });

    // Provider endpoints — proxy to platform APIs
    app.MapGet("/api/providers/status", async (IProviderRouter r) =>
        Results.Ok(await r.GetStatusAsync()));

    app.MapPost("/api/providers/{platform}/publish", async (
        string platform,
        PublishRequest req,
        IProviderRouter r,
        IMonetizationLogger m) =>
    {
        var post = await r.PublishAsync(platform, req);
        await m.LogPostAsync(post);
        return Results.Ok(post);
    });

    // Auth endpoints — full Meta OAuth + state mgmt
    app.MapGet("/auth/meta/login",    (IMetaProvider m) =>
        Results.Redirect(m.BuildAuthorizationUrl()));
    app.MapGet("/auth/tiktok/login",  (ITiktokProvider t) =>
        Results.Redirect(t.BuildAuthorizationUrl()));
    app.MapGet("/auth/youtube/login", (IYoutubeProvider y) =>
        Results.Redirect(y.BuildAuthorizationUrl()));
    app.MapGet("/auth/tiktok/callback", async (HttpContext ctx, ITiktokProvider t, ITokenVault v) =>
    {
        var code = ctx.Request.Query["code"].ToString();
        if (string.IsNullOrEmpty(code)) return Results.BadRequest("missing code");
        var tokens = await t.ExchangeCodeAsync(code);
        await v.SaveTokensAsync("tiktok", tokens);
        return Results.Ok(new { ok = true, expiresAt = tokens.ExpiresAt });
    });
    app.MapGet("/auth/youtube/callback", async (HttpContext ctx, IYoutubeProvider y, ITokenVault v) =>
    {
        var code = ctx.Request.Query["code"].ToString();
        if (string.IsNullOrEmpty(code)) return Results.BadRequest("missing code");
        var tokens = await y.ExchangeCodeAsync(code);
        await v.SaveTokensAsync("youtube", tokens);
        return Results.Ok(new { ok = true, expiresAt = tokens.ExpiresAt });
    });
    app.MapGet("/auth/meta/callback", async (
        HttpContext ctx, IMetaProvider m, ITokenVault v, ILogger<Program> log) =>
    {
        var (code, state) = MetaOAuthHelper.ParseCallback(ctx.Request.Query);
        // Step 1: exchange code for short-lived token
        var shortLived = await m.ExchangeCodeAsync(code);
        // Step 2: exchange short-lived for long-lived (60-day) token
        var longLived = await m.ExchangeForLongLivedAsync(shortLived.AccessToken);
        // Step 3: resolve the IG business account + page token fanout
        try
        {
            var igUser = await m.ResolveInstagramUserAsync(longLived.AccessToken);
            longLived.IgBusinessId = igUser.Id;
            longLived.IgUsername = igUser.Username;
            longLived.PageAccessToken = igUser.AccessToken;
            longLived.PageId = igUser.PageId;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "IG resolution failed during callback; user can re-run /api/providers/meta/resolve");
        }
        await v.SaveTokensAsync("meta", longLived);
        return Results.Ok(new { ok = true, expiresAt = longLived.ExpiresAt, igUsername = longLived.IgUsername });
    });
    app.MapGet("/auth/meta/webhook",   (HttpContext ctx, IWebhookVerifier w) =>
        w.VerifyMetaChallenge(ctx.Request.Query));
    app.MapPost("/auth/meta/webhook",  async (
        HttpContext ctx, IWebhookVerifier w, IRepository r) =>
    {
        var body = await MetaOAuthHelper.ReadBodyAsync(ctx);
        var ok = w.VerifyMetaSignature(ctx.Request.Headers, body);
        if (!ok) return Results.Unauthorized();
        await r.RecordWebhookAsync("meta", body);
        return Results.Ok();
    });

    // Skool webhook — receives join events, links to UTMs
    app.MapPost("/webhook/skool", async (
        HttpContext ctx, IWebhookVerifier w, IMonetizationLogger m) =>
    {
        var body = await MetaOAuthHelper.ReadBodyAsync(ctx);
        var ok = w.VerifySkoolSignature(ctx.Request.Headers, body);
        if (!ok) return Results.Unauthorized();
        await m.LogSkoolJoinAsync(body);
        return Results.Ok();
    });

    // Monetization reporting — for KPI tracking
    app.MapGet("/api/monetization/summary", async (IMonetizationLogger m) =>
        Results.Ok(await m.GetSummaryAsync()));

    app.MapGet("/api/monetization/conversions", async (IMonetizationLogger m, int? limit) =>
        Results.Ok(await m.GetConversionsAsync(limit ?? 100)));

    // Manual operator commands (for the phone-driven workflow)
    app.MapPost("/api/op/dry-run", async (
        string? hookId, IIrisEngine e, IProviderRouter r) =>
    {
        var slot = hookId != null
            ? e.Enqueue(hookId, Pillar.Integrate, new[] { "instagram", "facebook" })
            : e.GetCurrentQueue().FirstOrDefault();
        if (slot == null) return Results.NotFound();
        var preview = await r.DryRunAsync(slot);
        return Results.Ok(preview);
    });

    app.MapGet("/", () => Results.Redirect("/swagger"));
    app.UseSwagger();
    app.UseSwaggerUI();

    Log.Information("IRIS pipeline starting on {Env}", app.Environment.EnvironmentName);
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "IRIS pipeline crashed during startup");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

return 0;

// -----------------------------------------------------------------------------
//  Helper DTOs (kept here so the file is truly single-source)
// -----------------------------------------------------------------------------
public record EnqueueRequest(string HookId, string Pillar, string[] Platforms);
public record PublishRequest(string HookId, string Pillar, string Caption, string? MediaUrl);




