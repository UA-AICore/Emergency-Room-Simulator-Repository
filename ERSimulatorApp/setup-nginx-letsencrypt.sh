#!/usr/bin/env bash
# One-time setup: get a Let's Encrypt cert and put nginx in front of the app on port 443.
# Result: open https://YOUR_DUCKDNS_DOMAIN and the browser shows secure with no "not secure" warning.
#
# Your DuckDNS domain must point to this server's public IP (e.g. 149.165.168.99). Update the IP in DuckDNS, then run this script.
#
# Usage: sudo ./setup-nginx-letsencrypt.sh YOUR_DUCKDNS_DOMAIN
# Example: sudo ./setup-nginx-letsencrypt.sh ameya-temp.duckdns.org
#
# After this: run the app with ./start-app.sh Server and open https://YOUR_DUCKDNS_DOMAIN
set -e
if [ -z "$1" ]; then
  echo "Usage: sudo $0 <your-duckdns-domain>"
  echo "Example: sudo $0 ameya-temp.duckdns.org"
  echo ""
  echo "Ensure your DuckDNS domain points to this server's IP (e.g. 149.165.168.99), then run this script."
  exit 1
fi
DOMAIN="$1"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NGINX_CONF_SOURCE="${SCRIPT_DIR}/nginx-er-simulator.conf"
NGINX_AVAILABLE="/etc/nginx/sites-available/er-simulator"
NGINX_ENABLED="/etc/nginx/sites-enabled/er-simulator"

if [ "$(id -u)" -ne 0 ]; then
  echo "Run this script with sudo so it can install packages and write nginx config."
  exit 1
fi

echo "=== Installing nginx and certbot if needed ==="
apt-get update -qq
apt-get install -y -qq nginx certbot 2>/dev/null || true
if ! command -v certbot &>/dev/null; then
  echo "Could not install certbot. Install it manually: sudo apt install certbot"
  exit 1
fi

echo "=== Stopping nginx so certbot can use port 80 ==="
systemctl stop nginx 2>/dev/null || true

echo "=== Getting Let's Encrypt certificate for $DOMAIN ==="
echo "Ensure $DOMAIN points to this server's public IP (e.g. 149.165.168.99). Press Enter to continue."
read -r
certbot certonly --standalone -d "$DOMAIN" --non-interactive --agree-tos --register-unsafely-without-email || true
if [ ! -f "/etc/letsencrypt/live/$DOMAIN/fullchain.pem" ] || [ ! -f "/etc/letsencrypt/live/$DOMAIN/privkey.pem" ]; then
  echo "Certbot failed or cert not found. Fix DNS (point $DOMAIN to this server IP) and try again."
  systemctl start nginx 2>/dev/null || true
  exit 1
fi

echo "=== Installing nginx config for $DOMAIN ==="
sed "s/YOUR_DOMAIN/$DOMAIN/g" "$NGINX_CONF_SOURCE" > "$NGINX_AVAILABLE"
rm -f /etc/nginx/sites-enabled/default
ln -sf "$NGINX_AVAILABLE" "$NGINX_ENABLED"
nginx -t
systemctl enable nginx
systemctl start nginx

echo ""
echo "=== Done. Nginx is serving HTTPS for $DOMAIN and proxying to the app on port 8081. ==="
echo ""
echo "Next steps:"
echo "  1. Start the app (HTTP-only so nginx can proxy to it):"
echo "     cd $(dirname "$SCRIPT_DIR") && ./start-app.sh Server"
echo "  2. Open in the browser: https://$DOMAIN"
echo "     No 'not secure' warning; voice input works."
echo ""
echo "Renewal: Certbot renews certs automatically. After renewal, run: sudo systemctl reload nginx"
