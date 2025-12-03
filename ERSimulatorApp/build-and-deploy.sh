#!/bin/bash

# Build and Deploy Script for ER Simulator App
# This script builds the .NET app, creates a deployment package, and optionally uploads it

set -e

cd "$(dirname "$0")"

SERVER_USER="exouser"
SERVER_HOST="149.165.154.35"
ZIP_FILE="ERSimulatorApp-deployment.zip"
PUBLISH_DIR="publish"
BUILD_CONFIG="Release"

echo "🚀 ER Simulator App - Build and Deploy"
echo "======================================"
echo ""

# Step 1: Clean previous builds
echo "🧹 Cleaning previous builds..."
if [ -d "$PUBLISH_DIR" ]; then
    rm -rf "$PUBLISH_DIR"
fi
if [ -f "$ZIP_FILE" ]; then
    rm -f "$ZIP_FILE"
fi
echo "✅ Clean complete"
echo ""

# Step 2: Restore dependencies
echo "📦 Restoring .NET dependencies..."
dotnet restore
if [ $? -ne 0 ]; then
    echo "❌ Restore failed"
    exit 1
fi
echo "✅ Restore complete"
echo ""

# Step 3: Build and publish
echo "🔨 Building and publishing application..."
dotnet publish -c "$BUILD_CONFIG" -o "$PUBLISH_DIR" --no-self-contained
if [ $? -ne 0 ]; then
    echo "❌ Build failed"
    exit 1
fi
echo "✅ Build complete"
echo ""

# Step 4: Verify publish directory
if [ ! -d "$PUBLISH_DIR" ]; then
    echo "❌ Error: Publish directory not created"
    exit 1
fi

if [ ! -f "$PUBLISH_DIR/ERSimulatorApp.dll" ]; then
    echo "❌ Error: ERSimulatorApp.dll not found in publish directory"
    exit 1
fi

# Step 5: Copy Dockerfile to publish directory
echo "🐳 Copying Dockerfile..."
if [ ! -f "Dockerfile" ]; then
    echo "❌ Error: Dockerfile not found"
    exit 1
fi
cp Dockerfile "$PUBLISH_DIR/"
echo "✅ Dockerfile copied"
echo ""

# Step 6: Create deployment zip
echo "📦 Creating deployment package..."
cd "$PUBLISH_DIR"
zip -r "../$ZIP_FILE" .
cd ..
echo "✅ Package created: $ZIP_FILE ($(du -h "$ZIP_FILE" | cut -f1))"
echo ""

# Step 7: Ask if user wants to upload
echo "======================================"
echo "✅ Build Complete!"
echo "======================================"
echo ""
echo "Package: $ZIP_FILE"
echo "Size: $(du -h "$ZIP_FILE" | cut -f1)"
echo ""
read -p "Upload to server now? (y/n): " -n 1 -r
echo ""

if [[ $REPLY =~ ^[Yy]$ ]]; then
    echo "📤 Uploading to ${SERVER_USER}@${SERVER_HOST}..."
    scp -o StrictHostKeyChecking=accept-new "$ZIP_FILE" "${SERVER_USER}@${SERVER_HOST}:~/AppDrop"
    
    if [ $? -eq 0 ]; then
        echo ""
        echo "✅ Upload successful!"
        echo ""
        echo "Next steps:"
        echo "1. SSH to server: ssh ${SERVER_USER}@${SERVER_HOST}"
        echo "2. Run the deployment commands (see below)"
        echo ""
        echo "Quick deploy commands:"
        echo "  cd ~/AppDrop"
        echo "  unzip -o $ZIP_FILE"
        echo "  docker stop ersimulator || true"
        echo "  docker rm ersimulator || true"
        echo "  docker build -t ersimulator-app ."
        echo "  docker run -d --name ersimulator -p 8080:8080 \\"
        echo "    -e OpenAI__ApiKey=\"your-key\" \\"
        echo "    -e RAG__ApiKey=\"your-key\" \\"
        echo "    -e RAG__BaseUrl=\"https://aicore-healthcareteam-llm-server.tra220030.projects.jetstream-cloud.org/v1/chat/completions\" \\"
        echo "    -e HeyGen__ApiKey=\"your-key\" \\"
        echo "    -v /app/data:/app/data \\"
        echo "    ersimulator-app"
    else
        echo ""
        echo "❌ Upload failed. You can upload manually later:"
        echo "   scp $ZIP_FILE ${SERVER_USER}@${SERVER_HOST}:~/AppDrop"
    fi
else
    echo ""
    echo "📦 Package ready for manual upload:"
    echo "   scp $ZIP_FILE ${SERVER_USER}@${SERVER_HOST}:~/AppDrop"
fi

echo ""





