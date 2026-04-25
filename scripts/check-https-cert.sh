#!/usr/bin/env bash
# Inspect ERSimulatorApp/certs/server.pfx (Kestrel HTTPS). Password empty unless env PFX_PASS is set.
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PFX="${1:-$REPO_ROOT/ERSimulatorApp/certs/server.pfx}"
PASS="${PFX_PASS:-}"
PASSIN=()
if [ -n "$PASS" ]; then
  PASSIN=(-passin "pass:$PASS")
else
  PASSIN=(-passin pass:)
fi

if [ ! -f "$PFX" ]; then
  echo "No PFX at: $PFX" >&2
  echo "Create one: ./scripts/generate-https-dev-cert.sh" >&2
  exit 1
fi

echo "File: $PFX"
ls -la "$PFX"
echo ""
echo "--- Certificate (public) ---"
openssl pkcs12 -in "$PFX" -nokeys "${PASSIN[@]}" 2>/dev/null | openssl x509 -noout -subject -issuer -dates
echo ""
openssl pkcs12 -in "$PFX" -nokeys "${PASSIN[@]}" 2>/dev/null | openssl x509 -noout -ext subjectAltName 2>/dev/null || true
echo ""
if openssl pkcs12 -in "$PFX" -nokeys "${PASSIN[@]}" 2>/dev/null | openssl x509 -noout -checkend 0; then
  echo "Not expired (openssl -checkend)."
else
  echo "Expired or -checkend failed." >&2
  exit 1
fi
echo ""
echo "Note: A valid file + date does not mean browsers show a green lock. Public trust needs a cert from a CA"
echo "      the client trusts (e.g. Let's Encrypt). Self-signed = encrypted but 'Not secure' in Chrome."
echo "      PFX = PKCS#12 bundle: private key + certificate (same as people mean by 'TLS' or 'SSL' server cert)."
