# Phone-side scripts (Termux)

These keep the app running continuously without a laptop. The app itself runs in
your **GitHub Codespace** (auto-started by `.devcontainer/devcontainer.json`);
your phone's job is only to keep the Codespace awake by pinging `/healthz`.

| File | Role |
|---|---|
| `termux-heartbeat.sh` | Ping `/healthz` every 10 min so Codespaces never idles out. |
| `termux-boot-start.sh` | Termux:Boot launcher — runs the heartbeat automatically on phone boot. |

## How it fits together

```
Phone (Termux)                     GitHub Codespaces
--------------                     -----------------
termux-heartbeat.sh  --- GET /healthz --->  IRIS app (auto-started by devcontainer)
  every 10 min                              4 Quartz jobs keep running 24/7
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

## Auto-start on boot (optional)

1. Install **Termux:Boot** from F-Droid and open it once.
2. ```bash
   mkdir -p ~/.termux/boot
   cp ~/iris/termux-boot-start.sh ~/.termux/boot/
   chmod +x ~/.termux/boot/termux-boot-start.sh
   ```
3. Edit `IRIS_URL` inside `~/.termux/boot/termux-boot-start.sh`.
4. Reboot the phone — the heartbeat starts on its own and logs to
   `~/iris/heartbeat.log`.

> Battery: `termux-wake-lock` (used by the boot script) keeps the loop alive under
> Doze. Also set Android → Settings → Apps → Termux → Battery → **Unrestricted**.

See `docs/deploy-phone.md` for the full phone setup, and `docs/credentials-setup.md`
for filling in the platform tokens.
