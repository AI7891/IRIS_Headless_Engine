#!/data/data/com.termux/files/usr/bin/bash
# =============================================================================
#  IRIS status at a glance — one command to check the pipeline from your phone.
#  Shows: health, readiness, per-platform token status, queue depth, revenue.
#
#  Usage:
#    export IRIS_URL="https://YOUR-CODESPACE-5000.app.github.dev"
#    export IRIS_KEY="..."   # the API key; sent as X-Iris-Key, never in a URL
#    ./termux-status.sh
#
#  Tip: `pkg install jq` for the clean formatted view.
# =============================================================================
set -u

IRIS_URL="${IRIS_URL:-https://YOUR-CODESPACE-5000.app.github.dev}"
IRIS_KEY="${IRIS_KEY:-}"
fetch() { curl -fsS --max-time 15 -H "X-Iris-Key: $IRIS_KEY" "$IRIS_URL$1" 2>/dev/null; }

if [ -z "$IRIS_KEY" ]; then
  echo "!! IRIS_KEY is empty — requests will 401. Put IRIS_KEY=... in \$HOME/.iris/env (see docs/deploy-phone.md)." >&2
fi

echo "IRIS @ $IRIS_URL"
echo "-------------------------------------------"

if ! fetch /healthz >/dev/null; then
  echo "health   : DOWN  (codespace asleep/stopped, IRIS_URL wrong, or IRIS_KEY rejected)"
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

  # Outbox: what's waiting to be posted manually, and where to pick it up.
  pending=$(fetch '/api/outbox?status=Pending' | jq 'length')
  exported=$(fetch '/api/outbox?status=Exported' | jq 'length')
  echo "outbox   : ${pending:-?} pending · ${exported:-?} exported (awaiting post)"
  pickup=$(fetch '/api/outbox?status=Exported' | jq -r 'sort_by(.createdAt) | last | .exportRef // empty')
  [ -n "$pickup" ] && echo "pickup   : $pickup"
  # Retention: how many items still have local media vs pruned (records are always kept).
  fetch '/api/outbox?limit=1000' | jq -r '
    "media    : \([.[] | select(.mediaPruned | not)] | length) live · \([.[] | select(.mediaPruned)] | length) pruned"'

  fetch /api/monetization/summary | jq -r '
    "revenue  : €\(.totalRevenueEur)   joins:\(.totalJoins)   clicks:\(.totalClicks)",
    "by source: \((.bySource // []) | map("\(.source) €\(.revenueEur)") | join("   "))"'
else
  echo "(install jq for a clean view:  pkg install jq)"
  echo "providers: $(fetch /api/providers/status)"
  echo "queue    : $(fetch /api/iris/queue)"
  echo "outbox   : $(fetch '/api/outbox?status=Pending')"
  echo "summary  : $(fetch /api/monetization/summary)"
fi
