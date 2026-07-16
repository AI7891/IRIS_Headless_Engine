# IRIS Headless Content Factory — the Outbox Workflow

## Why the pivot

Automated cross-posting to Instagram/Facebook/TikTok/YouTube was retired (2026-07) because:

1. **Platform risk** — unattended API posting through unverified apps with an unproven
   token-refresh pattern risks flagging and account bans on every platform at once.
2. **Compliance surface** — storing long-lived social OAuth tokens raises EU
   cybersecurity/GDPR exposure we don't want for a solo operation.

IRIS is now a **headless content factory with a human-in-the-loop outbox**. Nothing
about curation, scoring, rendering, UTM attribution, or Skool monetization changed —
only the last mile: a human posts, instead of an API call.

The provider/publish code was **not deleted**. It is quarantined behind
`Features:AutoPublish` (default `false`): not registered in DI, not scheduled in
Quartz, endpoints not mapped. If the platform apps ever get verified, flip the flag
and the old pipeline comes back exactly as it was.

## The daily cycle

Every day at **09:00 UTC** the `DailyOutboxJob` runs (or trigger it any time with
`POST /api/outbox/build`):

0. **Retry stuck exports** — any package whose earlier export failed (items still
   `Pending`) is re-exported before anything new is built. A dedicated
   `ExportRetryJob` also sweeps every 15 minutes. A Drive hiccup never loses a day
   of content.
1. **Curate** — `IIrisEngine` (untouched) picks from the queue; if empty it
   auto-curates the top-scored hooks. The run builds the next
   `Outbox:PackagesPerRun` queued slots (default **1**; one package = one hook
   across all its platforms), best-first. Remaining slots stay queued; each
   package builds under its own try/catch, so one failing slot never blocks the
   rest. (`Iris:MaxPostsPerDayPerPlatform` no longer drives the outbox — it is
   quarantined config, read only by the retired auto-publish `DailyPostJob`.)
2. **Render one variant per platform**, each with correct dimensions, caption
   limit, and hashtag count:

   | Platform  | Media               | Dimensions | Caption limit | Hashtags | Clickable links | Title |
   |-----------|---------------------|------------|---------------|----------|-----------------|-------|
   | Instagram | image               | 1080×1350  | 2,200         | 8        | no (bio)        | —     |
   | Facebook  | image               | 1080×1350  | 63,206        | 3        | yes             | —     |
   | TikTok    | video (image fallback) | 1080×1920 | 2,200       | 5        | no (bio)        | —     |
   | YouTube   | video (image fallback) | 1080×1920 | 5,000       | 3        | yes             | ≤100 chars |

   Overlong captions are trimmed at word boundaries — **the tracking link and
   hashtags always survive intact**, which is what keeps Skool attribution working.
   Each variant's link is rewritten with `utm_source=<platform>` and
   `utm_medium=manual` (the engine's neutral `utm_source=iris` / `utm_medium=organic`
   defaults are replaced), while `utm_campaign` (hook) and `utm_content` (pillar)
   are left byte-for-byte intact. So a Skool join traces back to *both* the hook and
   the platform it was posted on — `/api/monetization/summary` reports a `bySource`
   (and `byPlatform`) breakdown.

   **Non-clickable platforms (Instagram, TikTok)**: a raw URL in the caption is
   inert. The caption prepends `🔗 Link in bio → linktr.ee` above the full UTM URL,
   which stays in the caption so the operator can paste it into the bio/Linktree.
   For one-tap convenience the same bare tracked URL is also written as
   `link.txt` in that platform's folder (referenced as `linkFile` in the manifest).
   Keep the bio link carrying `utm_source=instagram` (or `tiktok`) so those joins
   stay attributed; a bio left on the plain Linktree URL still converts, just
   without hook/platform UTMs.
3. **Persist to the `outbox` table** in SQLite (source of truth). Item lifecycle:
   `Pending → Exported → Posted` (or `Skipped`). The package directory is stored on
   each row so a retry export needs no re-render.
