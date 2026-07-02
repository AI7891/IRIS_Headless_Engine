#!/data/data/com.termux/files/usr/bin/bash
# =============================================================================
#  IRIS status at a glance — one command to check the pipeline from your phone.
#  Shows: health, readiness, per-platform token status, queue depth, revenue.
#
#  Usage:
#    export IRIS_URL="https://YOUR-CODESPACE-5000.app.github.dev"
#    ./termux-status.sh
#
#  Tip: `pkg install jq` for the clean formatted view.
# =============================================================================
set -u

IRIS_URL="${IRIS_URL:-https://YOUR-CODESPACE-5000.app.github.dev}"
fetch() { curl -fsS --max-time 15 "$IRIS_URL$1" 2>/dev/null; }

echo "IRIS @ $IRIS_URL"
echo "-------------------------------------------"

if ! fetch /healthz >/dev/null; then
  echo "health   : DOWN  (codespace asleep/stopped, or IRIS_URL wrong)"
  exit 1
fi
echo "health   : UP"

if fetch /readyz >/dev/null; then
  echo "ready    : yes (db reachable)"
else
  echo "ready    : NO  (db not reachable)"
fi

if command -v jq >/dev/null 2>&1; then
  fetch /api/providers/status | jq -r '
    "providers:",
    "  meta   : \(.meta.status)   \(.meta.expiresAt // "")",
    "  tiktok : \(.tiktok.status)   \(.tiktok.expiresAt // "")",
    "  youtube: \(.youtube.status)   \(.youtube.expiresAt // "")"'
  echo "queue    : $(fetch /api/iris/queue | jq 'length') slot(s)"
  fetch /api/monetization/summary | jq -r '
    "revenue  : €\(.totalRevenueEur)   joins:\(.totalJoins)   clicks:\(.totalClicks)"'
else
  echo "(install jq for a clean view:  pkg install jq)"
  echo "providers: $(fetch /api/providers/status)"
  echo "queue    : $(fetch /api/iris/queue)"
  echo "summary  : $(fetch /api/monetization/summary)"
fi
