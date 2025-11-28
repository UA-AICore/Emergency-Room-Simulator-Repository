#!/bin/bash

echo "🔍 Checking RAG Server Status..."
echo ""

# Check local RAG server
echo "1️⃣  Local RAG Server (http://127.0.0.1:5001):"
if curl -s -m 5 http://127.0.0.1:5001/health > /dev/null 2>&1; then
    echo "   ✅ ONLINE"
    curl -s http://127.0.0.1:5001/health | python3 -m json.tool 2>/dev/null || echo "   Response: $(curl -s http://127.0.0.1:5001/health)"
else
    echo "   ❌ OFFLINE"
    echo "   💡 Start it with: cd rag-local && source .venv/bin/activate && uvicorn app.server:app --host 127.0.0.1 --port 5001 --reload"
fi

echo ""

# Check ngrok tunnel
echo "2️⃣  ngrok Tunnel (https://unchid-promonopoly-tiera.ngrok-free.dev):"
if curl -s -m 5 -H "ngrok-skip-browser-warning: true" "https://unchid-promonopoly-tiera.ngrok-free.dev/health" > /dev/null 2>&1; then
    echo "   ✅ ONLINE"
    curl -s -H "ngrok-skip-browser-warning: true" "https://unchid-promonopoly-tiera.ngrok-free.dev/health" | python3 -m json.tool 2>/dev/null || echo "   Response: $(curl -s -H "ngrok-skip-browser-warning: true" "https://unchid-promonopoly-tiera.ngrok-free.dev/health")"
else
    echo "   ❌ OFFLINE"
    echo "   💡 Start ngrok with: ngrok http 5001"
fi

echo ""

# Check running processes
echo "3️⃣  Running Processes:"
if pgrep -f "uvicorn.*server:app" > /dev/null; then
    echo "   ✅ RAG server process found"
else
    echo "   ❌ No RAG server process found"
fi

if pgrep -f "ngrok" > /dev/null; then
    echo "   ✅ ngrok process found"
else
    echo "   ❌ No ngrok process found"
fi

echo ""
echo "📝 Summary:"
echo "   Your app is configured to use: https://unchid-promonopoly-tiera.ngrok-free.dev"
echo "   Make sure both the RAG server AND ngrok tunnel are running!"






