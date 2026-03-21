#!/usr/bin/env bash
# Start RAG backend, run ingest, then start the .NET app in one terminal.
# Usage: ./start-app.sh [Server|ServerHttps]
#   Server     = HTTP only (http://YOUR_IP:8081), no voice input
#   ServerHttps = HTTPS (https://YOUR_IP:8443), voice input works (default)
# The same launch command is used whether you use a self-signed or Let's Encrypt cert; only appsettings cert paths differ.
set -e
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO_ROOT"
PROFILE="${1:-ServerHttps}"
RAG_PID=""
cleanup() {
  echo ""
  echo "Shutting down..."
  if [ -n "$RAG_PID" ] && kill -0 "$RAG_PID" 2>/dev/null; then
    kill "$RAG_PID" 2>/dev/null || true
    echo "RAG (uvicorn) stopped."
  fi
  exit 0
}
trap cleanup SIGINT SIGTERM

# Knowledge Base API: export env from ERSimulatorApp appsettings (same source as .NET RAG:BaseUrl)
# so uvicorn sees KNOWLEDGE_* / RAG_CONTEXT_SOURCE without duplicating rag_backend/.env.
if [ -f "$REPO_ROOT/rag_backend/sync_kb_env_from_appsettings.py" ]; then
  eval "$(python3 "$REPO_ROOT/rag_backend/sync_kb_env_from_appsettings.py" "$REPO_ROOT")" || true
fi

# Check if port 8010 is already in use (e.g. from a previous run)
if command -v ss >/dev/null 2>&1; then
  PORT_IN_USE=$(ss -tlnp 2>/dev/null | grep -c ':8010 ' || true)
else
  PORT_IN_USE=$( (echo >/dev/tcp/127.0.0.1/8010) 2>/dev/null && echo 1 || echo 0)
fi
if [ "${PORT_IN_USE:-0}" -gt 0 ]; then
  echo "Port 8010 already in use; assuming RAG is already running. Skipping RAG startup."
  RAG_PID=""
else
  echo "Starting RAG backend on port 8010..."
  source rag_backend/.venv/bin/activate 2>/dev/null || source rag_backend/venv/bin/activate
  (cd rag_backend && python3 -m uvicorn app.main:app --host 0.0.0.0 --port 8010) &
  RAG_PID=$!
  echo "RAG PID: $RAG_PID"
fi

echo "Waiting for RAG to be ready..."
for i in 1 2 3 4 5 6 7 8 9 10; do
  if curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:8010/health 2>/dev/null | grep -q 200; then
    echo "RAG is up."
    break
  fi
  sleep 1
done
# Preserve SKIP_PDF_INGEST if sync_kb_env_from_appsettings already exported it
SKIP_PDF_INGEST="${SKIP_PDF_INGEST:-0}"
if [ "${RAG_CONTEXT_SOURCE:-}" = "knowledge_api" ]; then
  SKIP_PDF_INGEST=1
fi
if [ -f rag_backend/.env ] && grep -q '^RAG_CONTEXT_SOURCE=knowledge_api' rag_backend/.env 2>/dev/null; then
  SKIP_PDF_INGEST=1
fi
if [ -f rag_backend/.env ] && grep -q '^SKIP_PDF_INGEST=1' rag_backend/.env 2>/dev/null; then
  SKIP_PDF_INGEST=1
fi
if [ "$SKIP_PDF_INGEST" = "1" ]; then
  echo "Skipping PDF ingest (Knowledge API mode: RAG_CONTEXT_SOURCE=knowledge_api from appsettings/.env, or SKIP_PDF_INGEST=1)."
else
  echo "Running ingest (data/trauma_pdfs)..."
  curl -s -X POST http://127.0.0.1:8010/ingest -H "Content-Type: application/json" -d '{"folder": "data/trauma_pdfs"}' || true
fi
echo ""
if [ "$PROFILE" = "ServerHttps" ]; then
  echo "Starting .NET app as HTTPS site (profile: ServerHttps)..."
  echo "  → https://YOUR_IP:8443 (voice input works)"
  echo "  → http://YOUR_IP:8081 (redirects to HTTPS if cert is configured)"
else
  echo "Starting .NET app (profile: $PROFILE)..."
  echo "  → http://YOUR_IP:8081 (HTTP only, no voice input)"
fi
cd ERSimulatorApp
dotnet build -nologo -v q
dotnet run --launch-profile "$PROFILE"
cleanup
