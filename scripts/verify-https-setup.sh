#!/usr/bin/env bash
# Sanity-check HTTPS setup for CoDIRA on the machine where you run it.
#
# Modes:
#   Self-signed Kestrel (ServerHttps): checks ERSimulatorApp/certs/server.pfx exists and shows expiry.
#   Caddy + Let's Encrypt (ServerCaddy): checks nothing binds :8443 on public interfaces (optional),
#   and reminds you to probe via your real hostname on :443.
#
# Usage:
#   ./scripts/verify-https-setup.sh
#   ./scripts/verify-https-setup.sh https://127.0.0.1:8443   # curl TLS handshake (self-signed: -k implied)
#
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PFX="${REPO_ROOT}/ERSimulatorApp/certs/server.pfx"

echo "=== CoDIRA HTTPS verification ==="
echo ""

if [[ -f "$PFX" ]]; then
  echo "[PFX] Found: $PFX"
  if command -v openssl >/dev/null 2>&1; then
    echo "[PFX] Subject / validity (pkcs12 → x509):"
    openssl pkcs12 -in "$PFX" -nokeys -passin pass: 2>/dev/null \
      | openssl x509 -noout -subject -dates 2>/dev/null || echo "  (could not parse; install openssl or check PFX password in appsettings)"
  else
    echo "[PFX] openssl not in PATH — install openssl to inspect expiry."
  fi
else
  echo "[PFX] Missing: $PFX"
  echo "      For ServerHttps (self-signed): run ./scripts/generate-https-dev-cert.sh [SERVER_IP] [hostname]"
  echo "      For production LE certs: use Caddy + ServerCaddy — no PFX in Kestrel (see deploy/caddy/Caddyfile.example)."
fi

echo ""
if ss -ltnp 2>/dev/null | grep -q ':8443'; then
  echo "[ports] Something is listening on TCP 8443 (typical for ServerHttps)."
elif command -v ss >/dev/null 2>&1; then
  echo "[ports] Nothing listening on :8443 right now (OK if you use Caddy on :443 only)."
fi

TEST_URL="${1:-}"
if [[ -n "$TEST_URL" ]]; then
  echo ""
  echo "[curl] HEAD $TEST_URL"
  if curl -skI --max-time 10 "$TEST_URL" | head -n 8; then
    :
  else
    echo "curl failed — is the app running with ServerHttps?"
  fi
fi

echo ""
echo "Reminders:"
echo "  • Let's Encrypt / ACME requires a DNS name pointing at this VM (not only a bare public IP)."
echo "  • Open cloud security group TCP 80 + 443 for Caddy; app stays on 127.0.0.1:8081 with ServerCaddy."
echo "  • After systemd unit changes: sudo systemctl daemon-reload && sudo systemctl restart caddy codira-app"
