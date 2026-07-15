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
   `Pending`) is re-exported before anything new is built. A Drive hiccup never
   loses a day of content.
1. **Curate** — `IIrisEngine` (untouched) picks from the queue; if empty it
   auto-curates the top-scored hooks. Queued slots are packaged best-first until
   `Iris:MaxPostsPerDayPerPlatform` (default 2) is reached for every platform.
   The cap is seeded from the SQLite outbox table, so container restarts don't
   reset it; slots over the cap stay queued.
2. **Render one variant per platform**, each with correct dimensions, caption
   limit, and hashtag count:

   | Platform  | Media               | Dimensions | Caption limit | Hashtags | Title |
   |-----------|---------------------|------------|---------------|----------|-------|
   | Instagram | image               | 1080×1350  | 2,200         | 8        | —     |
   | Facebook  | image               | 1080×1350  | 63,206        | 3        | —     |
   | TikTok    | video (image fallback) | 1080×1920 | 2,200       | 5        | —     |
   | YouTube   | video (image fallback) | 1080×1920 | 5,000       | 3        | ≤100 chars |

   Overlong captions are trimmed at word boundaries — **the UTM link and hashtags
   always survive intact**, which is what keeps Skool attribution working. Each
   variant's link is stamped with `utm_source=<platform>`, so a Skool join traces
   back to *both* the hook (`utm_campaign`) and the platform it was posted on —
   `/api/monetization/summary` reports a `byPlatform` breakdown.

   **Instagram special case**: IG captions are not clickable, so a raw URL there
   is dead weight. The IG caption carries `🔗 Link in bio →` instead, and the
   stamped link ships as `instagram/link.txt`. To keep IG attribution, point the
   bio/Linktree button at that link (or paste it into the post's first comment).
   If the bio just stays on the plain Linktree URL, IG joins still count — they
   arrive without a hook/platform UTM.
3. **Persist to the `outbox` table** in SQLite (source of truth). Item lifecycle:
   `Pending → Exported → Posted` (or `Skipped`).
4. **Export the package** — media + `caption.txt` (+ `title.txt` for YouTube) +
   `manifest.json` — to Google Drive for phone pickup. If Drive isn't configured,
   the package stays under `output/outbox/<date>/<packageId>/`.
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
├── instagram/  caption.txt · link.txt (bio link) · media.png
├── facebook/   caption.txt · media.png
├── tiktok/     caption.txt · media.mp4 (media.png fallback)
└── youtube/    title.txt · description.txt · media.mp4 (media.png fallback)
```

The same tree is mirrored to the Drive folder, one subfolder per platform.

## AI creator content in the outbox

`POST /api/outbox/creator` runs the full content pipeline (Claude script →
Pexels image carousel → ElevenLabs voiceover → ffmpeg-composed video) and
packages the result exactly like a daily package: video platforms get the
composed mp4, image platforms get the lead carousel slide, and the AI caption
gains a UTM-tracked link (`utm_campaign=creator-<scriptId>`) so creator posts
show up in monetization reporting alongside hook posts. Requires the
Anthropic/Pexels/ElevenLabs keys from `ContentCreator` settings.

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

## Endpoint reference

| Endpoint | Purpose |
|---|---|
| `GET /api/outbox?status=exported&limit=50` | List outbox items (status filter optional: pending/exported/posted/skipped) |
| `GET /api/outbox/{packageId}` | All platform variants of one package |
| `POST /api/outbox/build` | Retry pending exports, then build + export today's packages (array) up to the daily cap |
| `POST /api/outbox/creator` | Run the AI pipeline (Claude script → Pexels carousel → ElevenLabs voiceover → composed video) and package its output — body optional: `{"keywords":"...","slideCount":5}` |
| `POST /api/outbox/{packageId}/export` | Re-export one package whose export failed (idempotent; 409 if its files are gone) |
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
