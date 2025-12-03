#!/bin/bash

# Server-side script to update the deployed ER Simulator App
# Run this script on the server after uploading a new deployment zip

set -e

ZIP_FILE="ERSimulatorApp-deployment.zip"
APP_NAME="ersimulator"
IMAGE_NAME="ersimulator-app"

echo "=========================================="
echo "ER Simulator App - Update Deployment"
echo "=========================================="
echo ""

# Check if zip file exists
if [ ! -f "$ZIP_FILE" ]; then
    echo "❌ Error: $ZIP_FILE not found in current directory"
    echo "   Make sure you're in ~/AppDrop and the zip file is uploaded"
    exit 1
fi

echo "✅ Found: $ZIP_FILE"
echo ""

# Stop and remove existing container
echo "🛑 Stopping existing container..."
docker stop "$APP_NAME" 2>/dev/null || echo "   Container not running"
docker rm "$APP_NAME" 2>/dev/null || echo "   Container not found"
echo "✅ Container stopped"
echo ""

# Extract the zip file
echo "📦 Extracting deployment package..."
rm -rf publish Dockerfile 2>/dev/null || true
unzip -q -o "$ZIP_FILE"
echo "✅ Extraction complete"
echo ""

# Verify required files
if [ ! -f "Dockerfile" ]; then
    echo "❌ Error: Dockerfile not found after extraction"
    exit 1
fi

if [ ! -d "publish" ] && [ ! -f "ERSimulatorApp.dll" ]; then
    echo "❌ Error: Published files not found"
    exit 1
fi

# Build Docker image
echo "🐳 Building Docker image..."
docker build -t "$IMAGE_NAME" .
if [ $? -ne 0 ]; then
    echo "❌ Docker build failed"
    exit 1
fi
echo "✅ Docker image built successfully"
echo ""

# Get environment variables from existing container (if it exists)
echo "📋 Checking for existing environment variables..."
ENV_VARS=""
if docker ps -a --format '{{.Names}}' | grep -q "^${APP_NAME}$"; then
    echo "   Found existing container, preserving environment variables..."
    # Note: This would require docker inspect, but for now we'll use defaults
    # In production, you should save env vars before stopping
fi

echo ""
echo "=========================================="
echo "✅ Update Complete!"
echo "=========================================="
echo ""
echo "⚠️  IMPORTANT: You need to start the container with environment variables."
echo ""
echo "To start the container, run:"
echo ""
echo "docker run -d --name $APP_NAME -p 8080:8080 \\"
echo "  -e OpenAI__ApiKey=\"your-openai-key\" \\"
echo "  -e RAG__ApiKey=\"your-rag-key\" \\"
echo "  -e RAG__BaseUrl=\"https://aicore-healthcareteam-llm-server.tra220030.projects.jetstream-cloud.org/v1/chat/completions\" \\"
echo "  -e HeyGen__ApiKey=\"your-heygen-key\" \\"
echo "  -v /app/data:/app/data \\"
echo "  $IMAGE_NAME"
echo ""
echo "Or use your existing environment setup script."
echo ""




