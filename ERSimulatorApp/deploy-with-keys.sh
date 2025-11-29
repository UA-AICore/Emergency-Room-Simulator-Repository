#!/bin/bash

# Deployment script with API keys from appsettings.json
# Run this on the server after uploading ERSimulatorApp-deployment.zip

set -e

ZIP_FILE="ERSimulatorApp-deployment.zip"
APP_NAME="ersimulator"
IMAGE_NAME="ersimulator-app"

# API Keys from appsettings.json
OPENAI_KEY="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"
RAG_KEY="sk-xOHZ6CFRvTXaxmKLkrRYkxWAxVayywgH"
RAG_BASEURL="https://aicore-healthcareteam-llm-server.tra220030.projects.jetstream-cloud.org/v1/chat/completions"
HEYGEN_KEY="sk_V2_hgu_kVnKR3rBT13_7oT6Ptetv8L09piUrAuYtJLRoLEjORa6"

echo "=========================================="
echo "ER Simulator App - Deploy with API Keys"
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
rm -rf publish Dockerfile ERSimulatorApp.dll 2>/dev/null || true
unzip -q -o "$ZIP_FILE"
echo "✅ Extraction complete"
echo ""

# Verify required files
if [ ! -f "Dockerfile" ]; then
    echo "❌ Error: Dockerfile not found after extraction"
    exit 1
fi

if [ ! -f "ERSimulatorApp.dll" ]; then
    echo "❌ Error: ERSimulatorApp.dll not found after extraction"
    exit 1
fi

# Create publish directory structure expected by Dockerfile
echo "📁 Organizing files for Docker build..."
mkdir -p publish
mv ERSimulatorApp.dll publish/ 2>/dev/null || true
mv ERSimulatorApp.deps.json publish/ 2>/dev/null || true
mv ERSimulatorApp.pdb publish/ 2>/dev/null || true
mv ERSimulatorApp.runtimeconfig.json publish/ 2>/dev/null || true
mv ERSimulatorApp.staticwebassets.endpoints.json publish/ 2>/dev/null || true
mv ERSimulatorApp.styles.css publish/ 2>/dev/null || true
mv ERSimulatorApp publish/ 2>/dev/null || true
mv appsettings.json publish/ 2>/dev/null || true
mv appsettings.Development.json publish/ 2>/dev/null || true
mv web.config publish/ 2>/dev/null || true
mv wwwroot publish/ 2>/dev/null || true
mv *.dll publish/ 2>/dev/null || true
echo "✅ Files organized"
echo ""

# Build Docker image
echo "🐳 Building Docker image..."
docker build -t "$IMAGE_NAME" .
if [ $? -ne 0 ]; then
    echo "❌ Docker build failed"
    exit 1
fi
echo "✅ Docker image built successfully"
echo ""

# Start container with API keys
echo "🚀 Starting container with updated API keys..."
docker run -d --name "$APP_NAME" -p 8080:8080 \
  -e OpenAI__ApiKey="$OPENAI_KEY" \
  -e RAG__ApiKey="$RAG_KEY" \
  -e RAG__BaseUrl="$RAG_BASEURL" \
  -e HeyGen__ApiKey="$HEYGEN_KEY" \
  -v /app/data:/app/data \
  "$IMAGE_NAME"

if [ $? -eq 0 ]; then
    echo "✅ Container started successfully"
    echo ""
    echo "Waiting 5 seconds for app to start..."
    sleep 5
    echo ""
    echo "📊 Container status:"
    docker ps | grep "$APP_NAME"
    echo ""
    echo "📋 Recent logs:"
    docker logs "$APP_NAME" --tail 20
    echo ""
    echo "✅ Deployment complete!"
    echo ""
    echo "Test the app:"
    echo "  curl http://localhost:8080/api/health"
    echo "  or visit: http://149.165.154.35:8080"
else
    echo "❌ Failed to start container"
    exit 1
fi

