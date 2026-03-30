#!/usr/bin/env bash
# ExecStart helper for systemd (codira-rag.service). See start-app.sh systemd-install.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

codira_rag_log() { echo "[codira-rag] $*" >&2; }

command -v python3 >/dev/null 2>&1 || { codira_rag_log "ERROR: python3 not on PATH"; exit 1; }

# Merge Knowledge Base env from appsettings (same as interactive start-app.sh). Never fail the unit on sync errors.
if [ -f rag_backend/sync_kb_env_from_appsettings.py ]; then
  set +e
  __kb_sync=$(python3 rag_backend/sync_kb_env_from_appsettings.py "$REPO_ROOT")
  __kb_ec=$?
  set -e
  if [ "$__kb_ec" -eq 0 ] && [ -n "$__kb_sync" ]; then
    eval "$__kb_sync" || true
  elif [ "$__kb_ec" -ne 0 ]; then
    codira_rag_log "warning: sync_kb_env_from_appsettings.py failed (exit $__kb_ec); continuing without merged KB env"
  fi
fi

if [ -f rag_backend/.venv/bin/activate ]; then
  # shellcheck source=/dev/null
  source rag_backend/.venv/bin/activate
elif [ -f rag_backend/venv/bin/activate ]; then
  # shellcheck source=/dev/null
  source rag_backend/venv/bin/activate
else
  codira_rag_log "ERROR: No Python venv found at rag_backend/.venv or rag_backend/venv."
  codira_rag_log "Fix on the server (run as deploy user):"
  codira_rag_log "  cd $REPO_ROOT/rag_backend && python3 -m venv .venv && . .venv/bin/activate && pip install -r requirements.txt"
  exit 1
fi

cd rag_backend
if ! python3 -c "import uvicorn" 2>/dev/null; then
  codira_rag_log "ERROR: uvicorn not importable inside venv. Run: pip install -r $REPO_ROOT/rag_backend/requirements.txt"
  exit 1
fi
if ! python3 -c "import app.main" 2>/dev/null; then
  codira_rag_log "ERROR: app.main failed to import (see stderr above)."
  python3 -c "import app.main" || true
  exit 1
fi

exec python3 -m uvicorn app.main:app --host 0.0.0.0 --port 8010
