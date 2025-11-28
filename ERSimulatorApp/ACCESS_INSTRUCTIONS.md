# How to Access Your Deployed ER Simulator App

## 🌐 Direct Access (If Port 8080 is Open)

The app is running on **port 8080** on server **149.165.154.35**.

### Access URLs:

**Main Application:**
```
http://149.165.154.35:8080
```

**Health Check Endpoint:**
```
http://149.165.154.35:8080/api/health
```

**API Endpoints:**
```
http://149.165.154.35:8080/api/avatar/v2/streaming/session/create
http://149.165.154.35:8080/api/health/detailed
```

## 🔒 Security Note

If port 8080 is blocked by a firewall, you have two options:

### Option 1: Open Port 8080 in Firewall

```bash
# SSH into the server
ssh exouser@149.165.154.35

# Check firewall status
sudo ufw status

# Allow port 8080 (if using ufw)
sudo ufw allow 8080/tcp

# Or if using firewalld
sudo firewall-cmd --permanent --add-port=8080/tcp
sudo firewall-cmd --reload
```

### Option 2: Set Up Reverse Proxy (Recommended for Production)

Set up Nginx or Apache as a reverse proxy to:
- Access via standard HTTP/HTTPS ports (80/443)
- Add SSL/TLS encryption
- Use a domain name instead of IP

#### Example Nginx Configuration:

```nginx
server {
    listen 80;
    server_name your-domain.com;  # or use IP: 149.165.154.35

    location / {
        proxy_pass http://localhost:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection keep-alive;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Prefix /;
        proxy_cache_bypass $http_upgrade;
    }
}
```

After setting up reverse proxy, access via:
```
http://your-domain.com
# or
http://149.165.154.35
```

## 🧪 Test Access

### From Your Local Machine:

```bash
# Test health endpoint
curl http://149.165.154.35:8080/api/health

# Expected response:
# {"status":"healthy","timestamp":"...","version":"1.0.0"}

# Test in browser
open http://149.165.154.35:8080
```

### From Server (SSH):

```bash
ssh exouser@149.165.154.35

# Test locally
curl http://localhost:8080/api/health

# Check container status
docker ps | grep ersimulator

# View logs
docker logs ersimulator
```

## 🔍 Troubleshooting

### Can't Access from Browser

1. **Check if port is open:**
   ```bash
   # From your local machine
   telnet 149.165.154.35 8080
   # or
   nc -zv 149.165.154.35 8080
   ```

2. **Check firewall on server:**
   ```bash
   ssh exouser@149.165.154.35
   sudo ufw status
   sudo iptables -L -n | grep 8080
   ```

3. **Check if container is running:**
   ```bash
   ssh exouser@149.165.154.35 "docker ps | grep ersimulator"
   ```

4. **Check container logs:**
   ```bash
   ssh exouser@149.165.154.35 "docker logs ersimulator --tail 50"
   ```

### Container Not Running

Restart the container:
```bash
ssh exouser@149.165.154.35 "cd ~/AppDrop && docker restart ersimulator"
```

### Port Already in Use

Check what's using port 8080:
```bash
ssh exouser@149.165.154.35 "sudo lsof -i :8080"
```

## 📝 Quick Reference

- **Server IP**: 149.165.154.35
- **Port**: 8080
- **Container Name**: ersimulator
- **Docker Image**: ersimulator-app
- **Health Endpoint**: `/api/health`
- **SSH Access**: `ssh exouser@149.165.154.35`

## 🔐 For Production

Consider:
1. Setting up SSL/TLS certificate (Let's Encrypt)
2. Using a domain name instead of IP
3. Setting up proper firewall rules
4. Configuring reverse proxy (Nginx/Apache)
5. Setting up monitoring and logging
6. Configuring automatic restarts (Docker restart policies)


