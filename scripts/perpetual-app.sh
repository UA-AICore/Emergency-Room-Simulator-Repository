#!/usr/bin/env bash
# ExecStart helper for systemd (codira-app.service). See start-app.sh systemd-install.
# Binds: ServerHttps → 8081+8443 + PFX; ServerCaddy → 127.0.0.1:8081 only (Caddy does TLS);
# Server → 0.0.0.0:8081. Kestrel cert path is for ServerHttps only.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT/ERSimulatorApp"

PROFILE="${CODIRA_LAUNCH_PROFILE:-ServerHttps}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"

if [ "$PROFILE" = "ServerHttps" ] && [ ! -f "certs/server.pfx" ]; then
  echo "ServerHttps needs certs/server.pfx (see ERSimulatorApp/appsettings.Example.json Kestrel, scripts/generate-https-dev-cert.sh)." >&2
  echo "Current directory: $(pwd)" >&2
  exit 1
fi

dotnet build -nologo -v q
exec dotnet run --launch-profile "$PROFILE" --no-build