4. **Export the package** — media + `caption.txt` (or `title.txt` + `description.txt`
   for YouTube) + `link.txt` (IG/TikTok) + `manifest.json` — for phone pickup. The
   default exporter **pushes it to the `outbox` branch of this repo** (see
   *Delivery* below); the package also always stays under
   `output/outbox/<date>/<packageId>/`.
5. **Operator posts manually** on each platform (copy caption, upload media), then
   confirms each one:

   ```
   POST /api/outbox/{packageId}/{platform}/confirm
   {"postUrl": "https://instagram.com/p/..."}     # body optional
   ```

UTM attribution and the Skool monetization webhook keep working unchanged, because
the UTM link lives inside the caption text the operator pastes.

## Package layout

```
output/outbox/2026-07-15/<packageId>/
├── manifest.json          # hook, pillar, per-platform files, confirm endpoints
├── instagram/  caption.txt (bio cue + URL) · link.txt (bare URL) · media.png
├── facebook/   caption.txt · media.png
├── tiktok/     caption.txt (bio cue + URL) · link.txt (bare URL) · media.mp4 (media.png fallback)
└── youtube/    title.txt · description.txt · media.mp4 (media.png fallback)
```

The same tree is delivered to the operator's phone by the configured exporter
(git branch by default — see *Delivery* below).

## AI creator content in the outbox

Set `Outbox:UseContentCreator: true` to render packages from the AI content
pipeline (Anthropic script → Pexels image carousel → ElevenLabs voiceover →
ffmpeg-composed video) instead of plain text cards. When enabled, every daily
package calls the pipeline once with the hook text: video platforms get the
composed mp4 re-encoded to their dimensions, image platforms get the lead
carousel slide cover-cropped to their dimensions, YouTube's title uses the AI
title, and the AI caption is used with the tracked link re-appended so creator
posts stay attributable. It is **opt-in** because it spends Anthropic/Pexels/
ElevenLabs credits and needs those keys in `ContentCreator` settings. Any
pipeline failure logs a warning and falls back to text-card rendering — an
external API outage never sinks the daily run.

## Delivery

The exporter is chosen by precedence: **Git** (default) → **Google Drive**
(Workspace only) → **Local** (no-op). The active one is named in the startup log.
If a delivery fails, items stay `Pending`, the package stays local, and it is
retried on the next daily run and by the 15-min `ExportRetryJob` — or force it with
`POST /api/outbox/{packageId}/export`. Nothing is ever lost.

### Git branch (default — zero cost, personal account)

`Outbox:Git` pushes each package to an **orphan branch** (`outbox`) of this repo
using the ambient Codespaces git credentials — no new secret, no cost. The operator
opens the pickup link in the **GitHub mobile app**.

- The branch is **force-pushed as a single squashed commit** every export. Git
  history is deliberately not kept: SQLite is the source of truth, and daily MP4s in
  permanent history would bloat the repo. Each export mirrors the last
  `RetentionDays` (default 14) of `output/outbox/`, so it is idempotent and
  self-healing — exporting today also re-publishes the still-retained earlier days.
- The `exportRef` is a GitHub tree URL, e.g.
  `https://github.com/<owner>/<repo>/tree/outbox/2026-07-16/<packageId>`, which opens
  straight to the package folder in the app.

```json
"Outbox": {
  "Git": {
    "Enabled": true,
    "Branch": "outbox",
    "RetentionDays": 14,
    "Repository": ""      // empty = derive from GITHUB_REPOSITORY / origin remote
  }
}
```

Auth is the ambient credential helper; if `GITHUB_TOKEN` is set it is supplied to
`git push` via a credential helper that reads it from the inherited environment — so
it is never placed in the process argument list (visible in `ps`), never interpolated
into a URL, and is redacted from every log/exception. The export runs in a temp
directory and never touches the app's own working tree.

At startup the app resolves and logs the push target
(`Outbox git target: owner/repo (branch 'outbox')`), so a healthy log confirms
delivery is wired. If it can't resolve a target it logs an **error** but keeps
serving — outbox export just fails until you fix `Outbox:Git:Repository`.

