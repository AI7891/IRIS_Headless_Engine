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
   Keep the bio link carrying `utm_source=instagram` (or `tiktok`) so those joins
   stay attributed; a bio left on the plain Linktree URL still converts, just
   without hook/platform UTMs.
3. **Persist to the `outbox` table** in SQLite (source of truth). Item lifecycle:
   `Pending → Exported → Posted` (or `Skipped`). The package directory is stored on
   each row so a retry export needs no re-render.
4. **Export the package** — media + `caption.txt` (or `title.txt` + `description.txt`
   for YouTube) + `manifest.json` — to Google Drive for phone pickup. If Drive isn't
   configured, the package stays under `output/outbox/<date>/<packageId>/`.
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
├── instagram/  caption.txt (bio cue + URL) · media.png
├── facebook/   caption.txt · media.png
├── tiktok/     caption.txt (bio cue + URL) · media.mp4 (media.png fallback)
└── youtube/    title.txt · description.txt · media.mp4 (media.png fallback)
```

The same tree is mirrored to the Drive folder (recursively, any depth), one
subfolder per platform.

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

## Google Drive setup (service account — no user OAuth)

Deliberately uses a **service-account key**, not a user OAuth flow, so no personal
long-lived tokens are stored anywhere:

1. In [Google Cloud Console](https://console.cloud.google.com): create (or reuse) a
   project → **APIs & Services → Enable APIs** → enable **Google Drive API**.
2. **IAM & Admin → Service Accounts → Create service account** (no roles needed).
3. Open the account → **Keys → Add key → JSON**. Download the key file and put it
   somewhere outside the repo (e.g. `~/secrets/iris-drive.json`).
4. In Google Drive (phone or web): create a folder, e.g. `IRIS Outbox`, and
   **share it with the service account's email** (`...@...iam.gserviceaccount.com`)
   as **Editor**. Copy the folder id from its URL (`/folders/<id>`).
5. Configure:

   ```json
   "Outbox": {
     "GoogleDrive": {
       "Enabled": true,
       "ServiceAccountJsonPath": "/home/you/secrets/iris-drive.json",
       "FolderId": "<the folder id>"
     }
   }
   ```

   `ServiceAccountJsonPath` may be left empty if the standard
   `GOOGLE_APPLICATION_CREDENTIALS` env var points at the key file.

Each daily package appears as `<date> <hookId>/` inside the shared folder. If an
export fails, the items stay `Pending`, the package remains available locally, and
the error is logged — nothing is lost: the next daily run retries it automatically,
or force it immediately with `POST /api/outbox/{packageId}/export`.

## Outbox settings (`Outbox` section)

```json
"Outbox": {
  "Platforms": [ "instagram", "facebook", "tiktok", "youtube" ],
  "PackagesPerRun": 1,        // packages built per daily run (one package = one hook)
  "RenderVideo": true,        // wrap stills into mp4 for TikTok/YouTube when ffmpeg is present
  "UseContentCreator": false, // opt-in AI media pipeline (costs API credits; falls back to text cards)
  "GoogleDrive": { "Enabled": false, "ServiceAccountJsonPath": "", "FolderId": "" }
}
```

## Endpoint reference

| Endpoint | Purpose |
|---|---|
| `GET /api/outbox?status=exported&limit=50` | List outbox items (status filter optional: pending/exported/posted/skipped) |
| `GET /api/outbox/{packageId}` | All platform variants of one package |
| `POST /api/outbox/build` | Retry pending exports, then build + export the next `PackagesPerRun` queued packages (array) |
| `POST /api/outbox/{packageId}/export` | Re-export one package whose export failed (idempotent; 409 if its files are gone). Also swept automatically every 15 min by `ExportRetryJob` |
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
