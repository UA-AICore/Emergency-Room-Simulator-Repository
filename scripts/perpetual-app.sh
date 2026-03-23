#!/usr/bin/env bash
# ExecStart helper for systemd (codira-app.service). See start-app.sh systemd-install.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT/ERSimulatorApp"

PROFILE="${CODIRA_LAUNCH_PROFILE:-ServerHttps}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"

dotnet build -nologo -v q
exec dotnet run --launch-profile "$PROFILE" --no-build
