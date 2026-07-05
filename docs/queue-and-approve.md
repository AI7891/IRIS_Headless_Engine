# Queue-and-Approve Workflow

The engine is **not an unattended auto-poster**. Unattended automated posting
violates Meta, TikTok, and YouTube platform terms and risks account bans, so the
pipeline keeps all its content-prep value (scheduling, curation, per-platform
formatting, media rendering, batching, SQLite state, Serilog logging) but its
final stage always produces a **draft that a human must approve** before anything
can reach a platform API.

## Draft lifecycle

```
                    ┌─ approve ─▶ Approved ──▶ ReadyToPublish ─┬─ publish ─▶ Published
PendingApproval ────┤                              ▲           └─ error ───▶ Failed
                    └─ reject ──▶ Rejected         └────────── retry ──────────┘
```

- Every scheduled job (`DailyDraftJob`, 09:00 UTC) and every content-creator run
  ends by inserting `PendingApproval` rows into the `PostDrafts` table.
- Approval is explicit and per-draft. Only approval moves a draft to
  `ReadyToPublish`; rejection is terminal.
- A draft can **never** jump straight to `Published` — the state machine is
  enforced in code (`DraftStateMachine`) and atomically in SQL (guarded
  `UPDATE … WHERE Status = expected`), covered by unit tests.
- Publishing uses **only official platform APIs** (Meta Graph API, TikTok
  Content Posting API, YouTube Data API v3) with vault-held OAuth tokens.
  If no official OAuth token is configured for the platform, publish does not
  attempt anything — it exports the draft as a JSON package under
  `output/exports/` for manual posting.

## Approving drafts

CLI (wraps the local API; set `IRIS_URL` if not on localhost:5000):

```bash
./scripts/drafts.sh list                # what's waiting for your decision
./scripts/drafts.sh show 12
./scripts/drafts.sh approve 12          # -> ReadyToPublish
./scripts/drafts.sh reject 13
./scripts/drafts.sh publish 12          # official API, or export if no OAuth
./scripts/drafts.sh export 12           # manual-posting package
./scripts/drafts.sh retry 12            # Failed -> ReadyToPublish
```

Or the endpoints directly:

| Endpoint | Does |
|---|---|
| `GET /api/drafts?status=…` | List (default `PendingApproval`; `all` for everything) |
| `GET /api/drafts/{id}` | Inspect one draft |
| `POST /api/drafts/{id}/approve` | `PendingApproval → Approved → ReadyToPublish` |
| `POST /api/drafts/{id}/reject` | `PendingApproval → Rejected` |
| `POST /api/drafts/{id}/publish` | Official-API publish, or export when OAuth isn't configured |
| `POST /api/drafts/{id}/export` | Manual-posting package to `output/exports/` |
| `POST /api/drafts/{id}/retry` | `Failed → ReadyToPublish` |

The old direct-publish endpoints (`POST /api/providers/{platform}/publish`,
`POST /api/creator/publish`) were removed; `POST /api/creator/draft` creates
PendingApproval drafts instead.

## Database

`PostDrafts` table in `data/iris.db` — migration is applied idempotently at
startup and also available standalone:

- Apply: `scripts/migrations/002_post_drafts.up.sql`
- Rollback: `scripts/migrations/002_post_drafts.down.sql`

Columns: `Id`, `Platform`, `Caption`, `MediaReference`, `ScheduledFor`,
`Status` (CHECK-constrained to the six lifecycle values), `CreatedAt`,
`ApprovedAt`, `PublishedAt`, `PlatformPostId`, `ErrorMessage`; index
`IX_PostDrafts_Status` for fast queue lookups.

## OAuth tokens

The `TokenVault` stores **only official OAuth Bearer tokens** obtained through
each platform's own authorization flow (`/auth/{meta|tiktok|youtube}/login`).
Anything else — session cookies, scraped tokens, non-Bearer credentials — is
rejected at save time. Tokens carry their granted scopes and issue time; the
vault warns when publish scopes are missing (see `OAuthScopes.RequiredForPublish`)
and when no refresh token is present. Refresh continues to run through the
providers' official token endpoints (hourly `TokenRefreshJob`).

Required publish scopes per platform:

- **Meta**: `pages_show_list`, `pages_manage_posts`, `instagram_basic`, `instagram_content_publish`
- **TikTok**: `user.info.basic`, `video.publish`
- **YouTube**: `https://www.googleapis.com/auth/youtube.upload`

## What this tool will not do

No unattended mass-posting, no session-token impersonation or replay, no browser
automation against the platforms, no keep-alive hacks. These violate the
platforms' terms of service and put the accounts at risk of permanent bans —
requests to add them will be declined.
