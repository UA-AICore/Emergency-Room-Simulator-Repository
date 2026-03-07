#!/usr/bin/env bash
# Start RAG backend, run ingest, then start the .NET app on localhost.
# Usage: ./start-app.sh
#   → http://localhost:5121 (use appsettings.Development.json with RAG BaseUrl http://127.0.0.1:8010)
set -e
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO_ROOT"
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
if [ ! -d rag_backend/.venv ] && [ ! -d rag_backend/venv ]; then
  echo "Creating Python venv in rag_backend/.venv and installing dependencies..."
  python3 -m venv rag_backend/.venv
  source rag_backend/.venv/bin/activate
  pip install -q -r rag_backend/requirements.txt
  echo "Venv ready."
  echo ""
fi
echo "Starting RAG backend on port 8010 (localhost only)..."
source rag_backend/.venv/bin/activate 2>/dev/null || source rag_backend/venv/bin/activate
(cd rag_backend && python3 -m uvicorn app.main:app --host 127.0.0.1 --port 8010) &
RAG_PID=$!
echo "RAG PID: $RAG_PID"
echo "Waiting for RAG to be ready..."
for i in 1 2 3 4 5 6 7 8 9 10; do
  if curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:8010/health 2>/dev/null | grep -q 200; then
    echo "RAG is up."
    break
  fi
  sleep 1
done
echo "Running ingest (data/trauma_pdfs)..."
curl -s -X POST http://127.0.0.1:8010/ingest -H "Content-Type: application/json" -d '{"folder": "data/trauma_pdfs"}' || true
echo ""
echo "Starting .NET app (profile: http, localhost only)..."
echo "  → http://localhost:5121"
cd ERSimulatorApp
dotnet build -nologo -v q
dotnet run --launch-profile http
cleanup
