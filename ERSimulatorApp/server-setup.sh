#!/bin/bash

# Server-side setup script
# Run this script on the server after uploading the zip file

set -e

ZIP_FILE="ERSimulatorApp-deployment.zip"
APP_NAME="ersimulator"

echo "=========================================="
echo "ER Simulator App - Server Setup"
echo "=========================================="
echo ""

# Check if zip file exists
if [ ! -f "$ZIP_FILE" ]; then
    echo "❌ Error: $ZIP_FILE not found in current directory"
    echo "   Make sure you're in ~/AppDrop"
    exit 1
fi

echo "✅ Found: $ZIP_FILE"
echo ""

# Extract the zip file
echo "📦 Extracting deployment package..."
unzip -q -o "$ZIP_FILE"
echo "✅ Extraction complete"
echo ""

# Check if Dockerfile exists
if [ ! -f "Dockerfile" ]; then
    echo "❌ Error: Dockerfile not found after extraction"
    exit 1
fi

# Check if publish directory exists
if [ ! -d "publish" ]; then
    echo "❌ Error: publish directory not found after extraction"
    exit 1
fi

echo "🐳 Building Docker image..."
docker build -t ersimulator-app .

if [ $? -eq 0 ]; then
    echo "✅ Docker image built successfully"
else
    echo "❌ Docker build failed"
    exit 1
fi

echo ""
echo "=========================================="
echo "Docker Image Ready!"
echo "=========================================="
echo ""
echo "To run the container, use:"
echo ""
echo "docker run -d --name $APP_NAME -p 8080:8080 \\"
echo "  -e OpenAI__ApiKey=\"your-openai-key\" \\"
echo "  -e RAG__ApiKey=\"your-rag-key\" \\"
echo "  -e RAG__BaseUrl=\"https://aicore-healthcareteam-llm-server.tra220030.projects.jetstream-cloud.org/v1/chat/completions\" \\"
echo "  -e HeyGen__ApiKey=\"your-heygen-key\" \\"
echo "  -v /app/data:/app/data \\"
echo "  ersimulator-app"
echo ""
echo "⚠️  Remember to replace the API keys with your actual keys!"
echo ""