#### Use a dedicated outbox repo (configured: `AI7891/IRIS_Outbox`)

`git clone` fetches every branch, so pointing the outbox at the **code** repo puts two
weeks of media on a branch of it — and every future clone and Codespace rebuild then
downloads it. `Outbox:Git:Repository` and the devcontainer grant are already set to
`AI7891/IRIS_Outbox`; the operator must do these steps, **in order**:

1. Create a **private** repo `AI7891/IRIS_Outbox` — empty, **no README**: the exporter
   force-pushes an orphan branch and will overwrite anything there.
   It must be private because it holds unpublished content and, via the captions, the
   tracked links.
2. **Rebuild the Codespace** — the devcontainer `customizations.codespaces.repositories`
   permission grant only takes effect when the Codespace is created:
   ```json
   "customizations": {
     "codespaces": {
       "repositories": {
         "AI7891/IRIS_Outbox": { "permissions": { "contents": "write" } }
       }
     }
   }
   ```
3. **Authorize the extra repository** when GitHub prompts on that first create.
4. Verify with `POST /api/outbox/build` that the `outbox` branch appears in
   **IRIS_Outbox**, not in `IRIS_Headless_Engine`.

Emptying `Repository` still works (it falls back to the code repo), but the startup
log will **warn** about the clone bloat.

### Google Drive (Workspace Shared Drive only — NOT personal Google)

> ⚠️ **Drive export requires a paid Google Workspace Shared Drive.** A Google
> **service account has no storage quota and cannot own files**, so uploading into a
> folder shared from a personal Gmail account fails immediately with
> `403 storageQuotaExceeded` ("Service Accounts do not have storage quota. Leverage
> shared drives, or use OAuth delegation instead"). Only enable this if you have a
> Workspace plan and a **Shared Drive** (which the org, not the service account,
> owns). On a free/personal Google account, use git delivery above.

If you do have a Shared Drive, set `Outbox:GoogleDrive:Enabled: true` (and
`Outbox:Git:Enabled: false`), then:

1. [Google Cloud Console](https://console.cloud.google.com) → enable the **Drive API**.
2. **IAM & Admin → Service Accounts → Create service account**, then **Keys → Add
   key → JSON**; store the key outside the repo (e.g. `~/secrets/iris-drive.json`).
3. Add the service account's email as a **member of the Shared Drive** (Content
   Manager). Copy the Shared Drive folder id.
4. Configure `ServiceAccountJsonPath` (or the `GOOGLE_APPLICATION_CREDENTIALS` env
   var) and `FolderId`. The uploader is already shared-drive aware (`drive.file`
   scope, `supportsAllDrives`).

## Phone notifications (Termux)

`scripts/termux-outbox-notify.sh` pings your phone when a package is ready so you
don't have to poll. It requires the **Termux:API** app and `pkg install termux-api jq`.

- Polls `GET /api/outbox?status=Exported` every `INTERVAL` seconds (default 300),
  grouping variants by package.
- Fires one Android notification per **new** package; tapping it (or the **Open**
  button) runs `termux-open-url` on the `exportRef` — the GitHub pickup folder.
- **First run seeds silently**: it records the currently-exported package ids without
  notifying, so a fresh install doesn't burst-notify history. It never notifies twice
  for the same package, and tolerates the Codespace being asleep without exiting.
- `scripts/termux-boot-start.sh` launches it in the background alongside the
  heartbeat (log at `~/iris/notify.log`); the heartbeat stays the foreground process.

Tunables: `IRIS_URL`, `INTERVAL`, `STATE_FILE` (default `~/.iris/seen-packages`).

## Retention (FIFO)

Media doesn't pile up forever. The `RetentionJob` runs daily at **09:30 UTC** (30 min
after the build) and can be triggered on demand with `POST /api/outbox/prune-now`.

- **Only media is pruned.** The rendered image/video/text files are deleted; the
  SQLite `outbox` **rows are never touched** — they carry the caption, `exportRef` and
  posted state that UTM/monetization attribution depends on. A pruned package's row
  stays, flagged `mediaPruned: true` (visible in `GET /api/outbox`).
