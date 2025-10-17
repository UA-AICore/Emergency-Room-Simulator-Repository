#!/bin/bash

echo "🏥 ER Simulator - Ollama Integration Test"
echo "=========================================="
echo ""

# Check if Ollama is running
echo "Checking if Ollama is running on http://127.0.0.1:11434..."
if curl -s http://127.0.0.1:11434/api/tags > /dev/null; then
    echo "✅ Ollama is running!"
else
    echo "❌ Ollama is not running. Please start Ollama first."
    echo "   Run: ollama serve"
    exit 1
fi

echo ""
echo "Testing API endpoint..."
curl -X POST http://127.0.0.1:11434/api/generate \
  -H "Content-Type: application/json" \
  -d '{
    "model": "phi3:mini",
    "prompt": "Hello, are you working?",
    "stream": false
  }' | jq -r '.response'

echo ""
echo "🎉 If you see a response above, Ollama is working correctly!"
echo "   Your .NET app should now be able to connect to Ollama."
