#!/data/data/com.termux/files/usr/bin/bash
# =============================================================================
#  IRIS outbox notifier — pings your phone (Termux) when a package is ready.
#
#  Polls /api/outbox?status=Exported and fires one Android notification per new
#  package. Tapping it (or the "Open" button) opens the GitHub pickup folder in
#  the app, where you post each platform manually and confirm.
#
#  Requires:
#    - The "Termux:API" app (F-Droid) + `pkg install termux-api jq`.
#      Without termux-notification it falls back to printing to the log.
#
#  Usage:
#    export IRIS_URL="https://YOUR-CODESPACE-5000.app.github.dev"
#    ./termux-outbox-notify.sh
#
#  Tunables (env vars):
#    IRIS_URL    full forwarded Codespaces URL (no trailing slash)
#    IRIS_KEY    the API key (matches the IRIS_API_KEY Codespaces secret);
#                sent as the X-Iris-Key header — header only, never in a URL
#    INTERVAL    seconds between polls (default 300 = 5 min)
#    STATE_FILE  seen-package ids, one per line (default ~/.iris/seen-packages)
# =============================================================================
set -u

IRIS_URL="${IRIS_URL:-https://YOUR-CODESPACE-5000.app.github.dev}"
IRIS_KEY="${IRIS_KEY:-}"
INTERVAL="${INTERVAL:-300}"
STATE_FILE="${STATE_FILE:-$HOME/.iris/seen-packages}"

if [ "$IRIS_URL" = "https://YOUR-CODESPACE-5000.app.github.dev" ]; then
  echo "!! Set IRIS_URL to your real forwarded Codespaces URL first." >&2
fi
if [ -z "$IRIS_KEY" ]; then
  echo "!! IRIS_KEY is empty — requests will 401. Put IRIS_KEY=... in \$HOME/.iris/env (see docs/deploy-phone.md)." >&2
fi

if ! command -v jq >/dev/null 2>&1; then
  echo "!! jq is required: pkg install jq" >&2
  exit 1
fi

HAVE_NOTIFY=1
if ! command -v termux-notification >/dev/null 2>&1; then
  HAVE_NOTIFY=0
  echo "!! termux-notification not found — install the Termux:API app and 'pkg install termux-api'." >&2
  echo "   Falling back to printing new packages to this log." >&2
fi

mkdir -p "$(dirname "$STATE_FILE")"

# Fetch the current Exported packages as lines: "<packageId>\t<count>\t<hookId>\t<exportRef>".
# One line per package (variants grouped). Empty on failure/none.
fetch_packages() {
  curl -fsS --max-time 15 -H "X-Iris-Key: $IRIS_KEY" "$IRIS_URL/api/outbox?status=Exported" 2>/dev/null \
    | jq -r '
        group_by(.packageId)[]
        | [ .[0].packageId, (length|tostring), (.[0].hookId // "package"), (.[0].exportRef // "") ]
        | @tsv' 2>/dev/null
}

seen() { grep -qxF "$1" "$STATE_FILE" 2>/dev/null; }

# First run: seed silently so a fresh install doesn't fire a burst for history.
if [ ! -f "$STATE_FILE" ]; then
  : > "$STATE_FILE"
  seeded=0
  while IFS=$'\t' read -r pkg count hook ref; do
    [ -z "$pkg" ] && continue
    echo "$pkg" >> "$STATE_FILE"
    seeded=$((seeded + 1))
  done <<EOF
$(fetch_packages)
EOF
  echo "Seeded $seeded already-exported package(s) without notifying."
fi

echo "IRIS outbox notifier -> $IRIS_URL every ${INTERVAL}s. Ctrl-C to stop."
while true; do
  ts="$(date '+%Y-%m-%d %H:%M:%S')"
  packages="$(fetch_packages)"
  if [ -z "$packages" ]; then
    # Unreachable (Codespace asleep) or nothing exported — log and keep going.
    curl -fsS --max-time 15 -H "X-Iris-Key: $IRIS_KEY" "$IRIS_URL/healthz" >/dev/null 2>&1 \
      || echo "$ts  API unreachable (codespace asleep, or IRIS_KEY rejected), retrying"
  else
    while IFS=$'\t' read -r pkg count hook ref; do
      [ -z "$pkg" ] && continue
      if seen "$pkg"; then continue; fi
      if [ "$HAVE_NOTIFY" -eq 1 ] && [ -n "$ref" ]; then
        termux-notification \
          --title "IRIS: package ready ($count variants)" \
          --content "$hook — tap to open the pickup folder" \
          --id "iris-$pkg" \
          --action "termux-open-url '$ref'" \
          --button1 "Open" --button1-action "termux-open-url '$ref'" 2>/dev/null
      fi
      echo "$ts  new package $pkg ($count variants) $hook -> ${ref:-<no link>}"
      echo "$pkg" >> "$STATE_FILE"
    done <<EOF
$packages
EOF
  fi
  sleep "$INTERVAL"
done
