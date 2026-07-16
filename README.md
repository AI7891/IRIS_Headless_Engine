# The Inner Shift Lab — IRIS Headless Content Factory

Headless C# content factory for **Instagram, Facebook, TikTok, YouTube**, scored and curated by the **IRIS Method engine** (Identify / Reprogram / Integrate / Stabilise), driving traffic to your Linktree → Skool funnel — with a **human-in-the-loop outbox** instead of automated API posting.

**Stack**: ASP.NET Core 8 · Quartz scheduler · SQLite (zero-budget) · ImageSharp + QuestPDF + FFmpeg (Canva-replacement) · git-branch phone pickup + Termux notifier · GitHub Codespaces for compute.

> **⚠️ Strategic pivot (2026-07): automated cross-posting is retired.** Unattended
> API posting through unverified apps risks platform flagging/account bans, and
> storing long-lived social OAuth tokens adds EU GDPR/cybersecurity surface. IRIS
> now renders one platform-formatted package per day into an **outbox** (SQLite +
> a git pickup branch) and the operator posts manually from the phone. The provider
> code still exists, quarantined behind `Features:AutoPublish` (default `false`).
> **Read [docs/headless-outbox.md](docs/headless-outbox.md) for the full workflow.**

---

## 0. The mission

> **Reach monetization within 1 week — one curated, correctly-formatted package per day, posted by hand in minutes.**

The factory is purpose-built for this:
- 30 high-converting IRIS hooks pre-loaded (sourced from your project bible)
- Auto-curation: top hooks scored by pillar fit + platform fit + time fit + conversion potential
- Daily outbox package: one variant per platform — correct dimensions, caption limits, hashtag counts — media + caption text + `manifest.json`, pushed to a git pickup branch (opens in the GitHub mobile app)
- Auto-media: `ContentRenderer` generates the PNG per platform ($0); TikTok/YouTube get ImageSharp still → ffmpeg → mp4
- Auto-UTM: every caption embeds `?utm_campaign={hook_id}&utm_content={pillar}` so revenue per hook is tracked even though posting is manual
- Operator confirms each post via one endpoint; monetization tracking is unchanged

---

## 1. Deploy on Codespaces (phone-driven)

### 1.1 Create the Codespace
1. Open `github.com/codespaces` in your Android browser
2. **New codespace** → select `main` branch → **2-core, 4GB RAM** is enough
3. Wait for VS Code web to load

### 1.2 Install .NET 8 runtime
In the Codespaces terminal:
```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
./tmp/dotnet-install.sh --channel 8.0 --install-dir ~/.dotnet
echo 'export PATH=$HOME/.dotnet:$PATH' >> ~/.bashrc
source ~/.bashrc
dotnet --version   # should print 8.x
```

### 1.3 Clone this repo and restore
```bash
git clone <your-repo-url> inner-shift-lab
cd inner-shift-lab/src
dotnet restore
```

### 1.4 Fill in `appsettings.json`
Edit `appsettings.json` with the real `RedirectUri` after Codespaces gives you a domain. See section 2 (Meta OAuth).

### 1.5 Run
```bash
dotnet run
```
Codespaces auto-forwards port 5000. Open the **Ports** tab → port 5000 → "Open in browser".

### 1.6 Health check
- `GET /healthz` → `{"status":"ok",...}`
- `GET /readyz` → 200 if DB is reachable
- `GET /api/outbox` → today's package status (Pending/Exported/Posted)

### 1.7 Phone pickup (default: git branch)
Out of the box, each package is pushed to the `outbox` branch of this repo (ambient
Codespaces credentials, zero cost) and you open it in the GitHub mobile app. Run
`scripts/termux-outbox-notify.sh` so your phone pings when a package is ready.
**Recommended:** set `Outbox:Git:Repository` to a dedicated private repo (e.g.
`you/IRIS_Outbox`) so the media never bloats clones of the code repo. Media is
FIFO-pruned automatically (rows kept). See
[docs/headless-outbox.md](docs/headless-outbox.md) → *Delivery* and *Retention*.
(Google Drive is available too, but only with a paid Workspace **Shared Drive** — a
personal Google account fails with `403 storageQuotaExceeded`.)

---

