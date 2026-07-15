# Daily Operator Runbook — Phone-Driven

This is the day-to-day playbook. Designed to be done entirely from an Android phone + GitHub Codespaces web UI. The daily loop is the **outbox workflow** (see [headless-outbox.md](headless-outbox.md)): IRIS renders and exports a package every morning; you post it manually and confirm.

## Morning (10 minutes)

1. **Tap Healthcheck shortcut** → `GET /healthz` should return `{"status":"ok"}`
2. **Open the Google Drive `IRIS Outbox` folder** → today's package appeared at
   09:00 UTC as `<date> <hookId>/`
   - Not there? `GET /api/outbox` to check status, or `POST /api/outbox/build` to build it now
3. **Post each platform folder**, one by one:
   - Open `caption.txt`, copy all → open the platform app → create post → paste
   - Attach `media.png` (IG/FB) or `media.mp4` (TikTok/YouTube)
   - YouTube also gets `title.txt` as the video title
   - **Don't edit the link in the caption** — it carries the UTM attribution
4. **Confirm each post** as you go:
   `POST /api/outbox/{packageId}/{platform}/confirm` with body `{"postUrl":"..."}`
   (the exact URLs are pre-filled per platform in the package's `manifest.json`)

## Midday (2 minutes)

- Check Skool community for new posts / questions
- Reply to any direct messages in Skool DMs
- `GET /api/outbox?status=exported` — anything still listed hasn't been posted/confirmed yet

## Evening (5 minutes)

- **Review `/api/monetization/summary`** → see if revenue moved
- If the top campaign in `byCampaign` is showing €0 joins but >50 clicks → check the Skool link, the Linktree UTM, or the Skool webhook config
- `GET /api/iris/queue` → glance at what's queued for tomorrow's package

## Weekly (15 minutes, every Sunday)

- **Audit hooks performance**: `GET /api/monetization/conversions?limit=200`, group by `utm_campaign`
- **Re-weight pillars** in `pillars.json` based on what converted
- **Bump low-performing hooks' score** to 30, or remove from `hooks.json` if dead
- **Update Linktree link order** based on top-converting destination
- **Clean old packages** out of the Drive folder if it's getting cluttered (SQLite keeps the record)

## Emergency: package didn't export to Drive

1. Check `data/iris.log` for `Outbox export failed`
2. The package is still on disk: `output/outbox/<date>/<packageId>/` — post from there
3. Common causes: service-account key path wrong (`Outbox:GoogleDrive:ServiceAccountJsonPath`),
   Drive folder not shared with the service-account email, folder id wrong
4. `POST /api/outbox/build` builds and exports a fresh package once fixed

## Emergency: pipeline crashed

1. `GET /healthz` returns connection error
2. Open Codespaces in phone browser → `cd src && dotnet run`
3. Check `data/iris.log` for the last error
4. Restart. The DailyOutboxJob auto-curates top hooks when the queue is empty — nothing is lost

## Emergency: Codespaces idle-killed

The Tasker heartbeat (see README §10) prevents this, but if it does happen:
1. Open Codespaces in phone browser
2. Codespaces may take 30-60s to resume
3. Hit `GET /healthz` to confirm it's up
4. Re-verify the Tasker profile is still active (Android battery optimization may have killed Tasker — whitelist it)

## Common questions

**Q: `ContentRenderer` is throwing when building the package. Why?**
A: `RenderImageAsync` needs a system font. On Codespaces, the `dotnet/runtime` base image has DejaVu. Verify with `fc-list | grep DejaVu`. If missing: `sudo apt-get install -y fonts-dejavu-core`.

**Q: TikTok/YouTube folders only contain `media.png`, no video?**
A: The mp4 render needs `ffmpeg` (`sudo apt-get install -y ffmpeg`). The package builder falls back to the still image so the day is never lost — post the image, or reinstall ffmpeg and `POST /api/outbox/build` again.

**Q: I posted but forgot which confirm URL to hit.**
A: Open `manifest.json` in the package — every platform entry has its exact `confirmEndpoint`. Or `GET /api/outbox?status=exported` to list what's still unconfirmed.

**Q: How do I know which hook is making money?**
A: `GET /api/monetization/conversions?limit=200`, look at the `utm_campaign` column. The hook with the highest `revenue_eur` sum is your top earner. This works exactly as before the pivot — the UTM link travels inside the caption you paste.

**Q: Can I re-enable automated posting?**
A: Set `Features:AutoPublish` to `true` in `appsettings.json` and restart — the provider adapters, OAuth endpoints, and DailyPostJob all come back. Only do this if the platform apps are verified; see the warning in the README.
