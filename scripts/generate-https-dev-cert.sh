#!/usr/bin/env bash
# Create a self-signed PFX for Kestrel (development / LAN). Browsers will warn until you trust
# the cert, or you replace this with a public CA (e.g. Let's Encrypt) in production.
#
# Usage:
#   ./scripts/generate-https-dev-cert.sh
#   ./scripts/generate-https-dev-cert.sh 203.0.113.8          # add server IP (voice / mic over HTTPS)
#   ./scripts/generate-https-dev-cert.sh 203.0.113.8 my.host.name
#
# Then copy the Kestrel block from ERSimulatorApp/appsettings.Example.json into your (ignored)
# appsettings.json and start with: ./start-app.sh ServerHttps

set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${REPO_ROOT}/ERSimulatorApp/certs"
PFX_PATH="${OUT_DIR}/server.pfx"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$OUT_DIR"
cd "$TMP"

DNS_IDX=1
IP_IDX=1
{
  echo "[req]"
  echo "distinguished_name = dn"
  echo "x509_extensions    = v3_req"
  echo "prompt = no"
  echo ""
  echo "[dn]"
  echo "CN = CoDIRA-dev"
  echo ""
  echo "[v3_req]"
  echo "keyUsage         = digitalSignature, keyEncipherment"
  echo "extendedKeyUsage = serverAuth"
  echo "subjectAltName   = @san"
  echo ""
  echo "[san]"
  echo "DNS.$DNS_IDX = localhost"
} > openssl.cnf
DNS_IDX=$((DNS_IDX + 1))
IP_IDX=1
echo "IP.$IP_IDX = 127.0.0.1" >> openssl.cnf
IP_IDX=$((IP_IDX + 1))

# Optional args: add as IP (IPv4 dotted quad) or DNS name
for arg in "$@"; do
  if [[ "$arg" =~ ^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}$ ]]; then
    echo "IP.$IP_IDX = $arg" >> openssl.cnf
    IP_IDX=$((IP_IDX + 1))
  else
    echo "DNS.$DNS_IDX = $arg" >> openssl.cnf
    DNS_IDX=$((DNS_IDX + 1))
  fi
done

openssl genrsa -out key.pem 2048
openssl req -new -x509 -key key.pem -out cert.pem -days 3650 -config openssl.cnf -extensions v3_req
# Empty password matches appsettings.Example.json Kestrel:Certificates:Default:Password
openssl pkcs12 -export -out server.pfx -inkey key.pem -in cert.pem -passout pass:

cp -f server.pfx "$PFX_PATH"
echo ""
echo "Wrote: $PFX_PATH"
echo "Ensure ERSimulatorApp/appsettings.json has the Kestrel block (see appsettings.Example.json), then load the new cert by restarting the app."
echo "  Interactive:  ./start-app.sh ServerHttps   (or CODIRA_STOP_EXISTING_APP=1 if a prior run holds 8081/8443)"
echo "  systemd:      ./start-app.sh systemd-restart"
echo "Then open https://YOUR_IP:8443  (browsers will warn for self-signed certs until the cert is trusted.)"
echo ""
