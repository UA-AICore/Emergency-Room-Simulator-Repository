# Debugging Whisper Audio Transcription Issues

## Step 1: Check Browser Console

### How to Open Browser Console:

1. **Chrome/Edge:**
   - Press `F12` or `Ctrl+Shift+I` (Windows/Linux) or `Cmd+Option+I` (Mac)
   - Or right-click on the page → "Inspect" → Click "Console" tab

2. **Firefox:**
   - Press `F12` or `Ctrl+Shift+K` (Windows/Linux) or `Cmd+Option+K` (Mac)
   - Or right-click → "Inspect Element" → Click "Console" tab

3. **Safari:**
   - Enable Developer menu first: Safari → Preferences → Advanced → Check "Show Develop menu"
   - Then press `Cmd+Option+C` or Develop → Show JavaScript Console

### What to Look For:

After clicking the "Voice" button and recording, you should see logs like:

```
[AUDIO] Using MIME type: audio/webm
[AUDIO] Sending audio blob: {type: "audio/webm", size: 12345, fileName: "recording.webm", extension: "webm"}
[API] Calling POST /api/avatar/v2/streaming/audio {conversationId: "...", hasStreamingToken: true, audioSize: 12345}
[API] /audio response status: 200 OK
```

**Important things to check:**
- ✅ What MIME type is being used? (should be audio/wav, audio/mp3, or audio/webm)
- ✅ What is the audio blob size? (should be > 1000 bytes, ideally > 10000 bytes)
- ✅ What is the HTTP response status? (200 = success, 400/500 = error)
- ❌ Any red error messages? Copy the full error text

### If You See Errors:

Look for messages like:
- `Error accessing microphone: ...`
- `Failed to transcribe audio: ...`
- `HTTP 400` or `HTTP 500` errors

**Copy the entire error message** - it will help diagnose the issue.

---

## Step 2: Check Server Terminal Output

### How to Find Server Logs:

The server is running in a terminal window. Look for the terminal where you ran `dotnet run`.

### What to Look For:

When you record audio, you should see logs like:

```
[Information] Processing audio input for session abc123..., file: recording.webm, size: 12345 bytes, content type: audio/webm
[Information] Step 1: Transcribing audio with Whisper...
[Information] Starting audio transcription for file: recording.webm
[Information] Detected MIME type: audio/webm for file: recording.webm
[Information] Sending audio to Whisper API: FileName=recording.webm, MimeType=audio/webm, StreamLength=12345
```

### If Transcription Fails:

You'll see error messages like:

```
[Error] Whisper API error: 400 - {"error": {"message": "...", "type": "...", "param": null, "code": null}}
```

**Common error patterns:**

1. **400 Bad Request:**
   ```
   Whisper API error: 400 - Invalid audio format or corrupted file
   ```
   - Usually means the audio format isn't supported or file is corrupted

2. **401 Unauthorized:**
   ```
   Whisper API error: 401 - Invalid API key
   ```
   - Check your OpenAI API key in `appsettings.Development.json`

3. **413 Request Entity Too Large:**
   ```
   Whisper API error: 413 - File too large
   ```
   - Audio file exceeds 25MB limit

4. **Empty Transcript:**
   ```
   [Warning] Whisper API returned empty transcript
   ```
   - Audio might be too quiet, too short, or contain no speech

### How to Copy Server Logs:

1. **Select the text** in the terminal
2. **Copy** (Ctrl+C / Cmd+C)
3. **Paste** into a text file or share with support

---

## Step 3: Test Recording

### Quick Test Steps:

1. **Open Browser Console** (F12)
2. **Go to Avatar page** (http://localhost:5120/Avatar)
3. **Click "Start Session"**
4. **Click "Voice" button**
5. **Speak clearly for 2-3 seconds** (e.g., "Hello, this is a test")
6. **Click "Stop"**
7. **Watch both:**
   - Browser console for `[AUDIO]` and `[API]` logs
   - Server terminal for `[Information]` and `[Error]` logs

### Expected Success Flow:

**Browser Console:**
```
[AUDIO] Using MIME type: audio/webm
[AUDIO] Sending audio blob: {type: "audio/webm", size: 15234, ...}
[API] /audio response status: 200 OK
```

**Server Terminal:**
```
[Information] Processing audio input for session ...
[Information] Audio transcribed successfully: Hello, this is a test
[Information] Step 2: Querying RAG database...
```

### If It Fails:

**Copy both:**
1. The error from browser console (red text)
2. The error from server terminal (lines starting with `[Error]`)

These will show exactly what went wrong!

---

## Common Issues and Solutions

### Issue: "Permission denied"
- **Solution:** Allow microphone access in browser settings
- **Check:** Browser address bar should show microphone icon

### Issue: "Audio file is empty"
- **Solution:** Record for at least 1-2 seconds
- **Check:** Browser console should show `audioSize > 1000`

### Issue: "Whisper API error: 400"
- **Solution:** Audio format might not be supported
- **Check:** Browser console shows what format was used
- **Try:** Refresh page and record again (might get different format)

### Issue: "Whisper API error: 401"
- **Solution:** Check OpenAI API key in `appsettings.Development.json`
- **Verify:** Key starts with `sk-proj-` or `sk-`
- **Test:** Visit https://platform.openai.com/api-keys to verify key is active

### Issue: "Empty transcript"
- **Solution:** 
  - Speak louder and clearer
  - Record for longer (3-5 seconds)
  - Check microphone is working (test in another app)

---

## Quick Reference Commands

### View Server Logs in Real-Time:
```bash
# If server is running in background, check logs
# The logs appear directly in the terminal where dotnet run was executed
```

### Check if Server is Running:
```bash
curl http://localhost:5120/Avatar
# Should return: HTTP 200
```

### Test API Key (if needed):
```bash
curl https://api.openai.com/v1/models \
  -H "Authorization: Bearer YOUR_API_KEY"
# Should return list of models if key is valid
```

---

## Need More Help?

If you're still having issues, provide:
1. ✅ Browser console error (screenshot or copy text)
2. ✅ Server terminal error (copy the `[Error]` lines)
3. ✅ Audio format being used (from `[AUDIO]` log)
4. ✅ File size (from `[AUDIO]` log)

This information will help diagnose the exact issue!




