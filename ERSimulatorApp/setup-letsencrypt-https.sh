#!/usr/bin/env bash
# Obtain a trusted HTTPS certificate (Let's Encrypt) so the browser shows "secure" with no warning.
# Requires: a domain name that points to this server's public IP (e.g. er-sim.yourdomain.com → 149.165.168.99).
# Port 80 must be free on this server for the duration of cert issuance (Certbot uses it temporarily).
set -e
if [ -z "$1" ]; then
  echo "Usage: $0 <your-domain>"
  echo "Example: $0 er-sim.example.com"
  echo ""
  echo "Prerequisites:"
  echo "  - DNS: Your domain must point to this server's public IP."
  echo "  - Port 80: Must be free (stop the .NET app or any other service on 80)."
  echo "  - Certbot: Install with e.g. sudo apt install certbot (Ubuntu/Debian)."
  exit 1
fi
DOMAIN="$1"
# Install certbot if missing (Debian/Ubuntu)
if ! command -v certbot &>/dev/null; then
  echo "Certbot not found. Install it, e.g.:"
  echo "  sudo apt update && sudo apt install -y certbot"
  exit 1
fi
echo "Obtaining certificate for: $DOMAIN"
echo "Port 80 will be used temporarily. Press Enter to continue (or Ctrl+C to cancel)."
read -r
sudo certbot certonly --standalone -d "$DOMAIN" --non-interactive --agree-tos --register-unsafely-without-email || true
if [ ! -f "/etc/letsencrypt/live/$DOMAIN/fullchain.pem" ] || [ ! -f "/etc/letsencrypt/live/$DOMAIN/privkey.pem" ]; then
  echo "Certbot failed or cert not found. Fix any errors above and try again."
  exit 1
fi
echo ""
echo "Certificate installed. Add this to your appsettings.json (Kestrel section):"
echo ""
echo "\"Kestrel\": {"
echo "  \"Certificates\": {"
echo "    \"Default\": {"
echo "      \"Path\": \"/etc/letsencrypt/live/$DOMAIN/fullchain.pem\","
echo "      \"KeyPath\": \"/etc/letsencrypt/live/$DOMAIN/privkey.pem\""
echo "    }"
echo "  }"
echo "}"
echo ""
echo "Then run: dotnet run --launch-profile ServerHttps"
echo "Open https://$DOMAIN:8443 in your browser — it should show as secure (no warning)."
echo ""
echo "Renewal: Certbot renews automatically (cron). After renewal, restart the app to load the new cert."
