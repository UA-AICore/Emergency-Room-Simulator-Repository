#!/usr/bin/env bash
# Create a self-signed certificate for HTTPS on this server (needed for ElevenLabs voice input).
# Run once, then add Kestrel cert to appsettings and run with ServerHttps. Open https://YOUR_SERVER_IP:8443
set -e
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CERTS_DIR="${SCRIPT_DIR}/certs"
mkdir -p "$CERTS_DIR"
cd "$CERTS_DIR"
openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
  -keyout server.key -out server.crt \
  -subj "/CN=localhost/O=ER Simulator"
openssl pkcs12 -export -out server.pfx -inkey server.key -in server.crt -passout pass:
rm -f server.key
# Keep server.crt so you can copy it to your demo machine and install as trusted (removes "not secure" in browser)
echo "Created ${CERTS_DIR}/server.pfx and ${CERTS_DIR}/server.crt"
echo "Add to appsettings: Kestrel.Certificates.Default.Path = certs/server.pfx, Password = \"\""
echo "Run: dotnet run --launch-profile ServerHttps (or ./start-app.sh)"
echo ""
echo "To remove the 'not secure' warning on your demo PC: copy server.crt to that machine and install it as a Trusted Root (see docs/TRUST-CERT-FOR-DEMO.md)."
