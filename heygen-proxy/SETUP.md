# HeyGen Proxy Setup Guide

## Quick Start

1. **Install Node.js dependencies:**
```bash
cd heygen-proxy
npm install
```

2. **Start the proxy server:**
```bash
npm start
```

The proxy will run on `http://localhost:3001`

3. **Update your appsettings.json:**
```json
{
  "HeyGen": {
    "ApiKey": "sk_V2_hgu_kVnKR3rBT13_7oT6Ptetv8L09piUrAuYtJLRoLEjORa6",
    "ProxyUrl": "http://localhost:3001",
    "AvatarId": "26a6ac7b34774cd3bfbebc5612cd2dba",
    "LookId": "Dexter_Casual_Front_public"
  }
}
```

4. **Restart your web app** to pick up the configuration changes.

## What This Proxy Does

- ✅ Routes all HeyGen API calls through a local proxy
- ✅ Logs all requests and responses for debugging
- ✅ Helps identify API endpoint issues
- ✅ Can add custom headers if needed
- ✅ Provides a health check endpoint

## Testing

### Test the proxy health:
```bash
curl http://localhost:3001/health
```

### Test HeyGen API through proxy:
```bash
curl -X POST http://localhost:3001/conversations/start \
  -H "X-Api-Key: sk_V2_hgu_kVnKR3rBT13_7oT6Ptetv8L09piUrAuYtJLRoLEjORa6" \
  -H "Content-Type: application/json" \
  -d '{
    "avatar_id": "26a6ac7b34774cd3bfbebc5612cd2dba",
    "look_id": "Dexter_Casual_Front_public",
    "language": "en-US",
    "voice_id": "medical_instructor",
    "initial_prompt": "Hello, welcome to the medical training session.",
    "session_id": "test_session_123"
  }'
```

## Troubleshooting

### Proxy not starting?
- Make sure port 3001 is not in use: `lsof -ti tcp:3001`
- Check Node.js is installed: `node --version`
- Install dependencies: `npm install`

### API calls failing?
- Check the proxy logs for error messages
- Verify your API key is correct
- Ensure HeyGen API endpoint format is correct
- Check HeyGen API documentation for latest endpoint formats

### Want to disable proxy?
Remove `ProxyUrl` from appsettings.json or set it to empty string.





