#!/data/data/com.termux/files/usr/bin/bash
# =============================================================================
#  IRIS heartbeat — keeps the GitHub Codespace awake from your phone (Termux).
#
#  GitHub Codespaces idles out after ~30 min of inactivity. Pinging /healthz on
#  an interval counts as activity, so the scheduler keeps running 24/7.
#
#  Usage:
#    export IRIS_URL="https://YOUR-CODESPACE-5000.app.github.dev"
#    ./termux-heartbeat.sh
#
#  Tunables (env vars):
#    IRIS_URL   full forwarded Codespaces URL (no trailing slash)
#    INTERVAL   seconds between pings (default 600 = 10 min)
# =============================================================================
set -u

IRIS_URL="${IRIS_URL:-https://YOUR-CODESPACE-5000.app.github.dev}"
INTERVAL="${INTERVAL:-600}"

if [ "$IRIS_URL" = "https://YOUR-CODESPACE-5000.app.github.dev" ]; then
  echo "!! Set IRIS_URL to your real forwarded Codespaces URL first." >&2
fi

echo "IRIS heartbeat -> $IRIS_URL/healthz every ${INTERVAL}s. Ctrl-C to stop."
while true; do
  ts="$(date '+%Y-%m-%d %H:%M:%S')"
  if curl -fsS --max-time 15 "$IRIS_URL/healthz" >/dev/null 2>&1; then
    echo "$ts  ok"
  else
    echo "$ts  FAILED (codespace asleep, stopped, or IRIS_URL wrong)"
  fi
  sleep "$INTERVAL"
done
