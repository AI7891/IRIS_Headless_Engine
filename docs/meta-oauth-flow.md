# Meta OAuth Flow — Complete Reference

The full token lifecycle, from a phone browser visit to a published Instagram post.

## Sequence diagram

```
Phone browser                Codespaces                  Meta Graph API
     │                            │                            │
     │ GET /auth/meta/login       │                            │
     ├───────────────────────────►│                            │
     │                            │ Build authorization URL     │
     │                            │  (state=csrf, scope=...)   │
     │◄───── 302 facebook.com ────┤                            │
     │                            │                            │
     │ User approves                                                 ─────►│
     │◄───── 302 /auth/meta/callback?code=X&state=Y ────│
     │                            │                            │
     │ (browser may show blank)   │ Step 1: Exchange code      │
     │                            │  POST /oauth/access_token  │
     │                            ├───────────────────────────►│
     │                            │◄──── short-lived token ────│
     │                            │                            │
     │                            │ Step 2: Long-lived         │
     │                            │  GET /oauth/access_token   │
     │                            │   ?grant_type=fb_exchange  │
     │                            ├───────────────────────────►│
     │                            │◄──── 60-day token ─────────│
     │                            │                            │
     │                            │ Step 3: Resolve IG biz ID  │
     │                            │  GET /me/accounts?         │
     │                            │   fields=instagram_business│
     │                            ├───────────────────────────►│
     │                            │◄──── IG user + page token ─│
     │                            │                            │
     │                            │ Step 4: AES-256 encrypt    │
     │                            │  Save to data/meta.tok     │
     │                            │                            │
     │                            │ Step 5: Heartbeat /readyz  │
     │                            │  returns 200               │
     │                            │                            │
     │ (later, scheduled post)    │                            │
     │                            │ POST /media (IG container) │
     │                            ├───────────────────────────►│
     │                            │◄──── container_id ─────────│
     │                            │                            │
     │                            │ Poll status (REELS)        │
     │                            ├───────────────────────────►│
     │                            │◄──── FINISHED ─────────────│
     │                            │                            │
     │                            │ POST /media_publish        │
     │                            ├───────────────────────────►│
     │                            │◄──── post_id ──────────────│
```

## File-level breakdown

| Step | File | Method |
|---|---|---|
| 1. Build login URL | `MetaProvider.cs` | `BuildAuthorizationUrl` |
| 2. Handle callback | `Program.cs` | `app.MapGet("/auth/meta/callback", ...)` |
| 3. Exchange code | `MetaProvider.cs` | `ExchangeCodeAsync` |
| 4. Long-lived exchange | `MetaProvider.cs` | `ExchangeForLongLivedAsync` |
| 5. Resolve IG user | `MetaProvider.cs` | `ResolveInstagramUserAsync` |
| 6. Encrypt + save | `Auth.cs` | `TokenVault.SaveTokensAsync` |
| 7. Publish post | `MetaProvider.cs` | `PublishInstagramMediaAsync` |
| 8. Retry on 429/5xx | `MetaProvider.cs` | inside `PublishFacebookPostAsync` (FB); IG inherits from HttpClient |
| 9. Token status | `MetaProvider.cs` | `GetTokenStatusAsync` |

## Scopes requested

```
public_profile
pages_show_list
pages_read_engagement
pages_manage_posts
business_management
instagram_basic
instagram_content_publish
```

**Why each one**:
- `public_profile` — basic identity
- `pages_show_list` — list pages you manage
- `pages_read_engagement` — analytics
- `pages_manage_posts` — post to Facebook Page
- `business_management` — required for IG Business discovery
- `instagram_basic` — basic IG account info
- `instagram_content_publish` — publish posts, reels, stories

## Token lifetime & refresh

| Token type | Lifetime | Refresh |
|---|---|---|
| Short-lived (Step 3) | 1-2 hours | — |
| Long-lived (Step 4) | **60 days** | Re-run `/auth/meta/login` (manual) |
| Page token (Step 5) | **never expires** if page is active | — |

The long-lived user token is refreshed by re-running the full flow. The page access token obtained via `ResolveInstagramUserAsync` is permanent for active pages.

## What gets stored (encrypted, AES-256)

`data/meta.tok` contains a JSON like:
```json
{
  "AccessToken": "<long-lived user token>",
  "TokenType": "Bearer",
  "ExpiresAt": "2026-08-21T00:00:00+00:00",
  "PageAccessToken": "<permanent page token>",
  "PageId": "123456789",
  "IgBusinessId": "17841234567890123",
  "IgUsername": "the_inner_shift_lab"
}
```

The `data/vault.key` file holds the 32-byte AES key. Both are 0600 perms on Linux.

## Why the original code was missing the core logic

The first version only did Step 3 (code → short-lived), never the long-lived exchange or IG resolution. Result: short-lived token expired in 1-2 hours, every publish failed with `OAuthException 190: invalid token`. This v1.0 fixes it end-to-end.

## Webhook security

For incoming webhooks (`POST /auth/meta/webhook`):
- `X-Hub-Signature-256: sha256=<hex>` — HMAC of body with `MetaAppSecret`
- Constant-time comparison via `CryptographicOperations.FixedTimeEquals`
- Reject if missing or mismatched

For webhook setup (`GET /auth/meta/webhook`):
- Meta calls with `hub.mode=subscribe&hub.verify_token=X&hub.challenge=Y`
- We accept if `hub.verify_token == MetaAppSecret`
- Echo back the challenge

