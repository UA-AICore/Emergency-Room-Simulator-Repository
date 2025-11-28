#!/bin/bash

# Setup SSH keys for passwordless deployment

SERVER_USER="exouser"
SERVER_HOST="149.165.154.35"
SSH_KEY_PATH="$HOME/.ssh/id_ed25519_ersimulator"

echo "=========================================="
echo "SSH Key Setup for Deployment"
echo "=========================================="
echo ""

# Check if key already exists
if [ -f "$SSH_KEY_PATH" ]; then
    echo "✅ SSH key already exists: $SSH_KEY_PATH"
    echo ""
    read -p "Do you want to use the existing key? (y/n) " -n 1 -r
    echo ""
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        echo "Generating new SSH key..."
        ssh-keygen -t ed25519 -f "$SSH_KEY_PATH" -N "" -C "ersimulator-deployment"
    fi
else
    echo "Generating new SSH key..."
    ssh-keygen -t ed25519 -f "$SSH_KEY_PATH" -N "" -C "ersimulator-deployment"
fi

echo ""
echo "📋 Public key (copy this):"
echo "=========================================="
cat "${SSH_KEY_PATH}.pub"
echo "=========================================="
echo ""

echo "Next steps:"
echo "1. SSH into the server:"
echo "   ssh ${SERVER_USER}@${SERVER_HOST}"
echo ""
echo "2. Add this public key to ~/.ssh/authorized_keys:"
echo "   mkdir -p ~/.ssh"
echo "   chmod 700 ~/.ssh"
echo "   echo '$(cat "${SSH_KEY_PATH}.pub")' >> ~/.ssh/authorized_keys"
echo "   chmod 600 ~/.ssh/authorized_keys"
echo ""
echo "3. Or run this command (you'll need to enter password once):"
echo "   ssh-copy-id -i ${SSH_KEY_PATH}.pub ${SERVER_USER}@${SERVER_HOST}"
echo ""
echo "4. Then update deploy.sh to use this key:"
echo "   Edit deploy.sh and change the scp command to:"
echo "   scp -i $SSH_KEY_PATH ..."
echo ""


