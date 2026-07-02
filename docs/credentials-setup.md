# Credentials setup — Meta, TikTok, YouTube

This maps every `REPLACE_ME` in `appsettings.json` to where you get it, and walks
the OAuth login you run afterwards. Two layers:

1. **Register a developer app** on each platform → copy the IDs/secrets into `appsettings.json`.
2. **Run the OAuth login** (`/auth/<platform>/login`) and click **Allow** → the app stores an access token in the encrypted vault.

Until step 2 is done, `GET /api/providers/status` shows `no_token` for that platform.

> Your redirect URIs use your forwarded Codespaces host, e.g.
> `https://YOUR-CODESPACE-5000.app.github.dev`. Replace `YOUR-CODESPACE` with your
> real codespace name everywhere below, and register the **exact** same URL in each
> developer console — a mismatch is the #1 cause of login failures.

---

## 1. Meta (Instagram + Facebook)

**Prerequisites:** a Facebook **Page** and an **Instagram Business** (or Creator)
account linked to that Page. Personal IG accounts will not work.

**Register the app:** https://developers.facebook.com → *My Apps* → *Create App*
→ type **Business**. Then *App settings → Basic* for the credentials, and add the
**Facebook Login** product to register the redirect URI.

| Console value | `appsettings.json` field |
|---|---|
| App ID | `Socials:Instagram:AppId` |
| App Secret | `Socials:Instagram:AppSecret` |
| Valid OAuth Redirect URI = `https://YOUR-CODESPACE-5000.app.github.dev/auth/meta/callback` | `Socials:Instagram:RedirectUri` |
| (same App Secret) | `Monetization:MetaAppSecret` — used for webhook signature + challenge verification |

Facebook Page fields (optional; the OAuth callback also resolves these automatically):

| Console value | `appsettings.json` field |
|---|---|
| Page ID | `Socials:Facebook:PageId` |
| Page access token | `Socials:Facebook:PageAccessToken` |

**Permissions the app requests:** `pages_show_list`, `pages_read_engagement`,
`pages_manage_posts`, `business_management`, `instagram_basic`,
`instagram_content_publish`. To post to real (non-test) accounts, submit these for
**App Review**. Until approved, add yourself as a **Tester** and post to your own account.

**Login:** open `GET /auth/meta/login`, click Allow. The callback exchanges the code
for a 60-day long-lived token and auto-resolves your IG business id + page token.

---

## 2. TikTok

**Register the app:** https://developers.tiktok.com → *Manage apps* → create an app,
add the **Content Posting API** product, and add the **Login Kit** redirect URI.

| Console value | `appsettings.json` field |
|---|---|
| Client Key | `Socials:Tiktok:ClientKey` |
| Client Secret | `Socials:Tiktok:ClientSecret` |
| Redirect URI = `https://YOUR-CODESPACE-5000.app.github.dev/auth/tiktok/callback` | `Socials:Tiktok:RedirectUri` |

**Scopes requested:** `user.info.basic`, `video.publish`, `video.upload`. The Content
Posting API requires an **audit** before it can post publicly; unaudited apps are
restricted to private (`SELF_ONLY`) posts — which is exactly the safe default the
code uses, so you can test immediately.

**Login:** open `GET /auth/tiktok/login`, click Allow. Tokens are short-lived (~2h)
but the app now stores the **refresh token** and refreshes automatically.

---

## 3. YouTube (Google)

**Register the app:** https://console.cloud.google.com → create a project →
*APIs & Services*.

1. *Library* → enable **YouTube Data API v3**.
2. *OAuth consent screen* → configure it; add yourself under **Test users**.
3. *Credentials* → *Create credentials* → **OAuth client ID** → type **Web application**;
   add the redirect URI.

| Console value | `appsettings.json` field |
|---|---|
| Client ID | `Socials:Youtube:ClientId` |
| Client Secret | `Socials:Youtube:ClientSecret` |
| Authorized redirect URI = `https://YOUR-CODESPACE-5000.app.github.dev/auth/youtube/callback` | `Socials:Youtube:RedirectUri` |
| Your channel id | `Socials:Youtube:ChannelId` (already set; change if not yours) |

**Scopes requested:** `youtube.upload`, `youtube.force-ssl`. Uploads default to
**unlisted** until you verify things work, then flip to public.

**Login:** open `GET /auth/youtube/login`, click Allow. To get a **refresh token**
Google requires consent with offline access — if a re-login ever shows no refresh
token, remove the app at https://myaccount.google.com/permissions and log in again.

---

## 4. Other secrets in `appsettings.json`

| Field | What to put |
|---|---|
| `Iris:HeartbeatSecret` | Any long random string (used to guard the heartbeat). Prefer setting it via env var `Iris__HeartbeatSecret` rather than committing it. |
| `Monetization:SkoolWebhookSecret` | The signing secret from your Skool webhook config (HMAC-SHA256 of the body → `x-skool-signature`). |

**Never commit real secrets.** Any config key can be overridden by an environment
variable using `__` (double underscore) for nesting, which takes precedence over
`appsettings.json`. Examples:

```bash
export Socials__Instagram__AppSecret="…"
export Monetization__SkoolWebhookSecret="…"
export Iris__HeartbeatSecret="$(openssl rand -hex 32)"
```

---

## 5. Verify

```bash
curl -s http://localhost:5000/api/providers/status
```

Each platform should read `"valid"` with an `expiresAt` once its login is complete.
`"no_token"` = you still need to run that platform's `/auth/<platform>/login`.
