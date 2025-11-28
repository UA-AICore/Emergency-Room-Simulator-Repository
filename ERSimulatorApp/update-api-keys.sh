#!/bin/bash

# Script to update API keys on the deployed app
# Usage: ./update-api-keys.sh [OpenAI_Key] [RAG_Key] [RAG_BaseUrl] [HeyGen_Key]
# Or edit this file with your keys and run: ./update-api-keys.sh

cd "$(dirname "$0")"

SERVER_USER="exouser"
SERVER_HOST="149.165.154.35"
PASSWORD="COY BID MY PUG AND VAN DEAF FOGY GORY BORN JUDO"

# Get keys from arguments or use defaults from appsettings.json
if [ $# -ge 4 ]; then
    OPENAI_KEY="$1"
    RAG_KEY="$2"
    RAG_BASEURL="$3"
    HEYGEN_KEY="$4"
else
    # Read from appsettings.json
    OPENAI_KEY=$(grep -A 1 '"OpenAI"' appsettings.json | grep '"ApiKey"' | cut -d'"' -f4)
    RAG_KEY=$(grep -A 5 '"RAG"' appsettings.json | grep '"ApiKey"' | cut -d'"' -f4)
    RAG_BASEURL=$(grep -A 5 '"RAG"' appsettings.json | grep '"BaseUrl"' | cut -d'"' -f4)
    HEYGEN_KEY=$(grep -A 5 '"HeyGen"' appsettings.json | grep '"ApiKey"' | cut -d'"' -f4)
fi

echo "🔄 Updating API keys on deployed app..."
echo ""
echo "OpenAI Key: ${OPENAI_KEY:0:20}..."
echo "RAG Key: ${RAG_KEY:0:20}..."
echo "RAG BaseUrl: $RAG_BASEURL"
echo "HeyGen Key: ${HEYGEN_KEY:0:20}..."
echo ""

# Use expect to automate the update
expect << EOF
set timeout 60
spawn ssh -o StrictHostKeyChecking=accept-new ${SERVER_USER}@${SERVER_HOST} "cd ~/AppDrop && docker stop ersimulator && docker rm ersimulator && docker run -d --name ersimulator -p 8080:8080 -e OpenAI__ApiKey='${OPENAI_KEY}' -e RAG__ApiKey='${RAG_KEY}' -e RAG__BaseUrl='${RAG_BASEURL}' -e HeyGen__ApiKey='${HEYGEN_KEY}' -v /app/data:/app/data ersimulator-app && echo '✅ Container restarted' && sleep 2 && docker logs ersimulator --tail 5"
expect {
    "password:" {
        send "${PASSWORD}\r"
        exp_continue
    }
    eof {
        # Done
    }
}
EOF

if [ $? -eq 0 ]; then
    echo ""
    echo "✅ API keys updated successfully!"
    echo ""
    echo "To verify, run:"
    echo "  ssh ${SERVER_USER}@${SERVER_HOST} 'docker logs ersimulator'"
else
    echo ""
    echo "❌ Failed to update API keys"
    exit 1
fi


