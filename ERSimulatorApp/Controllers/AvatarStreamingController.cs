using ERSimulatorApp.Models;
using ERSimulatorApp.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using System.IO;

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
        private readonly IWhisperService _whisperService;
        private readonly ILogger<AvatarStreamingController> _logger;
        private readonly string _sourceDocumentsPath;

        public AvatarStreamingController(
            IHeyGenStreamingService heyGenService,
            ILLMService llmService,
            IWhisperService whisperService,
            ILogger<AvatarStreamingController> logger,
            IConfiguration configuration,
            IWebHostEnvironment environment)
        {
            _heyGenService = heyGenService;
            _llmService = llmService;
            _whisperService = whisperService;
            _logger = logger;

            var configuredPath = configuration["RAG:SourceDocumentsPath"];
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                _sourceDocumentsPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "rag-local", "sample_data"));
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
                _logger.LogInformation("Creating new HeyGen streaming session");

                var sessionData = await _heyGenService.CreateStreamingSessionAsync();

                return Ok(new
                {
                    success = true,
                    sessionId = sessionData.SessionId,
                    url = sessionData.Url,
                    accessToken = sessionData.AccessToken,
                    streamingToken = sessionData.StreamingToken // Include token for reuse
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
        /// Process audio input: Whisper ASR → RAG → HeyGen
        /// Complete voice conversation flow
        /// </summary>
        [HttpPost("audio")]
        public async Task<IActionResult> ProcessAudio([FromForm] string conversationId, [FromForm] string? streamingToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(conversationId))
                {
                    return BadRequest(new { error = "Conversation ID is required" });
                }

                // Check if request contains audio file
                if (!Request.HasFormContentType || Request.Form.Files.Count == 0)
                {
                    return BadRequest(new { error = "Audio file is required" });
                }

                var audioFile = Request.Form.Files[0];
                if (audioFile == null || audioFile.Length == 0)
                {
                    return BadRequest(new { error = "Audio file is empty" });
                }

                _logger.LogInformation("Processing audio input for session {SessionId}, file: {FileName}, size: {Size} bytes, content type: {ContentType}", 
                    conversationId, audioFile.FileName, audioFile.Length, audioFile.ContentType);

                // Validate file size (minimum 1KB, maximum 25MB for Whisper API)
                if (audioFile.Length < 1024)
                {
                    _logger.LogWarning("Audio file is too small: {Size} bytes", audioFile.Length);
                    return BadRequest(new { error = "Audio recording is too short. Please record for at least 1-2 seconds." });
                }

                if (audioFile.Length > 25 * 1024 * 1024) // 25MB limit
                {
                    _logger.LogWarning("Audio file is too large: {Size} bytes", audioFile.Length);
                    return BadRequest(new { error = "Audio file is too large. Maximum size is 25MB." });
                }

                // Step 1: Transcribe audio using Whisper ASR
                _logger.LogInformation("Step 1: Transcribing audio with Whisper...");
                string transcript;
                try
                {
                    using var audioStream = audioFile.OpenReadStream();
                    transcript = await _whisperService.TranscribeAudioAsync(audioStream, audioFile.FileName);
                    
                    if (string.IsNullOrWhiteSpace(transcript))
                    {
                        _logger.LogWarning("Whisper returned empty transcript for file: {FileName}, size: {Size} bytes", 
                            audioFile.FileName, audioFile.Length);
                        return BadRequest(new { error = "Could not transcribe audio. The recording may be too quiet, too short, or contain no speech. Please try again." });
                    }

                    _logger.LogInformation("Audio transcribed successfully: {Transcript}", transcript);
                }
                catch (HttpRequestException httpEx)
                {
                    _logger.LogError(httpEx, "HTTP error transcribing audio with Whisper: {Message}", httpEx.Message);
                    return StatusCode(500, new
                    {
                        success = false,
                        error = "Failed to transcribe audio",
                        message = httpEx.Message
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error transcribing audio with Whisper: {Message}\n{StackTrace}", ex.Message, ex.StackTrace);
                    return StatusCode(500, new
                    {
                        success = false,
                        error = "Failed to transcribe audio",
                        message = ex.Message
                    });
                }

                // Step 2: Get RAG response from medical knowledge base
                _logger.LogInformation("Step 2: Querying RAG database for medical information...");
                LLMResponse ragResponse;
                try
                {
                    ragResponse = await _llmService.GetResponseAsync(transcript);
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

                if (ragResponse.IsFallback)
                {
                    _logger.LogWarning("RAG service is offline");
                    return StatusCode(503, new
                    {
                        success = false,
                        error = "Reference services are offline",
                        transcript = "I'm sorry, my reference services are offline right now. Please try again later.",
                        sources = new List<ChatSourceLink>(),
                        isFallback = true
                    });
                }

                // Step 3: Start the session if not already started
                try
                {
                    await _heyGenService.StartStreamingSessionAsync(conversationId, streamingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error starting streaming session (may already be started): {Message}", ex.Message);
                    // Continue - session might already be started
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

                    // Remove sources section from response before sending to HeyGen
                    var textToSend = RemoveSourcesSection(ragResponse.Response ?? string.Empty);
                    
                    if (string.IsNullOrWhiteSpace(textToSend))
                    {
                        _logger.LogError("Text to send to HeyGen is empty after removing sources section!");
                        return StatusCode(500, new
                        {
                            success = false,
                            error = "Response text is empty after processing",
                            transcript = ragResponse.Response ?? "No response generated"
                        });
                    }
                    
                    _logger.LogInformation("Step 3: Sending to HeyGen avatar (length: {Length} chars)", textToSend.Length);
                    _logger.LogInformation("Text being sent to HeyGen (first 200 chars): {TextPreview}", 
                        textToSend.Substring(0, Math.Min(200, textToSend.Length)));
                    
                    // CRITICAL: Only send the RAG response text - do NOT send user question or conversation history
                    // Send to HeyGen - this will handle TTS and streaming back to client
                    await _heyGenService.SendStreamingTaskAsync(conversationId, textToSend, streamingToken);
                    
                    _logger.LogInformation("Successfully sent {Length} characters to HeyGen for avatar speech", textToSend.Length);
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
                        transcript = ragResponse.Response,
                    sources = new List<ChatSourceLink>(),
                        isFallback = false
                    });
                }

                // Step 5: Return success with transcript
                return Ok(new
                {
                    success = true,
                    userTranscript = transcript,
                    avatarTranscript = ragResponse.Response,
                    sources = new List<ChatSourceLink>(),
                    isFallback = false
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

                // Step 1: Get RAG response from medical knowledge base
                // This queries the RAG database for medical information related to the user's question
                _logger.LogInformation("Querying RAG database for medical information...");
                var ragResponse = await _llmService.GetResponseAsync(request.Message);
                
                _logger.LogInformation("RAG database returned response with {SourceCount} medical source references", 
                    ragResponse.Sources?.Count ?? 0);

                if (ragResponse.IsFallback)
                {
                    _logger.LogWarning("RAG service is offline");
                    return StatusCode(503, new
                    {
                        success = false,
                        error = "Reference services are offline",
                        transcript = "I'm sorry, my reference services are offline right now. Please try again later.",
                        sources = new List<ChatSourceLink>(),
                        isFallback = true
                    });
                }

                // Step 2: Start the session if not already started (required before sending tasks)
                try
                {
                    await _heyGenService.StartStreamingSessionAsync(request.ConversationId, request.StreamingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error starting streaming session (may already be started): {Message}", ex.Message);
                    // Continue - session might already be started
                }

                // Step 3: Send to HeyGen for avatar speech
                try
                {
                    // Log the full RAG+personality response before processing
                    _logger.LogInformation("Full RAG+Personality response received (length: {Length} chars): {FullResponse}", 
                        ragResponse.Response?.Length ?? 0, 
                        ragResponse.Response?.Substring(0, Math.Min(500, ragResponse.Response.Length)) ?? "NULL");
                    
                    // Remove sources section from response before sending to HeyGen
                    // The avatar should only speak the actual response, not the sources list
                    var textToSend = RemoveSourcesSection(ragResponse.Response ?? string.Empty);
                    
                    // Validate that we have text to send
                    if (string.IsNullOrWhiteSpace(textToSend))
                    {
                        _logger.LogError("Text to send to HeyGen is empty after removing sources section! Original response length: {Length}", 
                            ragResponse.Response?.Length ?? 0);
                        _logger.LogError("Original response was: {OriginalResponse}", ragResponse.Response ?? "NULL");
                        return StatusCode(500, new
                        {
                            success = false,
                            error = "Response text is empty after processing",
                            message = "The RAG+personality response was empty or contained only sources",
                            transcript = ragResponse.Response ?? "No response generated"
                        });
                    }
                    
                    // CRITICAL: Verify streaming token is present
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
                    
                    _logger.LogInformation("Using streaming token from request (token length: {Length}, session: {SessionId})", 
                        request.StreamingToken.Length, request.ConversationId);
                    
                    // Log what we're sending to HeyGen for debugging
                    _logger.LogInformation("Sending to HeyGen avatar as medical instructor (length: {Length} chars, first 200 chars): {TextPreview}", 
                        textToSend.Length, 
                        textToSend.Substring(0, Math.Min(200, textToSend.Length)));
                    
                    // CRITICAL: Use the streaming token from session creation - do NOT generate a new one
                    await _heyGenService.SendStreamingTaskAsync(request.ConversationId, textToSend, request.StreamingToken);
                    
                    _logger.LogInformation("Successfully sent {Length} characters to HeyGen for avatar speech", textToSend.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending task to HeyGen: {Message}\n{StackTrace}", ex.Message, ex.StackTrace);
                    // Still return RAG response even if HeyGen fails
                    return StatusCode(500, new
                    {
                        success = false,
                        error = "Failed to send message to avatar, but received response from knowledge base",
                        message = ex.Message,
                        transcript = ragResponse.Response,
                    sources = new List<ChatSourceLink>(),
                        isFallback = false
                    });
                }

                // Step 3: Return success with RAG response
                return Ok(new
                {
                    success = true,
                    transcript = ragResponse.Response,
                    sources = new List<ChatSourceLink>(),
                    isFallback = false
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
                return string.Empty;
            }

            var safeFileName = Path.GetFileName(sourceFilename);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                return string.Empty;
            }

            var filePath = Path.Combine(_sourceDocumentsPath, safeFileName);
            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogDebug("Skipping source link because file was not found: {File}", filePath);
                return string.Empty;
            }

            return Url.Action("GetSourceFile", "Chat", new { filename = safeFileName }) ?? string.Empty;
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

