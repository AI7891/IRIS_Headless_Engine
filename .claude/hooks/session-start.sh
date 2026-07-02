#!/bin/bash
# =============================================================================
#  SessionStart hook — prepares the IRIS pipeline for Claude Code on the web.
#  Installs the .NET 8 SDK (if missing), ffmpeg + DejaVu fonts (needed by
#  ContentRenderer), then restores/builds and runs the test suite.
#
#  Runs in ASYNC mode: the session starts immediately and this setup runs in the
#  background, so a slow install never blocks or fails the session start.
#  Resilient by design: a failing step is logged but never aborts the session.
# =============================================================================

# Only run in the remote (web) environment; do nothing locally.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

# Async: return control to the session now; the rest runs in the background.
# asyncTimeout (ms) is generous enough for a cold container (SDK + build + tests).
echo '{"async": true, "asyncTimeout": 600000}'

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

log() { echo "[iris-setup] $*" >&2; }
# Run a step; log failures but keep going so the session always starts.
try() { "$@" || log "step failed (continuing): $*"; }

SUDO=""
if [ "$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1; then
  SUDO="sudo"
fi

# --- System packages: ffmpeg + fonts (idempotent) ----------------------------
if ! command -v ffmpeg >/dev/null 2>&1 || ! fc-list 2>/dev/null | grep -qi dejavu; then
  log "installing ffmpeg + fonts-dejavu-core"
  try $SUDO apt-get update -y
  try $SUDO apt-get install -y --no-install-recommends ffmpeg fonts-dejavu-core
else
  log "ffmpeg + fonts already present"
fi

# --- .NET 8 SDK (only if missing) --------------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
  log "dotnet not found; installing .NET 8 SDK"
  try $SUDO apt-get update -y
  try $SUDO apt-get install -y dotnet-sdk-8.0
else
  log "dotnet already present: $(dotnet --version 2>/dev/null)"
fi

# --- Restore / build / test --------------------------------------------------
if command -v dotnet >/dev/null 2>&1; then
  log "restoring + building src/InnerShiftLab.csproj"
  try dotnet restore "$PROJECT_DIR/src/InnerShiftLab.csproj"
  try dotnet build "$PROJECT_DIR/src/InnerShiftLab.csproj" -c Release --no-restore

  log "running tests"
  try dotnet test "$PROJECT_DIR/tests/InnerShiftLab.Tests/InnerShiftLab.Tests.csproj" -c Release
else
  log "dotnet unavailable; skipped build/test"
fi

log "setup complete"
exit 0
