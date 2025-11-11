# HeyGen Avatar Integration Plan for ER Simulator

## Overview
Integrate HeyGen's AI avatar technology to create a conversational medical instructor avatar that speaks directly to users while maintaining RAG database references.

## Architecture Options

### Option 1: HeyGen API Integration (Recommended)
Use HeyGen's Conversational Video API to create real-time avatar conversations.

**Flow:**
```
User speaks → Browser/Audio API → HeyGen API → Your Backend (RAG) → HeyGen API → Avatar speaks
```

### Option 2: HeyGen + Custom WebRTC
Use HeyGen for avatar generation with custom WebRTC for real-time streaming.

**Flow:**
```
User speaks → WebRTC → Your Backend → RAG → Generate TTS → HeyGen Avatar → Stream back
```

## Recommended Implementation: HeyGen Conversational Video API

### Step 1: Get HeyGen API Access
1. Sign up for HeyGen at https://heygen.com
2. Get API key from dashboard
3. Subscribe to Conversational Video API plan

### Step 2: Architecture Changes Needed

#### New Components:
1. **Audio/Video Frontend** - Replace chat UI with video player
2. **HeyGen Service** - Handle avatar API calls
3. **Audio Processing** - Convert speech to text, text to speech
4. **Streaming Handler** - Handle real-time video stream

### Step 3: Code Structure

```
ERSimulatorApp/
├── Controllers/
│   ├── AvatarController.cs          [NEW] - Handle avatar sessions
│   └── ChatController.cs            [MODIFY] - Keep for fallback
├── Services/
│   ├── HeyGenService.cs             [NEW] - HeyGen API integration
│   ├── AudioProcessingService.cs    [NEW] - Speech to text, TTS
│   └── RAGService.cs                [KEEP] - Already works!
└── Pages/
    ├── Avatar.cshtml                [NEW] - Avatar interface
    └── Index.cshtml                 [MODIFY] - Keep as option
```

## Implementation Details

### 1. HeyGenService.cs

```csharp
using System.Text;
using System.Text.Json;

namespace ERSimulatorApp.Services
{
    public interface IHeyGenService
    {
        Task<string> StartConversationAsync(string sessionId, string initialPrompt);
        Task SendAudioMessageAsync(string conversationId, byte[] audioData);
        Task<Stream> GetVideoStreamAsync(string conversationId);
        Task EndConversationAsync(string conversationId);
    }

    public class HeyGenService : IHeyGenService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _apiUrl = "https://api.heygen.com/v1/";
        private readonly ILogger<HeyGenService> _logger;

        public HeyGenService(
            HttpClient httpClient, 
            IConfiguration configuration,
            ILogger<HeyGenService> logger)
        {
            _httpClient = httpClient;
            _apiKey = configuration["HeyGen:ApiKey"] ?? throw new InvalidOperationException("HeyGen API key not found");
            _logger = logger;
        }

        public async Task<string> StartConversationAsync(string sessionId, string initialPrompt)
        {
            var request = new
            {
                avatar_id = "YOUR_MEDICAL_INSTRUCTOR_AVATAR_ID",
                language = "en-US",
                voice = "medical_instructor_female", // HeyGen allows custom voices
                prompt = initialPrompt,
                session_id = sessionId
            };

            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);

            var response = await _httpClient.PostAsync($"{_apiUrl}conversations/start", content);
            var responseContent = await response.Content.ReadAsStringAsync();
            
            if (response.IsSuccessStatusCode)
            {
                var result = JsonSerializer.Deserialize<HeyGenResponse>(responseContent);
                return result?.ConversationId ?? throw new InvalidOperationException("Failed to start conversation");
            }
            
            throw new HttpRequestException($"HeyGen API error: {response.StatusCode}");
        }

        public async Task<Stream> GetVideoStreamAsync(string conversationId)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_apiUrl}conversations/{conversationId}/stream");
            request.Headers.Add("X-Api-Key", _apiKey);
            
            var response = await _httpClient.SendAsync(request);
            return await response.Content.ReadAsStreamAsync();
        }
    }
}
```

### 2. AvatarController.cs

