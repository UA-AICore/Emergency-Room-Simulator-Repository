#!/bin/bash

echo "🏥 ER Simulator - Ollama Connection Test"
echo "========================================"
echo ""

# Check if Ollama is running
echo "Checking if Ollama is running on http://127.0.0.1:11434..."
if curl -s http://127.0.0.1:11434/api/tags > /dev/null; then
    echo "✅ Ollama is running!"
    echo ""
    
    # Test a medical question
    echo "Testing with a medical question: 'What is CPR?'"
    echo ""
    
    response=$(curl -s -X POST http://127.0.0.1:11434/api/generate \
      -H "Content-Type: application/json" \
      -d '{
        "model": "phi3:mini",
        "prompt": "What is CPR?",
        "stream": false
      }')
    
    echo "AI Response:"
    echo "$response" | jq -r '.response'
    echo ""
    echo "🎉 Ollama is working! Your .NET app can now connect to it."
    echo ""
    echo "Next steps:"
    echo "1. Run your .NET app: dotnet run"
    echo "2. Open browser to: http://localhost:5120"
    echo "3. Start chatting with the AI!"
    
else
    echo "❌ Ollama is not running."
    echo ""
    echo "Your teammate needs to start Ollama:"
    echo "1. Open WSL Ubuntu"
    echo "2. Run: ollama serve"
    echo "3. Wait for 'Listening on 127.0.0.1:11434' message"
    echo ""
    echo "Then run this test again."
fi