#!/data/data/com.termux/files/usr/bin/bash
# =============================================================================
#  Termux:Boot launcher — starts the heartbeat automatically when the phone
#  boots, so you never have to open Termux manually.
#
#  Install (one time):
#    1. Install the "Termux:Boot" app from F-Droid, then open it once.
#    2. mkdir -p ~/.termux/boot
#    3. cp this file to ~/.termux/boot/termux-boot-start.sh
#    4. chmod +x ~/.termux/boot/termux-boot-start.sh
#    5. Edit IRIS_URL below, and make sure termux-heartbeat.sh is at ~/iris/.
#
#  termux-wake-lock keeps the CPU awake so the loop survives Doze mode.
# =============================================================================
set -u

# --- EDIT THIS ---------------------------------------------------------------
export IRIS_URL="https://YOUR-CODESPACE-5000.app.github.dev"
export INTERVAL="600"
HEARTBEAT="$HOME/iris/termux-heartbeat.sh"
NOTIFIER="$HOME/iris/termux-outbox-notify.sh"
# -----------------------------------------------------------------------------

# Secrets (IRIS_KEY=...) live in one operator-owned file outside the repo, not
# pasted into three scripts. Create it with: echo 'IRIS_KEY=...' > ~/.iris/env
if [ -f "$HOME/.iris/env" ]; then
  set -a
  . "$HOME/.iris/env"
  set +a
fi

termux-wake-lock 2>/dev/null || true

if [ ! -x "$HEARTBEAT" ]; then
  echo "Heartbeat script not found/executable at $HEARTBEAT" >&2
  exit 1
fi

# Log to files so you can check them later:
#   tail -f ~/iris/heartbeat.log   ·   tail -f ~/iris/notify.log
mkdir -p "$HOME/iris"

# Outbox notifier runs in the background (its own poll interval, default 300s).
if [ -x "$NOTIFIER" ]; then
  ( INTERVAL=300 "$NOTIFIER" >> "$HOME/iris/notify.log" 2>&1 & )
else
  echo "Outbox notifier not found/executable at $NOTIFIER (skipping)" >&2
fi

# Heartbeat stays in the foreground so boot behaviour is unchanged.
exec "$HEARTBEAT" >> "$HOME/iris/heartbeat.log" 2>&1
