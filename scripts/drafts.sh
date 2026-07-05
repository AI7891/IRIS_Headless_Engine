#!/usr/bin/env bash
# =============================================================================
#  drafts.sh — CLI for the queue-and-approve workflow (wraps the local API).
#  Nothing is ever published without you approving it here (or via the API).
#
#  Usage:
#    ./scripts/drafts.sh list [status]      # default: PendingApproval ('all' for everything)
#    ./scripts/drafts.sh show <id>
#    ./scripts/drafts.sh approve <id>       # PendingApproval -> Approved -> ReadyToPublish
#    ./scripts/drafts.sh reject <id>        # PendingApproval -> Rejected
#    ./scripts/drafts.sh publish <id>       # ReadyToPublish -> Published via official API,
#                                           #   or exports the draft if OAuth isn't configured
#    ./scripts/drafts.sh export <id>        # write a manual-posting package to output/exports/
#    ./scripts/drafts.sh retry <id>         # Failed -> ReadyToPublish
#
#  Env: IRIS_URL (default http://localhost:5000)
# =============================================================================
set -euo pipefail

BASE="${IRIS_URL:-http://localhost:5000}"
CMD="${1:-list}"

json() { if command -v jq >/dev/null 2>&1; then jq .; else cat; echo; fi; }

case "$CMD" in
  list)
    STATUS="${2:-PendingApproval}"
    curl -sf "$BASE/api/drafts?status=$STATUS" | json
    ;;
  show)
    curl -sf "$BASE/api/drafts/${2:?usage: drafts.sh show <id>}" | json
    ;;
  approve|reject|publish|export|retry)
    ID="${2:?usage: drafts.sh $CMD <id>}"
    curl -sf -X POST "$BASE/api/drafts/$ID/$CMD" | json
    ;;
  *)
    echo "Unknown command '$CMD'. See header of this script for usage." >&2
    exit 1
    ;;
esac
