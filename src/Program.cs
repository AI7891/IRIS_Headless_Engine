// =============================================================================
//  The Inner Shift Lab — IRIS Headless Content Factory
//  File: src/Program.cs
//  Stack: ASP.NET Core 8 minimal API · Quartz scheduler · SQLite
//  IRIS: 4-pillar content engine (Identify / Reprogram / Integrate / Stabilise)
//
//  Daily cycle: curate hooks -> render one platform-formatted variant per
//  platform -> write to the SQLite outbox -> export (media + captions +
//  manifest.json) to Google Drive -> operator posts manually from the phone
//  and confirms via POST /api/outbox/{packageId}/{platform}/confirm.
//
//  The former auto-publish pipeline (Meta/TikTok/YouTube APIs + OAuth token
//  storage) is QUARANTINED behind Features:AutoPublish (default false): not
//  registered, not scheduled, endpoints gated off. It was retired because
//  unattended API posting risks platform flagging with unverified apps, and
//  storing long-lived social OAuth tokens adds GDPR/cybersecurity surface.
// =============================================================================

using InnerShiftLab.Auth;
using InnerShiftLab.ContentCreator;
using InnerShiftLab.Core;
using InnerShiftLab.Engine;
using InnerShiftLab.Monetization;
using InnerShiftLab.Outbox;
using InnerShiftLab.Providers;
using InnerShiftLab.Scheduling;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
    // The documented workflow runs from src/ with appsettings.json at the repo root, and a
    // published build copies it next to the binary. Resolve the app root up front and use it as
    // the content root so the default configuration loader reads appsettings.json with normal
    // precedence (environment variables and command line still override it).
    var appRoot = AppPaths.ResolveRoot(Directory.GetCurrentDirectory());
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = appRoot,
    });
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
        // Serialize enums as their names so responses (e.g. pillar) match the string values
        // the API accepts on input, rather than emitting raw integers.
        o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

    // Swagger/OpenAPI — required by app.UseSwagger()/UseSwaggerUI() below and the "/" landing page.
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // -----------------------------------------------------------------------------
    // 2. Bind strongly-typed config sections (fail fast if missing)
    // -----------------------------------------------------------------------------
    var irisSection = builder.Configuration.GetSection("Iris");
    builder.Services.Configure<IrisSettings>(irisSection);

    var socialsSection = builder.Configuration.GetSection("Socials");
    builder.Services.Configure<SocialsSettings>(socialsSection);

    var monetizationSection = builder.Configuration.GetSection("Monetization");
    builder.Services.Configure<MonetizationSettings>(monetizationSection);

    var contentCreatorSection = builder.Configuration.GetSection("ContentCreator");
    builder.Services.Configure<ContentCreatorSettings>(contentCreatorSection);

    var outboxSection = builder.Configuration.GetSection("Outbox");
    builder.Services.Configure<OutboxSettings>(outboxSection);

    // Master switch for the quarantined auto-publish pipeline. Default false: the
    // provider adapters are not registered, their jobs not scheduled, and their
    // endpoints not mapped. Flip to true only if the platform apps get verified.
    var autoPublish = builder.Configuration.GetValue<bool>("Features:AutoPublish");

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

    // SQLite repo for posts, tokens, conversions, webhook events.
    // Resolve a relative "Data Source" against the app root so the db lands in the same
    // data/ dir as the token vault and heartbeat, independent of the launch directory.
    var connString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=data/iris.db";
    connString = AppPaths.ResolveConnectionString(connString, appRoot);
    builder.Services.AddSingleton<IRepository>(_ => new SqliteRepository(connString));

    // HTTP clients (named, so we can apply per-provider policies)
    builder.Services.AddHttpClient("linktree", c => { c.Timeout = TimeSpan.FromSeconds(15); });
    builder.Services.AddHttpClient("pexels",     c => { c.Timeout = TimeSpan.FromSeconds(60); });
    builder.Services.AddHttpClient("elevenlabs", c => { c.Timeout = TimeSpan.FromSeconds(120); });
    // Google Drive uploads carry rendered video — allow a generous timeout.
    builder.Services.AddHttpClient("gdrive", c => { c.Timeout = TimeSpan.FromMinutes(5); });

    if (autoPublish)
    {
        // QUARANTINED auto-publish pipeline: platform HTTP clients, the OAuth
        // token vault, and the provider adapters. None of this exists in the
        // container while Features:AutoPublish is false.
        builder.Services.AddHttpClient("meta",     c => { c.Timeout = TimeSpan.FromSeconds(30); });
        builder.Services.AddHttpClient("tiktok",   c => { c.Timeout = TimeSpan.FromSeconds(60); });
        builder.Services.AddHttpClient("youtube",  c => { c.Timeout = TimeSpan.FromSeconds(60); });

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
    }

    // IRIS engine — reads hooks.json + pillars.json, scores and selects
    builder.Services.AddSingleton<IIrisEngine, IrisEngine>();

    // Content renderer — replaces Canva (ImageSharp + QuestPDF + FFmpeg)
    builder.Services.AddSingleton<IContentRenderer, ContentRenderer>();

    // Monetization logger — links UTMs to conversions
    builder.Services.AddSingleton<IMonetizationLogger, MonetizationLogger>();

    // Content creator pipeline — AI script (Claude) -> image carousel (Pexels) ->
    // voiceover (ElevenLabs) -> composed final output. The publish leg (router)
    // only exists when the quarantined auto-publish pipeline is enabled.
    var contentCreatorSettings = contentCreatorSection.Get<ContentCreatorSettings>() ?? new ContentCreatorSettings();
    builder.Services.AddSingleton(contentCreatorSettings);
    builder.Services.AddSingleton<IScriptGenerator, AnthropicScriptGenerator>();
    builder.Services.AddSingleton<IImageFetcher, PexelsImageFetcher>();
    builder.Services.AddSingleton<IVoiceSynthesizer, ElevenLabsVoiceSynthesizer>();
    builder.Services.AddSingleton<IContentComposer, FfmpegContentComposer>();
    builder.Services.AddSingleton<IContentCreationPipeline>(sp => new ContentCreationPipeline(
        sp.GetRequiredService<IScriptGenerator>(),
        sp.GetRequiredService<IImageFetcher>(),
        sp.GetRequiredService<IVoiceSynthesizer>(),
        sp.GetRequiredService<IContentComposer>(),
        autoPublish ? sp.GetRequiredService<IProviderRouter>() : null,
        sp.GetRequiredService<IMonetizationLogger>(),
        sp.GetRequiredService<ILogger<ContentCreationPipeline>>()));

    // Outbox — the human-in-the-loop replacement for auto-publishing.
    var outboxSettings = outboxSection.Get<OutboxSettings>() ?? new OutboxSettings();
    builder.Services.AddSingleton(outboxSettings);
    builder.Services.AddSingleton<IOutboxPackageBuilder>(sp => new OutboxPackageBuilder(
        sp.GetRequiredService<IContentRenderer>(),
        sp.GetRequiredService<IRepository>(),
        outboxSettings,
        AppPaths.OutputDir(appRoot),
        sp.GetRequiredService<ILogger<OutboxPackageBuilder>>()));
    builder.Services.AddSingleton<IPackageExporter>(sp => outboxSettings.GoogleDrive.Enabled
        ? new GoogleDrivePackageExporter(
            sp.GetRequiredService<IHttpClientFactory>(),
            outboxSettings,
            sp.GetRequiredService<ILogger<GoogleDrivePackageExporter>>())
        : new LocalPackageExporter(sp.GetRequiredService<ILogger<LocalPackageExporter>>()));
    builder.Services.AddSingleton<IOutboxService, OutboxService>();

    // Webhook verifier — for Skool join events
    builder.Services.AddSingleton<IWebhookVerifier, WebhookVerifier>();

    // -----------------------------------------------------------------------------
    // 4. Quartz scheduler — heartbeat, daily outbox, webhook sweep
    //    (+ quarantined auto-publish jobs when Features:AutoPublish is true)
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

        // Daily outbox build: 09:00 UTC (≈ 11:00 CET) — curate, render per-platform,
        // export to Google Drive; the operator posts manually during the day.
        var dailyOutbox = JobKey.Create("daily-outbox");
        q.AddJob<DailyOutboxJob>(h => h.WithIdentity(dailyOutbox).StoreDurably());
        q.AddTrigger(t => t
            .ForJob(dailyOutbox)
            .WithIdentity("daily-outbox-trigger")
            .WithCronSchedule("0 0 9 * * ?", b => b.InTimeZone(TimeZoneInfo.Utc)));

        // Webhook sweep: every 5 min — reconcile missed Skool join events
        var webhookSweep = JobKey.Create("webhook-sweep");
        q.AddJob<WebhookSweepJob>(h => h.WithIdentity(webhookSweep).StoreDurably());
        q.AddTrigger(t => t
            .ForJob(webhookSweep)
            .WithIdentity("webhook-sweep-trigger")
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(5).RepeatForever()));

        if (autoPublish)
        {
            // QUARANTINED: automated publishing + OAuth token refresh. Never
            // scheduled unless the auto-publish pipeline is explicitly re-enabled.
            var dailyPost = JobKey.Create("daily-post");
            q.AddJob<DailyPostJob>(h => h.WithIdentity(dailyPost).StoreDurably());
            q.AddTrigger(t => t
                .ForJob(dailyPost)
                .WithIdentity("daily-post-trigger")
                .WithCronSchedule("0 0 9 * * ?", b => b.InTimeZone(TimeZoneInfo.Utc)));

            var tokenRefresh = JobKey.Create("token-refresh");
            q.AddJob<TokenRefreshJob>(h => h.WithIdentity(tokenRefresh).StoreDurably());
            q.AddTrigger(t => t
                .ForJob(tokenRefresh)
                .WithIdentity("token-refresh-trigger")
                .WithSimpleSchedule(s => s.WithIntervalInMinutes(60).RepeatForever()));
        }
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
        if (!Enum.TryParse<Pillar>(req.Pillar, ignoreCase: true, out var pillar))
            return Results.BadRequest($"Unknown pillar '{req.Pillar}'. Expected one of: {string.Join(", ", Enum.GetNames<Pillar>())}");
        var slot = e.Enqueue(req.HookId, pillar, req.Platforms);
        return Results.Ok(slot);
    });

    // Outbox — the operator's daily loop: list, inspect, build on demand, confirm posted.
    app.MapGet("/api/outbox", async (IRepository r, string? status, int? limit) =>
    {
        OutboxStatus? filter = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<OutboxStatus>(status, ignoreCase: true, out var parsed))
                return Results.BadRequest($"Unknown status '{status}'. Expected one of: {string.Join(", ", Enum.GetNames<OutboxStatus>())}");
            filter = parsed;
        }
        return Results.Ok(await r.GetOutboxItemsAsync(filter, limit ?? 100));
    });

    app.MapGet("/api/outbox/{packageId}", async (string packageId, IRepository r) =>
    {
        var items = await r.GetOutboxPackageAsync(packageId);
        return items.Count == 0 ? Results.NotFound() : Results.Ok(items);
    });

    app.MapPost("/api/outbox/build", async (IOutboxService o, CancellationToken ct) =>
    {
        var package = await o.BuildDailyPackageAsync(ct);
        return package == null
            ? Results.NotFound(new { message = "No hooks available to package" })
            : Results.Ok(package);
    });

    app.MapPost("/api/outbox/{packageId}/{platform}/confirm", async (
        string packageId, string platform, ConfirmPostRequest? req, IOutboxService o) =>
    {
        var ok = await o.ConfirmPostedAsync(packageId, platform, req?.PostUrl);
        return ok ? Results.Ok(new { confirmed = true, packageId, platform })
                  : Results.NotFound(new { message = $"No outbox item for package '{packageId}' on '{platform}'" });
    });

    if (autoPublish)
    {
        // =========================================================================
        // QUARANTINED HTTP surface — the retired auto-publish pipeline. These
        // endpoints only exist when Features:AutoPublish is true.
        // =========================================================================

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
        {
            // Meta subscription verification: echo hub.challenge as plain text on success.
            var challenge = w.VerifyMetaChallenge(ctx.Request.Query);
            return challenge != null ? Results.Text(challenge) : Results.StatusCode(403);
        });
        app.MapPost("/auth/meta/webhook",  async (
            HttpContext ctx, IWebhookVerifier w, IRepository r) =>
        {
            var body = await MetaOAuthHelper.ReadBodyAsync(ctx);
            var ok = w.VerifyMetaSignature(ctx.Request.Headers, body);
            if (!ok) return Results.Unauthorized();
            await r.RecordWebhookAsync("meta", body);
            return Results.Ok();
        });
    }

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

    // Content creator endpoints — AI script -> carousel -> voiceover -> composed output
    app.MapPost("/api/creator/script", async (CreatorRequest? req, IScriptGenerator g, CancellationToken ct) =>
        Results.Ok(await g.GenerateAsync(req?.Keywords, req?.SlideCount, ct)));

    app.MapPost("/api/creator/run", async (CreatorRequest? req, IContentCreationPipeline p, CancellationToken ct) =>
        Results.Ok(await p.CreateAsync(req?.Keywords, req?.SlideCount, ct)));

    if (autoPublish)
    {
        // QUARANTINED: direct-publish leg of the creator pipeline.
        app.MapPost("/api/creator/publish", async (CreatorPublishRequest req, IContentCreationPipeline p, CancellationToken ct) =>
        {
            if (req.Platforms is null || req.Platforms.Length == 0)
                return Results.BadRequest("Provide at least one platform (instagram, facebook, tiktok, youtube).");
            return Results.Ok(await p.CreateAndPublishAsync(req.Keywords, req.Platforms, ct));
        });
    }

    // Monetization reporting — for KPI tracking
    app.MapGet("/api/monetization/summary", async (IMonetizationLogger m) =>
        Results.Ok(await m.GetSummaryAsync()));

    app.MapGet("/api/monetization/conversions", async (IMonetizationLogger m, int? limit) =>
        Results.Ok(await m.GetConversionsAsync(limit ?? 100)));

    if (autoPublish)
    {
        // QUARANTINED: dry-run preview against the provider router.
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
    }

    app.MapGet("/", () => Results.Redirect("/swagger"));
    app.UseSwagger();
    app.UseSwaggerUI();

    Log.Information("IRIS headless content factory starting on {Env} (AutoPublish={AutoPublish})",
        app.Environment.EnvironmentName, autoPublish);
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
public record CreatorRequest(string? Keywords, int? SlideCount);
public record CreatorPublishRequest(string? Keywords, string[] Platforms);
public record ConfirmPostRequest(string? PostUrl);




