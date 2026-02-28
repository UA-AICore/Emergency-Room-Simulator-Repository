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
rm -f server.key server.crt
echo "Created ${CERTS_DIR}/server.pfx"
echo "Add to appsettings: Kestrel.Certificates.Default.Path = certs/server.pfx, Password = \"\""
echo "Run: dotnet run --launch-profile ServerHttps"
echo "Open https://149.165.168.99:8443 and accept the browser warning once for voice input."
