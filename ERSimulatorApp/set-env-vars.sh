#!/bin/bash
# Environment Variables Setup Script for ER Simulator App
# Run this script on your production server to set required environment variables

echo "Setting up environment variables for ER Simulator App..."

# Critical API Keys
export OpenAI__ApiKey=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
export RAG__ApiKey=sk-xOHZ6CFRvTXaxmKLkrRYkxWAxVayywgH
export RAG__BaseUrl=https://aicore-healthcareteam-llm-server.tra220030.projects.jetstream-cloud.org/v1/chat/completions
export HeyGen__ApiKey=sk_V2_hgu_kVnKR3rBT13_7oT6Ptetv8L09piUrAuYtJLRoLEjORa6

# Path Base (update this if your app is hosted at a specific path)
# If app is at root (/), leave this commented out
# export ASPNETCORE_PATHBASE=/your-app-name

echo "Environment variables set successfully!"
echo ""
echo "To verify, run:"
echo "  echo \$OpenAI__ApiKey"
echo "  echo \$RAG__ApiKey"
echo "  echo \$RAG__BaseUrl"
echo "  echo \$HeyGen__ApiKey"
echo ""
echo "To make these persistent, add them to ~/.bashrc or /etc/environment"



