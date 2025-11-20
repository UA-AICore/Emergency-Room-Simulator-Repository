# HeyGen Proxy Server

A simple local proxy server for debugging and developing with the HeyGen API.

## Setup

1. Install dependencies:
```bash
cd heygen-proxy
npm install
```

2. Start the proxy server:
```bash
npm start
```

The server will run on `http://localhost:3001`

## Configuration

Update your `appsettings.json` to use the proxy:

```json
{
  "HeyGen": {
    "ApiKey": "your-api-key",
    "ProxyUrl": "http://localhost:3001",
    "AvatarId": "your-avatar-id",
    "LookId": "your-look-id"
  }
}
```

## Features

- ✅ Logs all requests and responses for debugging
- ✅ Handles CORS issues
- ✅ Forwards all headers including API key
- ✅ Health check endpoint at `/health`

## Testing

Test the proxy:
```bash
curl http://localhost:3001/health
```

Test with HeyGen API:
```bash
curl -X POST http://localhost:3001/conversations/start \
  -H "X-Api-Key: your-api-key" \
  -H "Content-Type: application/json" \
  -d '{
    "avatar_id": "your-avatar-id",
    "look_id": "your-look-id",
    "language": "en-US",
    "voice_id": "your-voice-id",
    "initial_prompt": "Hello",
    "session_id": "test123"
  }'
```