## 2–4. Platform OAuth — QUARANTINED (kept for reference)

> **The three sections below describe the retired auto-publish pipeline.** None of
> these endpoints exist unless `Features:AutoPublish` is `true` in
> `appsettings.json`. You do NOT need any platform app, OAuth flow, or token to run
> the outbox workflow — that's the point. Kept because the code is kept, and in
> case the apps ever get verified.

## 2. Meta OAuth (quarantined)

### 2.1 Create a Meta App
1. Go to `developers.facebook.com` (use phone browser)
2. **My Apps** → **Create App** → **Other** → **Business**
3. Add products: **Facebook Login**, **Instagram Graph API**, **Webhooks**
4. Settings → **Basic** → copy `App ID` and `App Secret`
5. **Facebook Login** → **Settings** → **Valid OAuth Redirect URIs**:
   ```
   https://YOUR-CODESPACE-NAME-5000.app.github.dev/auth/meta/callback
   ```
6. **App Review** → request `pages_show_list`, `pages_manage_posts`, `business_management`, `instagram_basic`, `instagram_content_publish`
7. **Roles** → connect the **@the_inner_shift_lab** Instagram Business account
8. Paste `App ID` and `App Secret` into `appsettings.json` → `Socials.Instagram`

### 2.2 Trigger the OAuth flow
1. Visit `https://YOUR-CODESPACE.app.github.dev/auth/meta/login`
2. Log in, approve
3. Redirected to `/auth/meta/callback` — pipeline exchanges code → short-lived → long-lived token → resolves IG business ID → page-token fanout → saves encrypted vault
4. Visit `GET /api/providers/status` — meta should show `valid`

### 2.3 Webhook verification
In Meta App Dashboard → **Webhooks** → subscribe to:
- `feed` (FB page posts)
- `comments`
- `mentions`
- `story_insights` (optional)

Callback URL: `https://YOUR-CODESPACE.app.github.dev/auth/meta/webhook`
Verify token: any string (use the `MetaAppSecret` value from `appsettings.json`)

---

## 3. TikTok OAuth (quarantined)

### 3.1 Create a TikTok dev app
1. `developers.tiktok.com` → **Manage apps** → **Create app**
2. Platform: **Web**
3. Add scopes: `user.info.basic`, `video.publish`, `video.upload`
4. **Redirect URI**: `https://YOUR-CODESPACE.app.github.dev/auth/tiktok/callback`
5. Copy `Client Key` + `Client Secret` → `appsettings.json` → `Socials.Tiktok`

### 3.2 Trigger OAuth
Visit `https://YOUR-CODESPACE.app.github.dev/auth/tiktok/login`.

### 3.3 Publish privacy
Default is `SELF_ONLY` (safe testing). Change to `PUBLIC` in `TikTokProvider.PublishVideoAsync` once you've confirmed it works.

---

## 4. YouTube OAuth (quarantined)

### 4.1 Create a Google Cloud project
1. `console.cloud.google.com` → **New project** → name it `inner-shift-lab`
2. **APIs & Services** → **Enable API** → search `YouTube Data API v3` → Enable
3. **OAuth consent screen** → External → fill in app name, support email, scopes:
   - `https://www.googleapis.com/auth/youtube.upload`
   - `https://www.googleapis.com/auth/youtube.force-ssl`
4. **Credentials** → **Create credentials** → **OAuth client ID** → **Web application**
5. **Authorized redirect URIs**: `https://YOUR-CODESPACE.app.github.dev/auth/youtube/callback`
6. Copy `Client ID` + `Client Secret` → `appsettings.json` → `Socials.Youtube`
7. Channel ID is already pre-filled: `UCHfWxzYcqzApXyfnm2A0JzQ`

### 4.2 Trigger OAuth
Visit `https://YOUR-CODESPACE.app.github.dev/auth/youtube/login`.

### 4.3 Upload privacy
Default is `unlisted`. Change to `public` in `YouTubeProvider.UploadVideoAsync` after first successful test.

---

## 5. Skool webhook (for monetization tracking)

### 5.1 Configure Skool
Skool doesn't have a built-in webhook UI, but you can use **Zapier** or **n8n** to forward Skool events to your endpoint. Configure:
- **URL**: `https://YOUR-CODESPACE.app.github.dev/webhook/skool`
- **Secret**: any string, paste into `appsettings.json` → `Monetization.SkoolWebhookSecret`

