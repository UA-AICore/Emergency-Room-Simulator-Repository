# ER Simulator App - Deployment Checklist

## Version Information
- **Build Date**: December 3, 2025
- **Framework**: .NET 8.0
- **Model**: Llama-3.2-1B-instruct
- **Key Features**: Advanced Source Inference, Enhanced Logging

## Pre-Deployment Checklist

### ✅ Build Status
- [x] Release build completed successfully
- [x] All dependencies included in publish folder
- [x] Configuration files present (appsettings.json, web.config)
- [x] Static assets (wwwroot) included

### ✅ Code Features Verified
- [x] Advanced source inference (`InferSourcesFromRemoteResponse`)
- [x] Enhanced logging throughout pipeline
- [x] Sources display only on Chat page
- [x] Avatar pages exclude sources (backend filtering)
- [x] Llama-3.2-1B branding updated

### ✅ Configuration Files
- [x] `appsettings.json` - Production settings
- [x] `appsettings.Development.json` - Development settings
- [x] `web.config` - IIS configuration (if applicable)

### 📦 Published Files Location
```
ERSimulatorApp/publish/
├── ERSimulatorApp.dll (527KB)
├── ERSimulatorApp (121KB executable)
├── appsettings.json
├── appsettings.Development.json
├── web.config
├── wwwroot/ (static assets)
└── [dependencies]
```

## Deployment Steps

### 1. Server Requirements
- .NET 8.0 Runtime installed
- Port 80/443 available (or configured port)
- Required environment variables or appsettings.json configured

### 2. Configuration
Ensure the following are configured in `appsettings.json`:
- **RAG:BaseUrl**: Remote RAG server endpoint
- **RAG:ApiKey**: API key for RAG service
- **RAG:Model**: `meta-llama/Llama-3.2-1B-instruct`
- **OpenAI:ApiKey**: For patient personality service
- **HeyGen:ApiKey**: For avatar streaming
- **HeyGenPatient:ApiKey**: For patient avatar

### 3. Deployment Options

#### Option A: Direct Deployment
```bash
# Copy publish folder to server
scp -r publish/ user@server:/path/to/app/

# On server, run:
cd /path/to/app
dotnet ERSimulatorApp.dll --urls "http://0.0.0.0:80"
```

#### Option B: Systemd Service
Create `/etc/systemd/system/ersimulator.service`:
```ini
[Unit]
Description=ER Simulator App
After=network.target

[Service]
Type=notify
WorkingDirectory=/path/to/app
ExecStart=/usr/bin/dotnet /path/to/app/ERSimulatorApp.dll --urls "http://0.0.0.0:80"
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
```

#### Option C: Docker (if Dockerfile exists)
```bash
docker build -t er-simulator .
docker run -p 80:80 er-simulator
```

### 4. Post-Deployment Verification
- [ ] Application starts without errors
- [ ] Health endpoint responds: `/api/Health`
- [ ] Chat page loads and sources display correctly
- [ ] Avatar pages load without sources
- [ ] API endpoints respond correctly
- [ ] Logs show source inference working

### 5. Environment Variables (Optional)
If using environment variables instead of appsettings.json:
- `RAG__BaseUrl`
- `RAG__ApiKey`
- `RAG__Model`
- `OpenAI__ApiKey`
- `HeyGen__ApiKey`
- `HeyGenPatient__ApiKey`

## Key Improvements in This Version
1. **Advanced Source Inference**: Intelligent document matching from RAG responses
2. **Enhanced Logging**: Detailed source tracking throughout pipeline
3. **Clean Architecture**: Sources only on Chat page, excluded from Avatar pages
4. **Updated Branding**: Llama-3.2-1B model branding

## Backup Information
- Original app backed up to: `ERSimulatorApp.backup_20251203_095228`
- All improvements from whisper-patient test integrated

## Troubleshooting
- Check logs for source inference messages
- Verify RAG API connectivity
- Ensure all API keys are valid
- Check port availability and firewall rules

