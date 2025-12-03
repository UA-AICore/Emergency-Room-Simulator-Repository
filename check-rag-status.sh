#!/bin/bash

echo "🔍 Checking RAG Server Status..."
echo ""

# Check remote RAG server (configured in appsettings.json)
echo "1️⃣  Remote RAG Server:"
RAG_URL=$(grep -A 3 '"RAG":' ERSimulatorApp/appsettings.json 2>/dev/null | grep '"BaseUrl"' | cut -d'"' -f4 || echo "")
if [ -z "$RAG_URL" ]; then
    echo "   ⚠️  RAG URL not found in appsettings.json"
else
    echo "   📍 URL: $RAG_URL"
    if curl -s -m 5 "$RAG_URL" > /dev/null 2>&1; then
        echo "   ✅ ONLINE"
    else
        echo "   ❌ OFFLINE or unreachable"
    fi
fi

echo ""
echo "📝 Note:"
echo "   The app uses a remote RAG server configured in appsettings.json"
echo "   Source inference is handled automatically from remote responses"