- **Age pass** (`KeepDays`, default 30): media older than this is pruned even if never
  posted — the abandoned-content cap.
- **Size pass** (`MaxTotalMegabytes`, default 2048; `0` = off): when the local
  `output/outbox/` tree exceeds the cap, the oldest eligible packages are pruned
  **oldest-first until just under it**, then it stops.
- **Safety** (`KeepUnpostedPackages`, default true): media for a package you haven't
  acted on yet (any `Pending`/`Exported` item) is never pruned by the size pass — only
  the age pass can remove it, and only once it passes `KeepDays`.
- **Git delivery needs no separate cleanup**: the next export force-pushes a mirror of
  what remains locally, so pruning local dirs removes them from the branch too. **Drive
  folders are deleted explicitly** by the prune (404-tolerant; a Drive hiccup never
  breaks the sweep).

## Outbox settings (`Outbox` section)

```json
"Outbox": {
  "Platforms": [ "instagram", "facebook", "tiktok", "youtube" ],
  "PackagesPerRun": 1,        // packages built per daily run (one package = one hook)
  "RenderVideo": true,        // wrap stills into mp4 for TikTok/YouTube when ffmpeg is present
  "UseContentCreator": false, // opt-in AI media pipeline (costs API credits; falls back to text cards)
  "Git": { "Enabled": true, "Branch": "outbox", "RetentionDays": 14, "Repository": "" },
  "GoogleDrive": { "Enabled": false, "ServiceAccountJsonPath": "", "FolderId": "" },
  "Retention": { "Enabled": true, "KeepDays": 30, "MaxTotalMegabytes": 2048, "KeepUnpostedPackages": true }
}
```

`Outbox:Git:Repository` — **strongly recommended: a dedicated private repo** (see *Use
a dedicated outbox repo* above). Empty falls back to the code repo and bloats every
clone. Delivery precedence when both Git and Drive are enabled: **Git wins** (a startup
warning is logged).

## Endpoint reference

| Endpoint | Purpose |
|---|---|
| `GET /api/outbox?status=exported&limit=50` | List outbox items (status filter optional: pending/exported/posted/skipped) |
| `GET /api/outbox/{packageId}` | All platform variants of one package |
| `POST /api/outbox/build` | Retry pending exports, then build + export the next `PackagesPerRun` queued packages (array) |
| `POST /api/outbox/{packageId}/export` | Re-export one package whose export failed (idempotent; 409 if its files are gone). Also swept automatically every 15 min by `ExportRetryJob` |
| `POST /api/outbox/prune-now` | Run the FIFO media-retention sweep now (same as the 09:30 cron) |
| `POST /api/outbox/{packageId}/{platform}/confirm` | Mark a variant as manually posted (optional body: `{"postUrl":"..."}`) |
| `POST /api/outbox/{packageId}/{platform}/skip` | Mark a variant as deliberately not posted |

Confirm/skip progress is mirrored into the `posts` table: the package row moves
`Queued → Publishing` (first confirm) `→ Published` (all platforms confirmed or
skipped, with at least one posted; all-skipped marks it `Failed`), so existing
reporting keeps working. Lifecycle transitions are guarded: a `Posted` variant
can't be skipped, a `Skipped` one can't be confirmed, and rebuilding a package
never regresses a terminal status.

## Quarantined: the auto-publish pipeline

Everything below only exists when `Features:AutoPublish` is `true` in
`appsettings.json`:

- DI: `ITokenVault`, `IMetaProvider`, `ITiktokProvider`, `IYoutubeProvider`,
  `IProviderRouter`, and the meta/tiktok/youtube HTTP clients
- Quartz: `DailyPostJob` (auto-publish at 09:00 UTC), `TokenRefreshJob` (hourly)
- Endpoints: `/api/providers/*`, `/auth/*` (all OAuth logins/callbacks and the
  Meta webhook), `/api/creator/publish`, `/api/op/dry-run`

With the flag off (the default), `POST /api/creator/publish` and the other
endpoints simply don't exist (404), and no OAuth token is ever requested or stored.
