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
#    IRIS_KEY   the API key (matches the IRIS_API_KEY Codespaces secret);
#               sent as the X-Iris-Key header — header only, never in a URL
#    INTERVAL   seconds between pings (default 600 = 10 min)
# =============================================================================
set -u

IRIS_URL="${IRIS_URL:-https://YOUR-CODESPACE-5000.app.github.dev}"
IRIS_KEY="${IRIS_KEY:-}"
INTERVAL="${INTERVAL:-600}"

if [ "$IRIS_URL" = "https://YOUR-CODESPACE-5000.app.github.dev" ]; then
  echo "!! Set IRIS_URL to your real forwarded Codespaces URL first." >&2
fi
if [ -z "$IRIS_KEY" ]; then
  echo "!! IRIS_KEY is empty — requests will 401. Put IRIS_KEY=... in \$HOME/.iris/env (see docs/deploy-phone.md)." >&2
fi

echo "IRIS heartbeat -> $IRIS_URL/healthz every ${INTERVAL}s. Ctrl-C to stop."
while true; do
  ts="$(date '+%Y-%m-%d %H:%M:%S')"
  if curl -fsS --max-time 15 -H "X-Iris-Key: $IRIS_KEY" "$IRIS_URL/healthz" >/dev/null 2>&1; then
    echo "$ts  ok"
  else
    echo "$ts  FAILED (codespace asleep/stopped, IRIS_URL wrong, or IRIS_KEY rejected)"
  fi
  sleep "$INTERVAL"
done
