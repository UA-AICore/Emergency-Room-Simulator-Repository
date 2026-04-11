using ERSimulatorApp.Models;
using ERSimulatorApp.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace ERSimulatorApp.Controllers
{
    /// <summary>
    /// Clean API controller for HeyGen Streaming Avatar integration
    /// </summary>
    [ApiController]
    [Route("api/avatar/v2/streaming")]
    public class AvatarStreamingController : ControllerBase
    {
        private readonly IHeyGenStreamingService _heyGenService;
        private readonly ILLMService _llmService;
        private readonly IElevenLabsSpeechToTextService _speechToTextService;
        private readonly IElevenLabsTextToSpeechService _ttsService;
        private readonly ILogger<AvatarStreamingController> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _sourceDocumentsPath;
        private readonly int _maxSpeechChars;

        public AvatarStreamingController(
            IHeyGenStreamingService heyGenService,
            ILLMService llmService,
            IElevenLabsSpeechToTextService speechToTextService,
            IElevenLabsTextToSpeechService ttsService,
            ILogger<AvatarStreamingController> logger,
            IConfiguration configuration,
            IWebHostEnvironment environment)
        {
            _heyGenService = heyGenService;
            _llmService = llmService;
            _speechToTextService = speechToTextService;
            _ttsService = ttsService;
            _logger = logger;
            _configuration = configuration;

            var configuredPath = configuration["RAG:SourceDocumentsPath"];
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                _sourceDocumentsPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "rag_backend", "data", "trauma_pdfs"));
            }
            else
            {
                _sourceDocumentsPath = Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredPath));
            }

            if (!Directory.Exists(_sourceDocumentsPath))
            {
                _logger.LogWarning("Source documents path does not exist: {SourcePath}", _sourceDocumentsPath);
            }

            _maxSpeechChars = configuration.GetValue<int?>("LiveAvatar:MaxSpeechChars")
                ?? configuration.GetValue("HeyGen:MaxSpeechChars", 1200);
        }

        /// <summary>Per-session conversation history for Dr. Dexter (student questions + his answers).</summary>
        private static readonly ConcurrentDictionary<string, List<ConversationMessage>> _avatarHistory = new();

        /// <summary>Session IDs we've already called HeyGen streaming.start for (avoid redundant 400).</summary>
        private static readonly ConcurrentDictionary<string, byte> _sessionsStarted = new();

        private const int AvatarContextMessageCount = 32; // last 16 exchanges (student + Dr. Dexter each) for wider context

        /// <summary>Message sent by the frontend when the avatar session starts (Avatar.cshtml).</summary>
        private static bool IsInitialGreeting(string message) =>
            !string.IsNullOrEmpty(message) &&
            message.Contains("Briefly introduce yourself as Dr. Dexter", StringComparison.OrdinalIgnoreCase);

        private const string ScriptedDrDexterGreeting = "Hi, I'm Dr. Dexter. I'm an ER physician and I'm here to help you learn about emergency medicine and trauma care. What would you like to explore today?";

        private static string BuildPromptWithAvatarContext(string currentQuestion, List<ConversationMessage> history)
        {
            List<ConversationMessage> recent = history.Count <= 1
                ? new List<ConversationMessage>()
                : history.Take(history.Count - 1).TakeLast(AvatarContextMessageCount).ToList();
            if (recent.Count == 0)
                return currentQuestion;
            var lines = recent.Select(m => m.Role == "user"
                ? "Student: " + m.Content
                : "Dr. Dexter: " + m.Content);
            return "Recent conversation:\n" + string.Join("\n", lines) + "\n\nStudent's current question: " + currentQuestion + "\n\nAnswer ONLY the student's CURRENT question above. You MUST respond in the SAME language as the student's current question (e.g. if they asked in Spanish, answer entirely in Spanish; if in English, in English). Keep your answer to one or two brief paragraphs—err on the concise side. Sound personable and natural, like a real attending talking to a resident, not a textbook or chatbot.";
        }

        /// <summary>
        /// Truncate text for HeyGen so each speech segment stays short and is less likely to disconnect mid-sentence.
        /// Full response is still returned in the API; only what the avatar speaks is capped.
        /// </summary>
        private string TruncateForAvatar(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= _maxSpeechChars)
                return text ?? string.Empty;
            var truncated = text.Substring(0, _maxSpeechChars);
            var lastSpace = truncated.LastIndexOf(' ');
            if (lastSpace > _maxSpeechChars / 2)
                truncated = truncated.Substring(0, lastSpace);
            return truncated.TrimEnd() + "…";
        }

        /// <summary>
        /// Create a new streaming session
        /// Returns LiveKit connection details
        /// </summary>
        [HttpPost("session/create")]
        public async Task<IActionResult> CreateSession()
        {
            try
            {
                _logger.LogInformation("Creating streaming session (server speech: {Server})",
                    _heyGenService.DeliversSpeechViaServer);

                var sessionData = await _heyGenService.CreateStreamingSessionAsync();

                var useServerTtsForLiveAvatar = _configuration.GetValue<bool>("ElevenLabs:UseServerTtsForLiveAvatar");
                var useLiveAvatarAgentSpeak = string.Equals(sessionData.Provider, "liveavatar", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(sessionData.WsUrl)
                    && _configuration.GetValue<bool>("LiveAvatar:UseAgentSpeakWebSocket");

                var auxiliaryVoiceMode = "none";
                if (string.Equals(sessionData.Provider, "liveavatar", StringComparison.OrdinalIgnoreCase))
                {
                    var aux = (_configuration["LiveAvatar:AuxiliaryVoiceMode"] ?? "None").Trim();
                    if (aux.Equals("BrowserTts", StringComparison.OrdinalIgnoreCase))
                        auxiliaryVoiceMode = "browsertts";
                    else if (aux.Equals("ServerTts", StringComparison.OrdinalIgnoreCase))
                        auxiliaryVoiceMode = "servertts";
                }

                var agentSpeakPcmHz = GetAgentSpeakPcmSampleRateHz();

                return Ok(new
                {
                    success = true,
                    sessionId = sessionData.SessionId,
                    url = sessionData.Url,
                    accessToken = sessionData.AccessToken,
                    streamingToken = sessionData.StreamingToken,
                    wsUrl = sessionData.WsUrl,
                    avatarProvider = sessionData.Provider,
                    deliversSpeechViaServer = _heyGenService.DeliversSpeechViaServer,
                    useServerTtsForLiveAvatar,
                    useLiveAvatarAgentSpeak,
                    auxiliaryVoiceMode,
                    agentSpeakPcmSampleRateHz = agentSpeakPcmHz
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating streaming session");
                return StatusCode(500, new
                {
                    success = false,
                    error = "Failed to create streaming session",
                    message = ex.Message
                });
            }
        }

        /// <summary>
        /// Synthesize speech as WAV (default, 24 kHz) or raw PCM s16le mono for LiveAvatar <c>agent.speak</c> (<c>?format=pcm</c>).
        /// PCM output rate is <c>LiveAvatar:AgentSpeakPcmSampleRateHz</c> (default 24000, same as TTS). Use 48000 only if LiveAvatar plays too fast/chipmunk; if voice sounds too deep/slow at 48000, keep 24000.
        /// </summary>
        [HttpPost("tts")]
        public async Task<IActionResult> SynthesizeTts([FromQuery] string? format, [FromBody] TtsSynthesizeRequest? body, CancellationToken cancellationToken)
        {
            var text = (body?.Text ?? "").Trim();
            if (string.IsNullOrEmpty(text))
                return BadRequest(new { error = "Text is required" });
            if (text.Length > _maxSpeechChars)
                return BadRequest(new { error = $"Text exceeds maximum length ({_maxSpeechChars} characters)" });

            const int ttsInternalHz = 24000;
            var result = await _ttsService.SynthesizePcm24kAsync(text, cancellationToken);
            if (!string.IsNullOrEmpty(result.ErrorMessage))
                return StatusCode(502, new { error = result.ErrorMessage });
            if (result.Pcm.Length == 0)
                return StatusCode(502, new { error = "TTS returned no audio" });

            if (string.Equals(format, "pcm", StringComparison.OrdinalIgnoreCase))
            {
                var outHz = GetAgentSpeakPcmSampleRateHz();
                var pcm = outHz == ttsInternalHz
                    ? result.Pcm
                    : PcmS16LeMonoResampler.Resample(result.Pcm, ttsInternalHz, outHz);
                Response.Headers.Append("X-Pcm-Sample-Rate-Hz", outHz.ToString());
                return File(pcm, "application/octet-stream");
            }

            var wav = Pcm16MonoWavBuilder.Build(result.Pcm, ttsInternalHz);
            return File(wav, "audio/wav", "speech.wav");
        }

        /// <summary>Sample rate for <c>/tts?format=pcm</c> (LiveAvatar agent.speak). Default 24000 matches Edge/ElevenLabs TTS; raise to 48000 only if you hear fast/chipmunk speech.</summary>
        private int GetAgentSpeakPcmSampleRateHz() =>
            Math.Clamp(_configuration.GetValue("LiveAvatar:AgentSpeakPcmSampleRateHz", 24000), 8000, 96000);

        /// <summary>
        /// Process audio input: ElevenLabs STT → RAG → HeyGen
        /// Complete voice conversation flow
        /// </summary>
        [HttpPost("audio")]
        public async Task<IActionResult> ProcessAudio([FromForm] string conversationId, [FromForm] string? streamingToken, [FromForm] bool useClaude = false)
        {
            try
            {
                _logger.LogInformation("ProcessAudio called - ContentType: {ContentType}, HasFormContentType: {HasForm}, Files.Count: {FileCount}",
                    Request.ContentType, Request.HasFormContentType, Request.Form.Files.Count);

                if (string.IsNullOrWhiteSpace(conversationId))
                {
                    _logger.LogWarning("ProcessAudio: conversationId is missing or empty");
                    return BadRequest(new { error = "Conversation ID is required" });
                }

                // Check if request contains audio file
                if (!Request.HasFormContentType)
                {
                    _logger.LogWarning("ProcessAudio: Request does not have form content type. ContentType: {ContentType}", Request.ContentType);
                    return BadRequest(new { error = "Request must be multipart/form-data" });
                }

                if (Request.Form.Files.Count == 0)
                {
                    _logger.LogWarning("ProcessAudio: No files in request. Form keys: {FormKeys}", string.Join(", ", Request.Form.Keys));
                    return BadRequest(new { error = "Audio file is required" });
                }

                // Try to get audio file by name first, then fall back to first file
                var audioFile = Request.Form.Files["audio"] ?? Request.Form.Files[0];
                if (audioFile == null || audioFile.Length == 0)
                {
                    _logger.LogWarning("ProcessAudio: Audio file is null or empty. FileName: {FileName}, Length: {Length}",
                        audioFile?.FileName, audioFile?.Length);
                    return BadRequest(new { error = "Audio file is empty" });
                }

                // Validate audio file size (minimum 1KB, maximum 25MB)
                if (audioFile.Length < 1024)
                {
                    _logger.LogWarning("ProcessAudio: Audio file too small: {Size} bytes", audioFile.Length);
                    return BadRequest(new { error = "Audio file is too small. Please record at least 1 second of audio." });
                }
                if (audioFile.Length > 25 * 1024 * 1024)
                {
                    _logger.LogWarning("ProcessAudio: Audio file too large: {Size} bytes", audioFile.Length);
                    return BadRequest(new { error = "Audio file is too large. Maximum size is 25MB." });
                }

                _logger.LogInformation("Processing audio input for session {SessionId}, file: {FileName}, size: {Size} bytes, contentType: {ContentType}, streamingToken length: {TokenLength}",
                    conversationId, audioFile.FileName, audioFile.Length, audioFile.ContentType, streamingToken?.Length ?? 0);

                // Validate streaming token early
                if (string.IsNullOrWhiteSpace(streamingToken))
                {
                    _logger.LogError("ProcessAudio: Streaming token is REQUIRED for audio processing - token from session creation must be reused. Session ID: {SessionId}", conversationId);
                    return StatusCode(400, new
                    {
                        success = false,
                        error = "Streaming token is required - session may have been created incorrectly"
                    });
                }

                // Step 1: Transcribe audio using ElevenLabs speech-to-text
                _logger.LogInformation("Step 1: Transcribing audio with ElevenLabs STT... File: {FileName}, Size: {Size} bytes, ContentType: {ContentType}",
                    audioFile.FileName, audioFile.Length, audioFile.ContentType);
                
                // Ensure we have a valid file name with extension
                var fileName = audioFile.FileName;
                if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
                {
                    // Default to webm if no extension
                    fileName = "audio.webm";
                    _logger.LogWarning("Audio file name missing or has no extension, defaulting to: {FileName}", fileName);
                }
                
                string transcript;
                try
                {
                    _logger.LogInformation("Step 1a: Opening audio stream from file (Length: {Length} bytes)...", audioFile.Length);
                    using var audioStream = audioFile.OpenReadStream();
                    
                    // Verify stream is readable
                    if (!audioStream.CanRead)
                    {
                        _logger.LogError("Audio stream is not readable");
                        return BadRequest(new { 
                            error = "Audio file stream is not readable. Please try recording again.",
                            details = "Stream cannot be read"
                        });
                    }
                    
                    _logger.LogInformation("Step 1b: Audio stream opened successfully, calling ElevenLabs STT with fileName: {FileName}...", fileName);
                    transcript = await _speechToTextService.TranscribeAudioAsync(audioStream, fileName);
                    
                    if (string.IsNullOrWhiteSpace(transcript))
                    {
                        _logger.LogWarning("ElevenLabs STT returned empty transcript - audio may be too short, unclear, or in unsupported format");
                        return BadRequest(new { 
                            error = "Could not transcribe audio. The audio may be too short, unclear, or in an unsupported format. Please try recording again.",
                            details = "ElevenLabs speech-to-text returned empty transcript"
                        });
                    }

                    _logger.LogInformation("Step 1c: Audio transcribed successfully. Transcript length: {Length} chars, Preview: {Preview}",
                        transcript.Length, transcript.Substring(0, Math.Min(100, transcript.Length)));
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("API key") || ex.Message.Contains("not configured"))
                {
                    _logger.LogError(ex, "ElevenLabs API key error: {Message}", ex.Message);
                    return StatusCode(500, new
                    {
                        success = false,
                        error = "ElevenLabs API key not configured. Set ElevenLabs:ApiKey or ELEVENLABS_API_KEY for Dr. Dexter voice input.",
                        message = ex.Message,
                        details = "Voice input for the avatar requires an ElevenLabs API key."
                    });
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogError(ex, "ElevenLabs API authentication failed: {Message}", ex.Message);
                    return StatusCode(401, new
                    {
                        success = false,
                        error = "ElevenLabs API authentication failed. Please verify your API key.",
                        message = ex.Message,
                        details = "The xi-api-key may be invalid or not have speech-to-text access."
                    });
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "ElevenLabs STT HTTP error: {Message}", ex.Message);
                    return StatusCode(500, new
                    {
                        success = false,
                        error = "Failed to connect to ElevenLabs speech-to-text. Check your connection and API key.",
                        message = ex.Message,
                        details = "Network error or ElevenLabs service issue."
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error transcribing audio with ElevenLabs STT: {Message}\n{StackTrace}", ex.Message, ex.StackTrace);
                    return StatusCode(500, new
                    {
                        success = false,
                        error = "Failed to transcribe audio",
                        message = ex.Message,
                        details = $"Error type: {ex.GetType().Name}. Check application logs for more details."
                    });
                }

                // Step 2: Get RAG response from medical knowledge base (Ollama or Claude per user toggle)
                string? modelOverride = useClaude ? (_configuration["RAG:ClaudeModel"]?.Trim() ?? "claude-opus-4-6") : null;
                _logger.LogInformation("Step 2: Querying RAG database for medical information (UseClaude: {UseClaude})...", useClaude);
                LLMResponse ragResponse;
                try
                {
                    ragResponse = await _llmService.GetResponseAsync(transcript, modelOverride);
                    _logger.LogInformation("RAG database returned response with {SourceCount} medical source references", 
                        ragResponse.Sources?.Count ?? 0);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting RAG response");
                    return StatusCode(500, new
                    {
                        success = false,
                        error = "Failed to get response from knowledge base",
                        message = ex.Message,
                        transcript = transcript
                    });
                }

                // When RAG is offline with no real answer, use short fallback; when Ollama returned content, use it
                const string genericOffline = "I'm sorry, my reference services are offline right now. Please try again later.";
                var responseTextForStart = RemoveSourcesSection(ragResponse.Response ?? string.Empty);
                bool hasRealContent = ragResponse.IsFallback
                    && !string.IsNullOrWhiteSpace(ragResponse.Response)
                    && !string.Equals(ragResponse.Response.Trim(), genericOffline, StringComparison.OrdinalIgnoreCase);
                var textToSend = (ragResponse.IsFallback && !hasRealContent)
                    ? "I'm Dr. Dexter. My reference database is temporarily unavailable. You can still see me here—please try again in a moment."
                    : responseTextForStart;

                if (ragResponse.IsFallback && !hasRealContent)
                {
                    _logger.LogWarning("RAG service is offline; sending fallback message to HeyGen so avatar still appears.");
                }
                else if (hasRealContent)
                {
                    _logger.LogInformation("Using Ollama fallback response for avatar (RAG HTTP was unavailable).");
                }

                // Step 3: Start the session if not already started (skip if we've already started this session to avoid HeyGen 400)
                if (!_sessionsStarted.ContainsKey(conversationId))
                {
                    try
                    {
                        await _heyGenService.StartStreamingSessionAsync(conversationId, streamingToken);
                        _sessionsStarted.TryAdd(conversationId, 0);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error starting streaming session (may already be started): {Message}", ex.Message);
                        // Continue - session might already be started
                    }
                }

                // Step 4: Send RAG response to HeyGen for avatar speech
                try
                {
                    // CRITICAL: Verify streaming token is present
                    if (string.IsNullOrWhiteSpace(streamingToken))
                    {
                        _logger.LogError("CRITICAL: Streaming token is missing! Session ID: {SessionId}. " +
                            "The token from session creation must be reused for streaming.task.", conversationId);
                        return StatusCode(500, new
                        {
                            success = false,
                            error = "Streaming token is required - session may have been created incorrectly"
                        });
                    }
                    
                    _logger.LogInformation("Using streaming token from session creation (token length: {Length}, session: {SessionId})", 
                        streamingToken.Length, conversationId);

                    if (string.IsNullOrWhiteSpace(textToSend))
                    {
                        _logger.LogError("Text to send to HeyGen is empty after removing sources section!");
                        return StatusCode(500, new
                        {
                            success = false,
                            error = "Response text is empty after processing",
                            transcript = "No response generated"
                        });
                    }
                    
                    var textForAvatar = TruncateForAvatar(textToSend);
                    if (textToSend.Length > textForAvatar.Length)
                        _logger.LogInformation("Truncated avatar speech from {Original} to {Truncated} chars to reduce mid-sentence disconnects", textToSend.Length, textForAvatar.Length);

                    if (_heyGenService.DeliversSpeechViaServer)
                    {
                        _logger.LogInformation("Step 3: Sending to HeyGen avatar (length: {Length} chars)", textForAvatar.Length);
                        _logger.LogInformation("Text being sent to HeyGen (first 200 chars): {TextPreview}",
                            textForAvatar.Substring(0, Math.Min(200, textForAvatar.Length)));
                        await _heyGenService.SendStreamingTaskAsync(conversationId, textForAvatar, streamingToken);
                        _logger.LogInformation("Successfully sent {Length} characters to HeyGen for avatar speech", textForAvatar.Length);
                    }
                    else
                    {
                        _logger.LogInformation("LiveAvatar LITE: browser will publish speak_text, length {Length}", textForAvatar.Length);
                        await _heyGenService.SendStreamingTaskAsync(conversationId, textForAvatar, streamingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending task to HeyGen: {Message}", ex.Message);
                    // Still return RAG response even if HeyGen fails
                    return StatusCode(500, new
                    {
                        success = false,
                        error = "Failed to send message to avatar, but received response from knowledge base",
                        message = ex.Message,
                        transcript = textToSend,
                        sources = (ragResponse.Sources ?? new List<SourceReference>()).Select(s => new ChatSourceLink
                        {
                            Title = string.IsNullOrWhiteSpace(s.Title)
                                ? Path.GetFileName(s.Filename) ?? "Source"
                                : s.Title,
                            Preview = s.Preview,
                            Similarity = s.Similarity,
                            Url = BuildSourceUrl(s.Filename)
                        })
                        .Where(link => !string.IsNullOrWhiteSpace(link.Url))
                        .ToList(),
                        isFallback = false
                    });
                }

                // Step 5: Return success with transcript and sources
                var sourceLinks = (ragResponse.Sources ?? new List<SourceReference>()).Select(s =>
                {
                    var url = BuildSourceUrl(s.Filename);
                    var link = new ChatSourceLink
                    {
                        Title = string.IsNullOrWhiteSpace(s.Title)
                            ? Path.GetFileName(s.Filename) ?? "Source"
                            : s.Title,
                        Preview = s.Preview,
                        Similarity = s.Similarity,
                        Url = url
                    };
                    
                    // Log source link creation for debugging
                    _logger.LogInformation("Source link created: Title={Title}, HasUrl={HasUrl}, Filename={Filename}",
                        link.Title, !string.IsNullOrWhiteSpace(url), s.Filename);
                    
                    return link;
                })
                .ToList();
                
                _logger.LogInformation("Returning {Count} source links (filtered from {Total} sources)",
                    sourceLinks.Count(s => !string.IsNullOrWhiteSpace(s.Url)),
                    sourceLinks.Count);
                
                return Ok(new
                {
                    success = true,
                    userTranscript = transcript,
                    avatarTranscript = textToSend,
                    speechForAvatar = !_heyGenService.DeliversSpeechViaServer ? TruncateForAvatar(textToSend) : null,
                    sources = sourceLinks.Where(link => !string.IsNullOrWhiteSpace(link.Url)).ToList(),
                    isFallback = ragResponse.IsFallback
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing audio: {Message}\n{StackTrace}", ex.Message, ex.StackTrace);
                return StatusCode(500, new
                {
                    success = false,
                    error = "Failed to process audio",
                    message = ex.Message
                });
            }
        }

        /// <summary>
        /// Send a message to the avatar (text-based, for backward compatibility)
        /// Gets RAG response first, then sends to HeyGen for avatar speech
        /// </summary>
        [HttpPost("task")]
        public async Task<IActionResult> SendTask([FromBody] AvatarMessageRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Message))
                {
                    return BadRequest(new { error = "Message cannot be empty" });
                }

                if (string.IsNullOrWhiteSpace(request.ConversationId))
                {
                    return BadRequest(new { error = "Session ID (conversationId) is required" });
                }

                _logger.LogInformation("Processing message for session {SessionId}: {UserQuestion}",
                    request.ConversationId, request.Message.Substring(0, Math.Min(100, request.Message.Length)));

                var history = _avatarHistory.GetOrAdd(request.ConversationId, _ => new List<ConversationMessage>());
                List<ConversationMessage> historySnapshot;
                lock (history)
                {
                    history.Add(new ConversationMessage { Role = "user", Content = request.Message });
                    historySnapshot = history.ToList();
                }

                // Step 1: Get RAG response (scripted greeting, or LLM with conversation context)
                LLMResponse ragResponse;
                if (IsInitialGreeting(request.Message))
                {
                    _logger.LogInformation("Using scripted greeting for initial Dr. Dexter intro.");
                    ragResponse = new LLMResponse
                    {
                        Response = ScriptedDrDexterGreeting,
                        Sources = new List<SourceReference>(),
                        IsFallback = false
                    };
                }
                else
                {
                    var promptWithContext = BuildPromptWithAvatarContext(request.Message, historySnapshot);
                    string? modelOverride = request.UseClaude ? (_configuration["RAG:ClaudeModel"]?.Trim() ?? "claude-opus-4-6") : null;
                    _logger.LogInformation("Querying RAG database for medical information (with conversation context, UseClaude: {UseClaude})...", request.UseClaude);
                    ragResponse = await _llmService.GetResponseAsync(promptWithContext, modelOverride);
                }

                var responseText = RemoveSourcesSection(ragResponse.Response ?? string.Empty);

                _logger.LogInformation("RAG database returned response with {SourceCount} medical source references", 
                    ragResponse.Sources?.Count ?? 0);

                // When RAG is offline with no real answer, use short fallback; when Ollama returned content, use it
                const string genericOfflineMessage = "I'm sorry, my reference services are offline right now. Please try again later.";
                bool hasRealFallbackContent = ragResponse.IsFallback
                    && !string.IsNullOrWhiteSpace(ragResponse.Response)
                    && !string.Equals(ragResponse.Response.Trim(), genericOfflineMessage, StringComparison.OrdinalIgnoreCase);
                var textToSendTask = (ragResponse.IsFallback && !hasRealFallbackContent)
                    ? "I'm Dr. Dexter. My reference database is temporarily unavailable. You can still see me here—please try again in a moment."
                    : responseText;

                lock (history)
                {
                    history.Add(new ConversationMessage { Role = "assistant", Content = textToSendTask });
                }

                if (ragResponse.IsFallback && !hasRealFallbackContent)
                {
                    _logger.LogWarning("RAG service is offline; sending fallback message to HeyGen so avatar still appears.");
                }
                else if (hasRealFallbackContent)
                {
                    _logger.LogInformation("Using Ollama fallback response for avatar (RAG HTTP was unavailable).");
                }

                // Step 2: Start the session if not already started (skip if already started to avoid HeyGen 400)
                if (!_sessionsStarted.ContainsKey(request.ConversationId))
                {
                    try
                    {
                        await _heyGenService.StartStreamingSessionAsync(request.ConversationId, request.StreamingToken);
                        _sessionsStarted.TryAdd(request.ConversationId, 0);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error starting streaming session (may already be started): {Message}", ex.Message);
                    }
                }

                // Step 3: Send to HeyGen for avatar speech (uses textToSendTask: RAG response or fallback)
                try
                {
                    if (string.IsNullOrWhiteSpace(textToSendTask))
                    {
                        _logger.LogError("Text to send to HeyGen is empty.");
                        return StatusCode(500, new
                        {
                            success = false,
                            error = "Response text is empty after processing",
                            transcript = "No response generated"
                        });
                    }
                    
                    if (string.IsNullOrWhiteSpace(request.StreamingToken))
                    {
                        _logger.LogError("CRITICAL: Streaming token is missing from request! Session ID: {SessionId}. " +
                            "The token from session creation must be reused for streaming.task.", request.ConversationId);
                        return StatusCode(500, new
                        {
                            success = false,
                            error = "Streaming token is required - ensure frontend sends streamingToken from session creation"
                        });
                    }
                    
                    var textForAvatarTask = TruncateForAvatar(textToSendTask);
                    if (textToSendTask.Length > textForAvatarTask.Length)
                        _logger.LogInformation("Truncated avatar speech from {Original} to {Truncated} chars to reduce mid-sentence disconnects", textToSendTask.Length, textForAvatarTask.Length);

                    if (_heyGenService.DeliversSpeechViaServer)
                    {
                        _logger.LogInformation("Sending to HeyGen (length: {Length} chars, IsFallback: {IsFallback})",
                            textForAvatarTask.Length, ragResponse.IsFallback);
                        await _heyGenService.SendStreamingTaskAsync(request.ConversationId, textForAvatarTask, request.StreamingToken);
                        _logger.LogInformation("Successfully sent {Length} characters to HeyGen for avatar speech", textForAvatarTask.Length);
                    }
                    else
                    {
                        _logger.LogInformation("LiveAvatar LITE: browser speak_text, length {Length} chars", textForAvatarTask.Length);
                        await _heyGenService.SendStreamingTaskAsync(request.ConversationId, textForAvatarTask, request.StreamingToken);
                    }
                }
                catch (Exception ex)
                {
                    bool sessionExpired = ex.Message.IndexOf("session state: closed", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (sessionExpired)
                        _logger.LogWarning("HeyGen session closed (response took too long); returning transcript so user can still see the answer.");
                    else
                        _logger.LogError(ex, "Error sending task to HeyGen: {Message}\n{StackTrace}", ex.Message, ex.StackTrace);
                    var links = (ragResponse.Sources ?? new List<SourceReference>()).Select(s => new ChatSourceLink
                    {
                        Title = string.IsNullOrWhiteSpace(s.Title)
                            ? Path.GetFileName(s.Filename) ?? "Source"
                            : s.Title,
                        Preview = s.Preview,
                        Similarity = s.Similarity,
                        Url = BuildSourceUrl(s.Filename)
                    }).Where(link => !string.IsNullOrWhiteSpace(link.Url)).ToList();
                    // When session expired/closed, return 200 with transcript so the user still sees the answer; frontend can show "Session expired, click Start Session"
                    if (sessionExpired)
                    {
                        return Ok(new
                        {
                            success = true,
                            transcript = textToSendTask,
                            speechForAvatar = !_heyGenService.DeliversSpeechViaServer ? TruncateForAvatar(textToSendTask) : null,
                            sources = links,
                            isFallback = ragResponse.IsFallback,
                            avatarDeliveryFailed = true,
                            sessionExpired = true
                        });
                    }
                    return StatusCode(500, new
                    {
                        success = false,
                        error = "Failed to send message to avatar, but received response from knowledge base",
                        message = ex.Message,
                        transcript = textToSendTask,
                        speechForAvatar = !_heyGenService.DeliversSpeechViaServer ? TruncateForAvatar(textToSendTask) : null,
                        sources = links,
                        isFallback = ragResponse.IsFallback
                    });
                }

                // Step 4: Return success with RAG response
                var sourceLinks = (ragResponse.Sources ?? new List<SourceReference>()).Select(s =>
                {
                    var url = BuildSourceUrl(s.Filename);
                    var link = new ChatSourceLink
                    {
                        Title = string.IsNullOrWhiteSpace(s.Title)
                            ? Path.GetFileName(s.Filename) ?? "Source"
                            : s.Title,
                        Preview = s.Preview,
                        Similarity = s.Similarity,
                        Url = url
                    };
                    
                    _logger.LogInformation("Source link created: Title={Title}, HasUrl={HasUrl}, Filename={Filename}",
                        link.Title, !string.IsNullOrWhiteSpace(url), s.Filename);
                    
                    return link;
                })
                .ToList();
                
                _logger.LogInformation("Returning {Count} source links (filtered from {Total} sources)",
                    sourceLinks.Count(s => !string.IsNullOrWhiteSpace(s.Url)),
                    sourceLinks.Count);
                
                return Ok(new
                {
                    success = true,
                    transcript = textToSendTask,
                    speechForAvatar = !_heyGenService.DeliversSpeechViaServer ? TruncateForAvatar(textToSendTask) : null,
                    sources = sourceLinks.Where(link => !string.IsNullOrWhiteSpace(link.Url)).ToList(),
                    isFallback = ragResponse.IsFallback
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing streaming task: {Message}\n{StackTrace}", ex.Message, ex.StackTrace);
                return StatusCode(500, new
                {
                    success = false,
                    error = "Failed to process message",
                    message = ex.Message
                });
            }
        }

        /// <summary>
        /// Removes the sources section from the response text before sending to HeyGen
        /// The avatar should only speak the actual response, not the sources list
        /// </summary>
        private string RemoveSourcesSection(string response)
        {
            if (string.IsNullOrEmpty(response))
                return response;

            // Use regex to find and remove sources section
            var sourcesPattern = @"(\n\s*)?📚\s*Sources:?\s*\n?(\s*[•\-\*]\s+[^\n]+(\n|$))*.+$";
            var match = System.Text.RegularExpressions.Regex.Match(response, sourcesPattern, 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | 
                System.Text.RegularExpressions.RegexOptions.Multiline | 
                System.Text.RegularExpressions.RegexOptions.Singleline);
            
            string cleaned;
            if (match.Success)
            {
                cleaned = response.Substring(0, match.Index).TrimEnd();
                _logger.LogDebug("Removed sources section from response before sending to HeyGen");
            }
            else
            {
                // Try simpler patterns if regex didn't catch it
                var sourceMarkers = new[]
                {
                    "\n📚 Sources:\n",
                    "\n📚 Sources\n",
                    "\n📚 Sources:",
                    "📚 Sources:\n",
                    "📚 Sources:",
                    "\nSources:\n",
                    "\nSources\n",
                    "\nSources:",
                    "Sources:\n",
                    "Sources:"
                };

                cleaned = response;
                foreach (var marker in sourceMarkers)
                {
                    var index = response.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0)
                    {
                        cleaned = response.Substring(0, index).TrimEnd();
                        _logger.LogDebug("Removed sources section using marker '{Marker}'", marker);
                        break;
                    }
                }
            }

            // Final cleanup - remove any trailing whitespace
            cleaned = cleaned.TrimEnd('\n', '\r', ' ', '\t');
            
            return cleaned;
        }

        private string BuildSourceUrl(string? sourceFilename)
        {
            if (string.IsNullOrWhiteSpace(sourceFilename))
            {
                _logger.LogDebug("BuildSourceUrl: sourceFilename is null or empty");
                return string.Empty;
            }

            var safeFileName = Path.GetFileName(sourceFilename);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                _logger.LogDebug("BuildSourceUrl: safeFileName is null or empty for {SourceFilename}", sourceFilename);
                return string.Empty;
            }

            var filePath = Path.Combine(_sourceDocumentsPath, safeFileName);
            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("BuildSourceUrl: File not found at {FilePath}, but still creating URL for source: {SafeFileName}", 
                    filePath, safeFileName);
                // Still create the URL even if file doesn't exist - the file might be in a different location
                // or the URL might work anyway
            }

            var url = Url.Action("GetSourceFile", "AvatarStreaming", new { filename = safeFileName });
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning("BuildSourceUrl: Url.Action returned null for filename: {SafeFileName}", safeFileName);
                return string.Empty;
            }
            
            _logger.LogDebug("BuildSourceUrl: Created URL {Url} for filename: {SafeFileName}", url, safeFileName);
            return url;
        }

        /// <summary>
        /// Serve a source document PDF by filename (from RAG references). Used for source links in the avatar UI.
        /// </summary>
        [HttpGet("source-file")]
        public IActionResult GetSourceFile([FromQuery] string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
            {
                return BadRequest(new { error = "Filename is required" });
            }
            var safeFileName = Path.GetFileName(filename);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                return BadRequest(new { error = "Invalid filename" });
            }
            var filePath = Path.Combine(_sourceDocumentsPath, safeFileName);
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound(new { error = "File not found", filename = safeFileName });
            }
            var contentType = "application/pdf";
            if (safeFileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                contentType = "text/plain";
            return PhysicalFile(filePath, contentType, safeFileName);
        }

        /// <summary>
        /// Keep-alive: HeyGen uses streaming.start; LiveAvatar uses POST /v1/sessions/keep-alive.
        /// Frontend should call this periodically (e.g. every 45s) while the session is active.
        /// </summary>
        [HttpPost("session/keepalive")]
        public async Task<IActionResult> KeepAlive([FromBody] AvatarSessionRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.SessionId))
                {
                    return BadRequest(new { error = "Session ID is required" });
                }
                if (string.IsNullOrWhiteSpace(request.StreamingToken))
                {
                    return BadRequest(new { error = "Streaming token is required for keepalive" });
                }
                await _heyGenService.StartStreamingSessionAsync(request.SessionId, request.StreamingToken);
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Keepalive failed for session {SessionId} (session may already be closed)", request.SessionId);
                return Ok(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Stop a streaming session
        /// </summary>
        [HttpPost("session/stop")]
        public async Task<IActionResult> StopSession([FromBody] AvatarSessionRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.SessionId))
                {
                    return BadRequest(new { error = "Session ID is required" });
                }

                _logger.LogInformation("Stopping streaming session: {SessionId}", request.SessionId);

                await _heyGenService.StopStreamingSessionAsync(request.SessionId, request.StreamingToken);
                _sessionsStarted.TryRemove(request.SessionId, out _);

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping streaming session");
                return StatusCode(500, new
                {
                    success = false,
                    error = "Failed to stop streaming session",
                    message = ex.Message
                });
            }
        }
    }
}

