# Phone-side scripts (Termux)

These keep the app running continuously without a laptop. The app itself runs in
your **GitHub Codespace** (auto-started by `.devcontainer/devcontainer.json`);
your phone's job is only to keep the Codespace awake by pinging `/healthz`.

| File | Role |
|---|---|
| `termux-heartbeat.sh` | Ping `/healthz` every 10 min so Codespaces never idles out. |
| `termux-outbox-notify.sh` | Poll `/api/outbox?status=Exported` and fire an Android notification per new package; tap opens the git pickup folder in the GitHub app. |
| `termux-boot-start.sh` | Termux:Boot launcher — runs the heartbeat (foreground) + notifier (background) on phone boot. |
| `termux-status.sh` | One-shot at-a-glance status: health, queue depth, outbox pending/exported, revenue. |

## How it fits together

```
Phone (Termux)                     GitHub Codespaces
--------------                     -----------------
termux-heartbeat.sh  --- GET /healthz ------>  IRIS app (auto-started by devcontainer)
  every 10 min                                 Quartz jobs keep running 24/7
termux-outbox-notify.sh -- GET /api/outbox -->  daily package pushed to the `outbox`
  every 5 min             (status=Exported)     git branch → notification → GitHub app
```

## Quick start

1. In your Codespace, open the **Ports** tab and make port **5000** visibility
   **Public** (or keep it private and log in on the phone). Copy the forwarded URL,
   e.g. `https://fuzzy-space-5000.app.github.dev`.
2. In Termux on your phone:
   ```bash
   pkg install curl
   mkdir -p ~/iris
   # copy termux-heartbeat.sh into ~/iris/ (via git clone, scp, or paste)
   chmod +x ~/iris/termux-heartbeat.sh
   export IRIS_URL="https://YOUR-CODESPACE-5000.app.github.dev"
   ~/iris/termux-heartbeat.sh
   ```
   You should see `ok` lines every 10 minutes.

## Check status any time

```bash
pkg install jq          # optional, for the clean formatted view
export IRIS_URL="https://YOUR-CODESPACE-5000.app.github.dev"
~/iris/termux-status.sh
```

Example output:

```
health   : UP
ready    : yes (db reachable)
queue    : 1 slot(s)
outbox   : 0 pending · 1 exported (awaiting post)
pickup   : https://github.com/OWNER/REPO/tree/outbox/2026-07-16/<packageId>
revenue  : €0   joins:0   clicks:0
by source:
```

The `pickup` line is the git branch URL for the latest exported package — tap it to
open the folder in the GitHub app. (The `providers:` section only appears when the
quarantined `Features:AutoPublish` pipeline is enabled.)

## Package-ready notifications (optional)

```bash
pkg install termux-api jq       # + install the "Termux:API" app from F-Droid
export IRIS_URL="https://YOUR-CODESPACE-5000.app.github.dev"
~/iris/termux-outbox-notify.sh
```

The first run silently records the packages already exported (so you don't get a
burst for history), then fires one notification per **new** package. Tapping it opens
the pickup folder on the `outbox` git branch in the GitHub app. Without the Termux:API
app it falls back to printing new packages to the log.

## Auto-start on boot (optional)

1. Install **Termux:Boot** from F-Droid and open it once.
2. ```bash
   mkdir -p ~/.termux/boot
   cp ~/iris/termux-boot-start.sh ~/.termux/boot/
   chmod +x ~/.termux/boot/termux-boot-start.sh
   ```
3. Edit `IRIS_URL` inside `~/.termux/boot/termux-boot-start.sh`.
4. Reboot the phone — the heartbeat (foreground) and outbox notifier (background)
   start on their own and log to `~/iris/heartbeat.log` and `~/iris/notify.log`.

> Battery: `termux-wake-lock` (used by the boot script) keeps the loop alive under
> Doze. Also set Android → Settings → Apps → Termux → Battery → **Unrestricted**.

See `docs/deploy-phone.md` for the full phone setup, and `docs/credentials-setup.md`
for filling in the platform tokens.
