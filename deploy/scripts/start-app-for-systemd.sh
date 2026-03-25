#!/usr/bin/env bash
# Older systemd units use this path. Delegates to scripts/perpetual-app.sh.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
exec "$REPO/scripts/perpetual-app.sh"
