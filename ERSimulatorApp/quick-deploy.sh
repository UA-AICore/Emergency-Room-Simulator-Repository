#!/bin/bash

# Quick Deployment Script
# Run this script and enter your password when prompted

cd "$(dirname "$0")"

SERVER_USER="exouser"
SERVER_HOST="149.165.154.35"
ZIP_FILE="ERSimulatorApp-deployment.zip"

echo "🚀 ER Simulator App - Quick Deploy"
echo "=================================="
echo ""

if [ ! -f "$ZIP_FILE" ]; then
    echo "❌ Error: $ZIP_FILE not found"
    exit 1
fi

echo "📦 Package: $ZIP_FILE ($(du -h "$ZIP_FILE" | cut -f1))"
echo "🎯 Server: ${SERVER_USER}@${SERVER_HOST}"
echo ""
echo "📤 Uploading... (enter password when prompted)"
echo ""

# Upload the file
scp -o StrictHostKeyChecking=accept-new "$ZIP_FILE" "${SERVER_USER}@${SERVER_HOST}:~/AppDrop"

if [ $? -eq 0 ]; then
    echo ""
    echo "✅ Upload successful!"
    echo ""
    echo "Next steps:"
    echo "1. SSH to server: ssh ${SERVER_USER}@${SERVER_HOST}"
    echo "2. See DEPLOYMENT_STEPS.md for complete setup instructions"
    echo ""
else
    echo ""
    echo "❌ Upload failed. Please check your password and try again."
    exit 1
fi


