# Path Base Configuration

## Overview
The ER Simulator App is configured to work correctly when hosted behind a reverse proxy (nginx) at a sub-path like `/er-simulator/`.

## How It Works

### 1. Path Base Detection
The app reads the path base from two sources (in priority order):
1. **X-Forwarded-Prefix Header** - Set by the reverse proxy (nginx)
2. **ASPNETCORE_PATHBASE Environment Variable** - Fallback for direct deployment

### 2. Implementation Details

#### Program.cs Middleware
A custom middleware runs early in the pipeline (after `UseForwardedHeaders()`) that:
- Checks for `X-Forwarded-Prefix` header from the reverse proxy
- Falls back to `ASPNETCORE_PATHBASE` environment variable if header is not present
- Normalizes the path base (ensures it starts with `/` and doesn't end with `/`)
- Sets `context.Request.PathBase` for the current request

#### Static Files & Razor Views
- Static files (CSS, JS, images) are automatically prefixed with the path base
- Razor views using `~/` syntax (e.g., `~/css/site.css`) automatically resolve to the correct path
- All `asp-page` and `asp-area` tag helpers respect the path base

### 3. Example

When the app is hosted at `https://example.com/er-simulator/`:

**Before (Broken):**
- Browser requests: `https://example.com/css/site.css` ❌ (404 Not Found)
- Browser requests: `https://example.com/lib/bootstrap/dist/css/bootstrap.min.css` ❌ (404 Not Found)

**After (Fixed):**
- Browser requests: `https://example.com/er-simulator/css/site.css` ✅
- Browser requests: `https://example.com/er-simulator/lib/bootstrap/dist/css/bootstrap.min.css` ✅

### 4. Configuration

#### For Reverse Proxy Deployment (Recommended)
The reverse proxy should set the `X-Forwarded-Prefix` header:
```nginx
location /er-simulator/ {
    proxy_pass http://localhost:8080/;
    proxy_set_header X-Forwarded-Prefix /er-simulator;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    # ... other proxy settings
}
```

#### For Direct Deployment (Fallback)
Set the environment variable:
```bash
export ASPNETCORE_PATHBASE=/er-simulator
```

Or in Docker:
```dockerfile
ENV ASPNETCORE_PATHBASE=/er-simulator
```

### 5. Files Affected
- ✅ `Program.cs` - Path base middleware and configuration
- ✅ `_Layout.cshtml` - Uses `~/` syntax (automatically respects path base)
- ✅ All Razor pages - Use `asp-page` helpers (automatically respect path base)
- ✅ Static files - Automatically served with correct path prefix

### 6. Testing
To test locally with a path base:
```bash
# Set environment variable
export ASPNETCORE_PATHBASE=/er-simulator

# Run the app
dotnet run --urls "http://localhost:5000"

# Access at: http://localhost:5000/er-simulator/
# Static files should load correctly from: http://localhost:5000/er-simulator/css/site.css
```

## Troubleshooting

### Static Files Still Not Loading?
1. Verify the `X-Forwarded-Prefix` header is being sent by the reverse proxy
2. Check that the middleware runs before `UseStaticFiles()`
3. Ensure Razor views use `~/` syntax, not absolute paths like `/css/site.css`
4. Check browser console for 404 errors and verify the requested paths

### Path Base Not Detected?
1. Check reverse proxy configuration for `X-Forwarded-Prefix` header
2. Verify `ASPNETCORE_PATHBASE` environment variable is set (if using fallback)
3. Check application logs for path base detection