```csharp
using Microsoft.AspNetCore.Mvc;

namespace ERSimulatorApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AvatarController : ControllerBase
    {
        private readonly IHeyGenService _heyGenService;
        private readonly ILLMService _llmService; // Your existing RAG service
        private readonly ILogger<AvatarController> _logger;

        public AvatarController(
            IHeyGenService heyGenService,
            ILLMService llmService,
            ILogger<AvatarController> logger)
        {
            _heyGenService = heyGenService;
            _llmService = llmService;
            _logger = logger;
        }

        [HttpPost("session/start")]
        public async Task<IActionResult> StartSession([FromBody] AvatarSessionRequest request)
        {
            try
            {
                // Get initial RAG response
                var ragResponse = await _llmService.GetResponseAsync(
                    "You are an experienced medical instructor. Greet the student and ask how you can help.");
                
                // Start HeyGen conversation with RAG context
                var conversationId = await _heyGenService.StartConversationAsync(request.SessionId, ragResponse);
                
                return Ok(new { conversationId, sessionId = request.SessionId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting avatar session");
                return StatusCode(500, new { error = "Failed to start session" });
            }
        }

        [HttpPost("message")]
        public async Task<IActionResult> SendMessage([FromBody] AvatarMessageRequest request)
        {
            try
            {
                // User asks question → Get RAG response
                var ragResponse = await _llmService.GetResponseAsync(request.Message);
                
                // Send to HeyGen to have avatar speak it
                // HeyGen will generate video with avatar saying the response
                
                return Ok(new { response = ragResponse, timestamp = DateTime.UtcNow });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing avatar message");
                return StatusCode(500, new { error = "Failed to process message" });
            }
        }
    }
}
```

## Frontend: Avatar.cshtml

```html
@page
@model ERSimulatorApp.Pages.AvatarModel

<div class="container-fluid h-100">
    <div class="row h-100">
        <!-- Avatar Video Panel -->
        <div class="col-lg-8 d-flex flex-column">
            <div class="avatar-container flex-grow-1">
                <!-- HeyGen Video Player -->
                <video id="avatarVideo" autoplay playsinline muted class="w-100 h-100"></video>
                
                <!-- Audio controls (unmute to hear) -->
                <button id="muteBtn" class="btn btn-primary position-absolute bottom-0">
                    <i class="fas fa-microphone"></i>
                </button>
            </div>
            
            <!-- Chat log for reference -->
            <div class="chat-history mt-2" style="height: 150px; overflow-y: auto;">
                <!-- Show transcription and sources -->
            </div>
        </div>
        
        <!-- Info Panel -->
        <div class="col-lg-4">
            <div class="info-panel h-100 p-3">
                <h5>Medical Instructor Assistant</h5>
                <div id="currentTopics">
                    <strong>Discussing:</strong>
                    <span id="currentTopic">No topic yet</span>
                </div>
                <div id="sourcesPanel" class="mt-3">
                    <h6>Sources:</h6>
                    <ul id="sourcesList"></ul>
                </div>
            </div>
        </div>
    </div>
</div>

@section Scripts {
<script>
    // WebRTC or HeyGen SDK integration
    // Handle audio input
    // Stream video output
    // Display sources
</script>
}
```

## Integration Flow

### User Interaction Flow:
```
1. User opens /Avatar page
2. User clicks "Start Session"
3. Your backend starts HeyGen conversation
4. HeyGen stream begins → User sees avatar
5. User speaks: "What are the ABCs of trauma?"
6. Audio → Backend → Speech-to-Text
7. Backend → RAG Service → Gets medical answer + sources
8. Backend → Text-to-Speech (or HeyGen TTS)
9. HeyGen generates avatar video saying response
10. Video streams to user
11. Sources display in side panel
```

## HeyGen Configuration

### 1. Avatar Creation
In HeyGen dashboard:
- Create custom medical instructor avatar
- Upload or generate professional medical attire
- Choose professional demeanor

### 2. Voice Configuration
- Use HeyGen's voice cloning or
- Use their medical training voices
- Configure speaking style (enthusiastic instructor)

### 3. API Setup
Add to `appsettings.json`:
```json
{
  "HeyGen": {
    "ApiKey": "your-heygen-api-key",
    "ApiUrl": "https://api.heygen.com/v1/",
    "AvatarId": "your-avatar-id",
    "VoiceId": "medical_instructor",
    "Language": "en-US"
  }
}
```

