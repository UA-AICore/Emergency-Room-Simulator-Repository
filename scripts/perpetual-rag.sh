#!/usr/bin/env bash
# ExecStart helper for systemd (codira-rag.service). See start-app.sh systemd-install.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

if [ -f rag_backend/sync_kb_env_from_appsettings.py ]; then
  eval "$(python3 rag_backend/sync_kb_env_from_appsettings.py "$REPO_ROOT")" || true
fi

if [ -f rag_backend/.venv/bin/activate ]; then
  # shellcheck source=/dev/null
  source rag_backend/.venv/bin/activate
elif [ -f rag_backend/venv/bin/activate ]; then
  # shellcheck source=/dev/null
  source rag_backend/venv/bin/activate
else
  echo "No rag_backend/.venv or venv found." >&2
  exit 1
fi

cd rag_backend
exec python3 -m uvicorn app.main:app --host 0.0.0.0 --port 8010
