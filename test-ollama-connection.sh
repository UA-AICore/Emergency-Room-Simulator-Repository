# Test script to verify Ollama is working
echo "Testing Ollama connection..."

# Test if Ollama is running
if curl -s http://127.0.0.1:11434/api/tags > /dev/null; then
    echo "✅ Ollama is running!"
    
    # Test a simple question
    echo "Asking Ollama: 'What is a heart attack?'"
    
    curl -X POST http://127.0.0.1:11434/api/generate \
      -H "Content-Type: application/json" \
      -d '{
        "model": "phi3:mini",
        "prompt": "What is a heart attack?",
        "stream": false
      }' | jq -r '.response'
    
    echo ""
    echo "🎉 If you see a medical response above, Ollama is working!"
else
    echo "❌ Ollama is not running."
    echo "   Your teammate needs to run: ollama serve"
fi