## Technical Considerations

### 1. Real-Time Audio Processing
```csharp
// Use WebRTC or browser Audio API
public class AudioProcessingService
{
    public async Task<string> SpeechToTextAsync(byte[] audioData)
    {
        // Use Azure Speech Services, Google Speech-to-Text, or HeyGen's built-in
        // HeyGen API may include this
    }
    
    public async Task<byte[]> TextToSpeechAsync(string text, string voice)
    {
        // Convert text to audio for HeyGen to sync with avatar
    }
}
```

### 2. Streaming Video
```javascript
// Frontend JavaScript
const videoElement = document.getElementById('avatarVideo');
const mediaSource = new MediaSource();

videoElement.src = URL.createObjectURL(mediaSource);

// Connect to HeyGen stream
// Or use HeyGen's JavaScript SDK
```

### 3. Sources Display
Keep your existing RAG source citation system:
```javascript
function displaySources(response) {
    const sources = response.sources;
    const list = document.getElementById('sourcesList');
    
    sources.forEach(source => {
        const li = document.createElement('li');
        li.innerHTML = `${source.filename} (${source.similarity}% match)`;
        list.appendChild(li);
    });
}
```

## Cost Considerations

### HeyGen Pricing (estimated):
- **Basic**: ~$50/month for limited minutes
- **Pro**: ~$200/month for 500 minutes
- **Enterprise**: Custom pricing

### Alternative Approach:
If cost is a concern, consider:
1. **Recorded segments** - Pre-record avatar responses for common questions
2. **Hybrid approach** - Avatar for intro/conclusion, chat for Q&A
3. **API optimization** - Cache frequent responses to reduce API calls

## Implementation Steps

### Phase 1: Basic Integration
1. ✅ Sign up for HeyGen API
2. ✅ Create medical instructor avatar in HeyGen dashboard
3. ✅ Implement HeyGenService.cs
4. ✅ Create AvatarController.cs
5. ✅ Add HeyGen configuration

### Phase 2: Frontend
6. ✅ Create Avatar.cshtml page
7. ✅ Implement video streaming
8. ✅ Add audio input handling
9. ✅ Integrate with existing RAG backend

### Phase 3: Polish
10. ✅ Add sources display
11. ✅ Add session management
12. ✅ Add error handling
13. ✅ Add loading states

## API Integration Example

### Your Backend Acts as Middleware:
```csharp
[HttpPost("avatar/speak")]
public async Task<IActionResult> Speak([FromBody] AvatarSpeakRequest request)
{
    // 1. Get user's audio/transcript
    var userMessage = request.Transcript;
    
    // 2. Send to your RAG system (keep your existing service!)
    var medicalResponse = await _llmService.GetResponseAsync(userMessage);
    
    // 3. Extract sources
    var sources = ExtractSourcesFromResponse(medicalResponse);
    
    // 4. Send to HeyGen to speak
    var heyGenResponse = await _heyGenService.GenerateAvatarSpeechAsync(
        medicalResponse, 
        avatarId: "medical_instructor"
    );
    
    // 5. Return video stream + sources
    return Ok(new {
        videoUrl = heyGenResponse.VideoUrl,
        sources = sources,
        transcript = medicalResponse
    });
}
```

## Benefits of This Approach

✅ **Keep your RAG system** - No changes needed to existing medical retrieval
✅ **Add personality** - HeyGen avatar makes it conversational
✅ **Visual engagement** - Avatar vs plain chat
✅ **Sources preserved** - Display in side panel
✅ **Scalable** - HeyGen handles video generation

## Alternative: Text-to-Video Generation

If real-time isn't needed, you could:
1. Generate text responses (keep current system)
2. Send text to HeyGen for video generation
3. Return short video clips (~10-20 seconds)
4. Play video with sources displayed

This reduces API costs significantly!

## Getting Started

1. **Sign up**: https://heygen.com
2. **Get API key**: HeyGen Dashboard → API
3. **Create avatar**: Custom medical instructor
4. **Test API**: Simple integration first
5. **Scale up**: Add to your app

Would you like me to implement the HeyGen integration for your app?







