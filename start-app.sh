#!/usr/bin/env bash
# Start RAG backend, run ingest, then start the .NET app in one terminal.
#
# Interactive (foreground; stops when you Ctrl+C or close the terminal):
#   ./start-app.sh [Server|ServerHttps]
#   Server        = HTTP only (http://YOUR_IP:8081), no voice input
#   ServerHttps   = HTTPS (https://YOUR_IP:8443), voice input works (default)
#
# Perpetual (Linux systemd — survives SSH disconnect & restarts on crash):
#   ./start-app.sh systemd-install [Server|ServerHttps]   # writes units to /tmp, prints sudo cp + enable
#   ./start-app.sh systemd-restart                        # after code changes
#   ./start-app.sh perpetual                              # alias: restart + status
#   ./start-app.sh systemd-status | systemd-stop | systemd-logs
#
# The same launch command is used whether you use a self-signed or Let's Encrypt cert; only appsettings cert paths differ.
set -e
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO_ROOT"

# ----- systemd / perpetual (Linux) -----
codira_service_user() {
  if [ -n "${SUDO_USER:-}" ]; then
    echo "$SUDO_USER"
  else
    echo "${USER:-$(id -un)}"
  fi
}

codira_systemd_install() {
  local profile="${1:-ServerHttps}"
  if [ "$profile" != "Server" ] && [ "$profile" != "ServerHttps" ]; then
    echo "Invalid launch profile: $profile (use Server or ServerHttps)" >&2
    exit 1
  fi
  local svc_user
  svc_user="$(codira_service_user)"
  if [ -z "$svc_user" ] || [ "$svc_user" = "root" ]; then
    echo "Run systemd-install as your deploy user (not root) so User= in units is correct." >&2
    exit 1
  fi
  chmod +x "$REPO_ROOT/scripts/perpetual-rag.sh" "$REPO_ROOT/scripts/perpetual-app.sh" 2>/dev/null || true
  chmod +x "$REPO_ROOT/deploy/scripts/start-rag-for-systemd.sh" "$REPO_ROOT/deploy/scripts/start-app-for-systemd.sh" 2>/dev/null || true
  local TMP
  TMP="$(mktemp -d "${TMPDIR:-/tmp}/codira-systemd-XXXXXX")"
  cat >"$TMP/codira-rag.service" <<UNIT
[Unit]
Description=CoDIRA RAG backend (FastAPI / uvicorn on 8010)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${svc_user}
Group=${svc_user}
ExecStart=${REPO_ROOT}/scripts/perpetual-rag.sh
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT
  cat >"$TMP/codira-app.service" <<UNIT
[Unit]
Description=CoDIRA ER Simulator (.NET / Kestrel)
After=network-online.target codira-rag.service
Wants=codira-rag.service

[Service]
Type=simple
User=${svc_user}
Group=${svc_user}
Environment=CODIRA_LAUNCH_PROFILE=${profile}
Environment=ASPNETCORE_ENVIRONMENT=Production
ExecStart=${REPO_ROOT}/scripts/perpetual-app.sh
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
UNIT
  echo "Generated units in $TMP"
  echo "--- codira-rag.service ---"
  cat "$TMP/codira-rag.service"
  echo "--- codira-app.service ---"
  cat "$TMP/codira-app.service"
  echo ""
  echo "Install with:"
  echo "  sudo cp \"$TMP/codira-rag.service\" \"$TMP/codira-app.service\" /etc/systemd/system/"
  echo "  sudo systemctl daemon-reload"
  echo "  sudo systemctl enable --now codira-rag codira-app"
  echo ""
  echo "After code changes:"
  echo "  ./start-app.sh systemd-restart"
  echo ""
  echo "Stop using interactive ./start-app.sh on the same machine (port conflicts)."
}

codira_need_systemctl() {
  if ! command -v systemctl >/dev/null 2>&1; then
    echo "systemctl not found — perpetual mode needs Linux with systemd." >&2
    exit 1
  fi
}

FIRST="${1:-}"
case "$FIRST" in
  systemd-install|install-systemd)
    shift
    PROFILE="${1:-ServerHttps}"
    codira_systemd_install "$PROFILE"
    exit 0
    ;;
  systemd-restart|restart-systemd)
    codira_need_systemctl
    sudo systemctl restart codira-rag codira-app
    systemctl status codira-rag codira-app --no-pager || true
    exit 0
    ;;
  perpetual)
    codira_need_systemctl
    echo "Restarting codira-rag + codira-app (systemd)..."
    sudo systemctl restart codira-rag codira-app
    systemctl status codira-rag codira-app --no-pager || true
    exit 0
    ;;
  systemd-stop|stop-systemd)
    codira_need_systemctl
    sudo systemctl stop codira-app codira-rag
    exit 0
    ;;
  systemd-status|status-systemd)
    codira_need_systemctl
    systemctl status codira-rag codira-app --no-pager || true
    exit 0
    ;;
  systemd-logs|logs-systemd)
    codira_need_systemctl
    journalctl -u codira-rag -u codira-app -n 80 --no-pager
    exit 0
    ;;
esac

# ----- interactive foreground run -----
PROFILE="${1:-ServerHttps}"
RAG_PID=""
FT_PID=""
cleanup() {
  echo ""
  echo "Shutting down..."
  if [ -n "$RAG_PID" ] && kill -0 "$RAG_PID" 2>/dev/null; then
    kill "$RAG_PID" 2>/dev/null || true
    echo "RAG (uvicorn) stopped."
  fi
  if [ -n "$FT_PID" ] && kill -0 "$FT_PID" 2>/dev/null; then
    kill "$FT_PID" 2>/dev/null || true
    echo "MedGemma-ft (uvicorn) stopped."
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

# Check if port 11435 is already in use (e.g. from a previous run)
if command -v ss >/dev/null 2>&1; then
  FT_PORT_IN_USE=$(ss -tlnp 2>/dev/null | grep -c ':11435 ' || true)
else
  FT_PORT_IN_USE=$( (echo >/dev/tcp/127.0.0.1/11435) 2>/dev/null && echo 1 || echo 0)
fi
if [ "${FT_PORT_IN_USE:-0}" -gt 0 ]; then
  echo "Port 11435 already in use; assuming fine-tuned MedGemma serve is already running. Skipping."
  FT_PID=""
else
  echo "Starting fine-tuned MedGemma serve on port 11435 (logs/finetune-serve.log)..."
  source rag_backend/.venv/bin/activate 2>/dev/null || source rag_backend/venv/bin/activate
  (python3 -m uvicorn rag_backend.finetuning.serve:app --host 127.0.0.1 --port 11435 \
     > logs/finetune-serve.log 2>&1) &
  FT_PID=$!
  echo "MedGemma-ft PID: $FT_PID (model load takes ~45s on first start)"
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
