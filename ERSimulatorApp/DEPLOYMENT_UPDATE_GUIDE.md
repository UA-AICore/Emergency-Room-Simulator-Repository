# 🚀 Quick Guide: Updating the Deployed Web App

This guide shows you how to deploy your code changes to the production server.

## Prerequisites

- Access to the server: `exouser@149.165.154.35`
- SSH access configured (or password)
- Docker installed on the server

---

## Method 1: Automated Build and Deploy (Recommended)

### Step 1: Build and Create Deployment Package

From the `ERSimulatorApp` directory, run:

```bash
cd "/Users/hginman/ER Simulator Web App/ERSimulatorApp"
./build-and-deploy.sh
```

This script will:
- ✅ Clean previous builds
- ✅ Restore .NET dependencies
- ✅ Build and publish the application
- ✅ Create `ERSimulatorApp-deployment.zip`
- ✅ Optionally upload to the server

### Step 2: Deploy on Server

If you chose to upload automatically, or if you upload manually:

```bash
# SSH to the server
ssh exouser@149.165.154.35

# Navigate to AppDrop directory
cd ~/AppDrop

# Run the update script (if you uploaded it)
chmod +x update-deployed-app.sh
./update-deployed-app.sh
```

Or manually:

```bash
# Extract the new package
unzip -o ERSimulatorApp-deployment.zip

# Stop and remove old container
docker stop ersimulator
docker rm ersimulator

# Build new image
docker build -t ersimulator-app .

# Start new container with environment variables
docker run -d --name ersimulator -p 8080:8080 \
  -e OpenAI__ApiKey="your-openai-key" \
  -e RAG__ApiKey="your-rag-key" \
  -e RAG__BaseUrl="https://aicore-healthcareteam-llm-server.tra220030.projects.jetstream-cloud.org/v1/chat/completions" \
  -e HeyGen__ApiKey="your-heygen-key" \
  -v /app/data:/app/data \
  ersimulator-app
```

---

## Method 2: Manual Build and Deploy

### Step 1: Build the Application Locally

```bash
cd "/Users/hginman/ER Simulator Web App/ERSimulatorApp"

# Restore dependencies
dotnet restore

# Publish the application
dotnet publish -c Release -o publish --no-self-contained
```

### Step 2: Create Deployment Package

```bash
# Copy Dockerfile to publish directory
cp Dockerfile publish/

# Create zip file
cd publish
zip -r ../ERSimulatorApp-deployment.zip .
cd ..
```

### Step 3: Upload to Server

```bash
scp ERSimulatorApp-deployment.zip exouser@149.165.154.35:~/AppDrop
```

### Step 4: Deploy on Server

SSH to the server and follow the steps in Method 1, Step 2.

---

## Quick Update Commands (Server-Side)

If you just need to rebuild and restart:

```bash
# SSH to server
ssh exouser@149.165.154.35
cd ~/AppDrop

# Extract, rebuild, restart
unzip -o ERSimulatorApp-deployment.zip
docker stop ersimulator && docker rm ersimulator
docker build -t ersimulator-app .
docker run -d --name ersimulator -p 8080:8080 \
  -e OpenAI__ApiKey="your-key" \
  -e RAG__ApiKey="your-key" \
  -e RAG__BaseUrl="https://aicore-healthcareteam-llm-server.tra220030.projects.jetstream-cloud.org/v1/chat/completions" \
  -e HeyGen__ApiKey="your-key" \
  -v /app/data:/app/data \
  ersimulator-app
```

---

## Verifying the Update

After deployment, verify the app is running:

```bash
# Check container status
docker ps | grep ersimulator

# Check logs
docker logs ersimulator --tail 50

# Test health endpoint
curl http://localhost:8080/api/health

# Or test in browser
# http://149.165.154.35:8080
```

---

## Troubleshooting

### Build Fails Locally
- Ensure .NET 8 SDK is installed: `dotnet --version`
- Check for compilation errors: `dotnet build`

### Upload Fails
- Verify SSH access: `ssh exouser@149.165.154.35`
- Check disk space on server: `df -h`

### Container Won't Start
- Check logs: `docker logs ersimulator`
- Verify environment variables are set correctly
- Check port 8080 is available: `sudo lsof -i :8080`

### App Not Accessible
- Check firewall: `sudo ufw status`
- Verify container is running: `docker ps`
- Check port mapping: `docker port ersimulator`

---

## Environment Variables Reference

Make sure to set these when starting the container:

- `OpenAI__ApiKey` - Your OpenAI API key
- `RAG__ApiKey` - Your RAG service API key  
- `RAG__BaseUrl` - RAG service URL
- `HeyGen__ApiKey` - Your HeyGen API key
- `ASPNETCORE_PATHBASE` - Optional path prefix

See `ENV_VARS_QUICK_REFERENCE.txt` for more details.

---

## Files Changed in This Update

The fixes applied:
- ✅ `ChatController.cs` - Fixed source filtering to show sources even without local files
- ✅ `AvatarStreamingController.cs` - Fixed source filtering in avatar endpoints
- ✅ `Index.cshtml` - Updated frontend to display sources without URLs
- ✅ `Avatar.cshtml` - Updated frontend to display sources without URLs

These changes ensure that reference sources appear in the UI even when PDF files aren't available locally on the server.




