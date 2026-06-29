# Daily Operator Runbook — Phone-Driven

This is the day-to-day playbook. Designed to be done entirely from an Android phone + GitHub Codespaces web UI.

## Morning (5 minutes)

1. **Tap Healthcheck shortcut** → `GET /healthz` should return `{"status":"ok"}`
2. **Tap Provider Status shortcut** → `GET /api/providers/status`
   - All three (Meta, TikTok, YouTube) should show `"valid"`
   - If `"expired"` or `"no_token"`, tap the corresponding re-auth shortcut
3. **Tap Today's Conversions shortcut** → review `GET /api/monetization/conversions?limit=10`
4. **Glance at `/api/iris/queue`** → see what the scheduler will publish today

## Midday (2 minutes)

- Check Skool community for new posts / questions
- Reply to any direct messages in Skool DMs
- The DailyPostJob at 09:00 UTC has already published 3 posts — verify by `GET /api/providers/status` or the platform apps

## Evening (5 minutes)

- **Review `/api/monetization/summary`** → see if revenue moved
- If the top campaign in `byCampaign` is showing €0 joins but >50 clicks → check the Skool link, the Linktree UTM, or the Skool webhook config
- Tap **Dry-run a hook** to preview tomorrow's planned posts

## Weekly (15 minutes, every Sunday)

- **Audit hooks performance**: `GET /api/monetization/conversions?limit=200`, group by `utm_campaign`
- **Re-weight pillars** in `pillars.json` based on what converted
- **Bump low-performing hooks' score** to 30, or remove from `hooks.json` if dead
- **Update Linktree link order** based on top-converting destination
- **Re-verify tokens** are all still `valid`

## Emergency: token expired

| Provider | Symptom | Fix |
|---|---|---|
| Meta | `OAuthException 190` in logs | Tap re-auth shortcut, complete flow |
| TikTok | `invalid_token` in logs | Tap re-auth shortcut, complete flow |
| YouTube | `401 Unauthorized` from YouTube | Tap re-auth shortcut, complete flow |

## Emergency: pipeline crashed

1. `GET /healthz` returns connection error
2. Open Codespaces in phone browser → `cd src && dotnet run`
3. Check `data/iris.log` for the last error
4. Restart. DailyPostJob will re-enqueue top 3 hooks automatically

## Emergency: Codespaces idle-killed

The Tasker heartbeat (see README §10) prevents this, but if it does happen:
1. Open Codespaces in phone browser
2. Codespaces may take 30-60s to resume
3. Hit `GET /healthz` to confirm it's up
4. Re-verify the Tasker profile is still active (Android battery optimization may have killed Tasker — whitelist it)

## Common questions

**Q: A post has no image and `ContentRenderer` is throwing. Why?**
A: `RenderImageAsync` needs a system font. On Codespaces, the `dotnet/runtime` base image has DejaVu. Verify with `fc-list | grep DejaVu`. If missing: `sudo apt-get install -y fonts-dejavu-core`.

**Q: Why are TikTok posts `SELF_ONLY`?**
A: Safe default for testing. Edit `TikTokProvider.PublishVideoAsync`, change `privacy_level` from `SELF_ONLY` to `PUBLIC` after you've confirmed end-to-end works.

**Q: YouTube uploads are failing. Why?**
A: Default privacy is `unlisted`. If you want public, edit `YouTubeProvider.UploadVideoAsync`, change `"unlisted"` to `"public"`. Also check daily upload quota (10,000 units; 1 short = ~100 units, 1 long = 1600).

**Q: How do I know which hook is making money?**
A: `GET /api/monetization/conversions?limit=200`, look at the `utm_campaign` column. The hook with the highest `revenue_eur` sum is your top earner.