### 5.2 Payload format Skool/Zapier should send
```json
{
  "plan": "inner_circle",   // or "root_work_lab", "starter_kit"
  "ref": "utm_campaign=hook-id&utm_content=Identify",  // copied from UTMs
  "post_id": "instagram-post-id-123",
  "platform": "instagram",
  "metadata": "full url if needed"
}
```

### 5.3 View monetization data
- `GET /api/monetization/summary` — total joins, revenue, top campaigns
- `GET /api/monetization/conversions?limit=50` — recent conversion events

---

## 6. Operator commands (phone-driven)

All available as HTTP endpoints. Add them as Android home screen shortcuts for fast tapping:

| Endpoint | Purpose |
|---|---|
| `GET /healthz` | Liveness |
| `GET /readyz` | Readiness (DB ok) |
| `GET /api/iris/hooks` | List all 30 IRIS hooks |
| `GET /api/iris/queue` | Show queued slots |
| `POST /api/iris/enqueue` body: `{"hookId":"...","pillar":"...","platforms":[...]}` | Add to queue |
| `GET /api/outbox` | Outbox items (`?status=exported` to see what's waiting) |
| `GET /api/outbox/{packageId}` | One package's platform variants |
| `POST /api/outbox/build` | Build + export the next `Outbox:PackagesPerRun` packages now |
| `POST /api/outbox/{packageId}/export` | Retry a failed Drive export (also swept every 15 min) |
| `POST /api/outbox/{packageId}/{platform}/confirm` | Confirm a manual post (body optional: `{"postUrl":"..."}`) |
| `POST /api/outbox/{packageId}/{platform}/skip` | Mark a variant as deliberately not posted |
| `GET /api/monetization/summary` | Revenue + join stats |
| `GET /api/monetization/conversions` | Recent conversion events |

Quarantined (only with `Features:AutoPublish=true`): `POST /api/op/dry-run`,
`POST /api/providers/{platform}/publish`, `GET /api/providers/status`, `/auth/*`,
`POST /api/creator/publish`.

---

## 7. The IRIS engine — how content is picked

Every hour the engine scores each hook against:
- **Pillar fit** (Identify/Reprogram/Integrate/Stabilise weights)
- **Platform fit** (hook's `bestFor` vs target platform)
- **Time fit** (proximity to scheduled posting slot)
- **Conversion potential** (the hook's own `score`, 0-100)

When the queue is empty, the top-scored hooks are auto-curated at 09:00 UTC daily and the best one becomes the day's outbox package (the rest stay queued for following days). Customize `pillars.json` to reweight.

---

## 8. The 14-day launch (pre-loaded in the scheduler)

| Day | Content |
|---|---|
| 1 | Welcome/pinned post on Skool · origin story Reel on all platforms |
| 2 | Science hook: "Cortisol destroys melanocytes" |
| 3 | Carousel: "The 4 Pillars of the IRIS Method" |
| 4 | Parts work hook + 5 most engaged followers DMed |
| 5 | Announce 7-Day IRIS Reset Challenge €37 |
| 6 | Repigmentation update (highest-converting content type) |
| 7 | Last call for challenge + 15-min IG Live |
| 8–14 | Challenge runs inside Skool · daily post · document breakthroughs |

The DailyOutboxJob at 09:00 UTC packages the day's top hook(s) for all platforms (`Outbox:PackagesPerRun`, default 1); you pick them up from the `outbox` git branch (or Drive). For day-specific content, edit the hook `score` field in `hooks.json` (higher = picked first).

---

## 9. Canva replacement — what's in the box

| Need | Used |
|---|---|
| Square post (1080×1080) | `ImageSharp.RenderImageAsync` — `palette: "iris-default"` (amber/black) |
| Portrait post (1080×1350) | Add to `RenderImageAsync` (one-line change) |
| Carousel | Loop over `RenderImageAsync` with multiple hook texts |
| PDF lead magnet | `QuestPDF.RenderPdfAsync` |
| Reels / TikTok / YouTube | `ffmpeg` overlay on a generated background |
| Stock photos | Free tier: Picsum (`picsum.photos`) — drop URL into `req.MediaUrl` |

All free, no subscription.

---

## 10. Termux + Tasker heartbeat (the Codespaces idle-timeout fix)

GitHub Codespaces free tier **kills inactive sessions after 30 min**. This is the #1 reason pipelines "stop working after hour 3". To bypass it:

### 10.1 Termux on Android
Install Termux from **F-Droid** (not Play Store). Then:
```bash
pkg update && pkg install openssh termux-api
```

### 10.2 Tasker profile (heartbeat every 10 min)
1. Install **Tasker** + **Termux:API**
2. **Profile** → **Time** → repeat every 10 min
3. **Task** → **Net** → **HTTP Request**:
   - Method: GET
   - URL: `https://YOUR-CODESPACE.app.github.dev/healthz`
   - Timeout: 10

Codespaces sees the request, considers the session active, and never idles out. Your scheduler runs 24/7.

### 10.3 Optional: Tasker → Termux SSH (full control from phone)
```bash
ssh -o ServerAliveInterval=60 codespace@YOUR-CODESPACE.ssh.github.com
```

---

## 11. Phone shortcuts

Create Android home screen shortcuts for these endpoints (use **HTTP Shortcuts** app or Chrome "Add to Home Screen"):

1. **Healthcheck** → `GET /healthz`
2. **Queue status** → `GET /api/iris/queue`
3. **Outbox status** → `GET /api/outbox?status=exported`
4. **Build today's package** → `POST /api/outbox/build`
5. **Today's conversions** → `GET /api/monetization/conversions?limit=10`
6. **Pickup** → tap the Termux "package ready" notification → GitHub app opens the `outbox` branch folder

---

## 12. Project structure
```
inner-shift-lab/
├── README.md
├── appsettings.json          # credentials template
├── hooks.json                # 30 IRIS hooks
├── pillars.json              # pillar weights
├── docs/
│   ├── headless-outbox.md    # the outbox workflow (start here)
│   ├── deploy-phone.md
│   ├── meta-oauth-flow.md    # quarantined pipeline reference
│   ├── api-replacement-canva.md
│   └── runbook.md
└── src/
    ├── InnerShiftLab.csproj
    ├── Program.cs
    ├── InnerShiftLab.Core/
    │   ├── CoreModels.cs
    │   ├── OutboxModels.cs
    │   └── SqliteRepository.cs
    ├── InnerShiftLab.Engine/
    │   ├── IrisEngine.cs
    │   └── ContentRenderer.cs
    ├── InnerShiftLab.Outbox/           # human-in-the-loop outbox
    │   ├── PlatformFormatter.cs        #   per-platform dims/captions/hashtags
    │   ├── OutboxPackageBuilder.cs     #   media + captions + manifest.json
    │   ├── GitOutboxExporter.cs        #   git orphan-branch pickup (default)
    │   ├── PackageExporters.cs         #   Google Drive (Workspace) / local
    │   ├── OutboxService.cs            #   daily cycle + confirm
    │   └── OutboxSettings.cs
    ├── InnerShiftLab.Auth/             # QUARANTINED (Features:AutoPublish)
    │   └── Auth.cs
    ├── InnerShiftLab.Providers/        # QUARANTINED (Features:AutoPublish)
    │   ├── MetaProvider.cs
    │   ├── TikTokProvider.cs
    │   ├── YouTubeProvider.cs
    │   └── ProviderRouter.cs
    ├── InnerShiftLab.Monetization/
    │   └── MonetizationLogger.cs
    └── InnerShiftLab.Scheduling/
        └── QuartzJobs.cs
```

---

## 13. First-7-days revenue checklist

- [ ] Day 1: Deploy to Codespaces, git `outbox` pickup branch + Termux notifier wired
- [ ] Day 2: First outbox package posted manually to all 4 platforms, links go live
- [ ] Day 3: Linktree has UTMs working, first clicks tracked
- [ ] Day 4: First Skool free signups
- [ ] Day 5: First 5-DM sequence triggers fire
- [ ] Day 6: First Inner Circle conversion
- [ ] Day 7: First €37/month recurring revenue ✓

---

Last updated: 2026-07-15 · IRIS Headless Content Factory v2.0

