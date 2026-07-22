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
# Codespace: crispy-winner-9g6w6x5jqg5hpg5v — the app is on the port-5000 forward
# (editor URL is https://crispy-winner-9g6w6x5jqg5hpg5v.github.dev/)
export IRIS_URL="https://crispy-winner-9g6w6x5jqg5hpg5v-5000.app.github.dev"
export INTERVAL="600"
HEARTBEAT="$HOME/iris/termux-heartbeat.sh"
# -----------------------------------------------------------------------------

termux-wake-lock 2>/dev/null || true

if [ ! -x "$HEARTBEAT" ]; then
  echo "Heartbeat script not found/executable at $HEARTBEAT" >&2
  exit 1
fi

# Log to a file so you can check it later: tail -f ~/iris/heartbeat.log
mkdir -p "$HOME/iris"
exec "$HEARTBEAT" >> "$HOME/iris/heartbeat.log" 2>&1
