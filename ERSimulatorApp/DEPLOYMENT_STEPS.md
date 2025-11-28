# Deployment Steps

## Step 1: Upload the Application

Run this command from the `ERSimulatorApp` directory:

```bash
scp ERSimulatorApp-deployment.zip exouser@149.165.154.35:~/AppDrop
```

**Note:** You'll be prompted for the password for `exouser`. Enter it when prompted.

## Step 2: SSH into the Server

After uploading, SSH into the server to set up the application:

```bash
ssh exouser@149.165.154.35
```

## Step 3: Extract and Set Up the Application

Once on the server, run these commands:

```bash
# Navigate to AppDrop
cd ~/AppDrop

# Extract the zip file
unzip ERSimulatorApp-deployment.zip

# The extraction should create:
# - publish/ folder (application files)
# - Dockerfile (container configuration)
```

## Step 4: Configure Environment Variables

Set the required environment variables. You can either:

### Option A: Use the helper script (if you uploaded it)
```bash
# Make it executable
chmod +x set-env-vars.sh

# Edit it with your actual API keys
nano set-env-vars.sh

# Run it
./set-env-vars.sh
```

### Option B: Set environment variables manually

Create a `.env` file or set them in your deployment system:

```bash
# Critical API Keys
export OpenAI__ApiKey="your-openai-key"
export RAG__ApiKey="your-rag-key"
export RAG__BaseUrl="https://aicore-healthcareteam-llm-server.tra220030.projects.jetstream-cloud.org/v1/chat/completions"
export HeyGen__ApiKey="your-heygen-key"

# Path Base (if app is hosted at /your-app-name/)
export ASPNETCORE_PATHBASE="/your-app-name"
```

**Important:** Replace the placeholder values with your actual API keys!

## Step 5: Build and Run with Docker

```bash
# Build the Docker image
docker build -t ersimulator-app .

# Run the container with environment variables
docker run -d \
  --name ersimulator \
  -p 8080:8080 \
  -e OpenAI__ApiKey="your-openai-key" \
  -e RAG__ApiKey="your-rag-key" \
  -e RAG__BaseUrl="https://aicore-healthcareteam-llm-server.tra220030.projects.jetstream-cloud.org/v1/chat/completions" \
  -e HeyGen__ApiKey="your-heygen-key" \
  -v /app/data:/app/data \
  ersimulator-app
```

Or if using Docker Compose, create a `docker-compose.yml` based on `docker-compose.example.yml`.

## Step 6: Verify Deployment

Check that the application is running:

```bash
# Check container status
docker ps

# Check logs
docker logs ersimulator

# Test health endpoint (if accessible)
curl http://localhost:8080/api/health
```

## Troubleshooting

### If SCP asks for password:
- Enter the password for the `exouser` account when prompted
- If you don't have the password, contact your server administrator

### If you need to set up SSH keys:
```bash
# Generate SSH key (if you don't have one)
ssh-keygen -t ed25519 -C "your_email@example.com"

# Copy public key to server
ssh-copy-id exouser@149.165.154.35
```

### If the app doesn't start:
- Check Docker logs: `docker logs ersimulator`
- Verify environment variables are set correctly
- Ensure port 8080 is not already in use
- Check that the `/app/data` directory has write permissions

## Files Reference

- **ERSimulatorApp-deployment.zip** - Contains `publish/` folder and `Dockerfile`
- **SERVER_CONFIGURATION.md** - Detailed configuration guide
- **ENV_VARS_QUICK_REFERENCE.txt** - Quick reference for environment variables
- **docker-compose.example.yml** - Example Docker Compose configuration


