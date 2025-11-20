#!/usr/bin/env python3
"""
HeyGen Proxy Server (Python version)

This proxy server helps with local development by:
1. Logging all requests/responses for debugging
2. Adding custom headers if needed
3. Handling CORS issues
4. Providing a local endpoint for testing

Usage:
    python3 proxy_server.py

Then configure your app to use: http://localhost:3001
"""

import http.server
import socketserver
import urllib.request
import urllib.parse
import json
import sys
from datetime import datetime

PORT = 3001
HEYGEN_API_URL = 'https://api.heygen.com/v1'

class ProxyHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        """Override to add timestamp"""
        timestamp = datetime.now().strftime('%Y-%m-%d %H:%M:%S')
        print(f"[{timestamp}] {format % args}")
    
    def do_GET(self):
        """Handle GET requests"""
        if self.path == '/health':
            self.send_health_response()
            return
        
        self.proxy_request()
    
    def do_POST(self):
        """Handle POST requests"""
        self.proxy_request()
    
    def do_PUT(self):
        """Handle PUT requests"""
        self.proxy_request()
    
    def do_DELETE(self):
        """Handle DELETE requests"""
        self.proxy_request()
    
    def send_health_response(self):
        """Send health check response"""
        response = {
            'status': 'ok',
            'proxy': 'HeyGen API Proxy',
            'target': HEYGEN_API_URL,
            'timestamp': datetime.now().isoformat()
        }
        self.send_json_response(response, 200)
    
    def proxy_request(self):
        """Proxy the request to HeyGen API"""
        try:
            # Get request body if present
            content_length = int(self.headers.get('Content-Length', 0))
            body = self.rfile.read(content_length) if content_length > 0 else None
            
            # Log request
            print(f"\n{'='*60}")
            print(f"Request: {self.command} {self.path}")
            print(f"Headers: {dict(self.headers)}")
            if body:
                try:
                    body_json = json.loads(body.decode('utf-8'))
                    print(f"Body: {json.dumps(body_json, indent=2)}")
                except:
                    print(f"Body: {body.decode('utf-8', errors='ignore')[:500]}")
            print(f"{'='*60}\n")
            
            # Build target URL
            target_path = self.path
            if not target_path.startswith('/'):
                target_path = '/' + target_path
            target_url = HEYGEN_API_URL + target_path
            
            # Create request to HeyGen API
            req = urllib.request.Request(target_url, data=body)
            
            # Copy headers (except Host)
            for header, value in self.headers.items():
                if header.lower() != 'host':
                    req.add_header(header, value)
            
            # Make request
            print(f"Proxying to: {target_url}")
            try:
                with urllib.request.urlopen(req, timeout=120) as response:
                    response_data = response.read()
                    status_code = response.getcode()
                    
                    # Log response
                    print(f"\n{'='*60}")
                    print(f"Response Status: {status_code}")
                    try:
                        response_json = json.loads(response_data.decode('utf-8'))
                        print(f"Response Body: {json.dumps(response_json, indent=2)}")
                    except:
                        print(f"Response Body: {response_data.decode('utf-8', errors='ignore')[:500]}")
                    print(f"{'='*60}\n")
                    
                    # Send response back to client
                    self.send_response(status_code)
                    
                    # Copy response headers
                    for header, value in response.headers.items():
                        if header.lower() not in ['connection', 'transfer-encoding']:
                            self.send_header(header, value)
                    
                    self.end_headers()
                    self.wfile.write(response_data)
                    
            except urllib.error.HTTPError as e:
                error_body = e.read()
                print(f"\n{'='*60}")
                print(f"Error Response Status: {e.code}")
                try:
                    error_json = json.loads(error_body.decode('utf-8'))
                    print(f"Error Body: {json.dumps(error_json, indent=2)}")
                except:
                    print(f"Error Body: {error_body.decode('utf-8', errors='ignore')[:500]}")
                print(f"{'='*60}\n")
                
                self.send_response(e.code)
                self.end_headers()
                self.wfile.write(error_body)
                
        except Exception as e:
            print(f"Proxy error: {str(e)}")
            error_response = {
                'error': 'Proxy error',
                'message': str(e)
            }
            self.send_json_response(error_response, 500)
    
    def send_json_response(self, data, status_code):
        """Send JSON response"""
        json_data = json.dumps(data).encode('utf-8')
        self.send_response(status_code)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(json_data)))
        self.end_headers()
        self.wfile.write(json_data)

def main():
    """Start the proxy server"""
    with socketserver.TCPServer(("", PORT), ProxyHandler) as httpd:
        print(f"\n{'='*60}")
        print(f"🚀 HeyGen Proxy Server running on http://localhost:{PORT}")
        print(f"📡 Proxying to: {HEYGEN_API_URL}")
        print(f"\nTo use this proxy, update your appsettings.json:")
        print(f'"HeyGen": {{')
        print(f'  "ProxyUrl": "http://localhost:{PORT}"')
        print(f'  ...')
        print(f'}}')
        print(f"{'='*60}\n")
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\n\nShutting down proxy server...")
            sys.exit(0)

if __name__ == '__main__':
    main()

