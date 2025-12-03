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

                // Validate audio file size (minimum 1KB, maximum 25MB - Whisper API limit)
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

                // Step 1: Transcribe audio using Whisper ASR
                _logger.LogInformation("Step 1: Transcribing audio with Whisper... File: {FileName}, Size: {Size} bytes, ContentType: {ContentType}",
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
                    
                    _logger.LogInformation("Step 1b: Audio stream opened successfully, calling WhisperService.TranscribeAudioAsync with fileName: {FileName}...", fileName);
                    transcript = await _whisperService.TranscribeAudioAsync(audioStream, fileName);
                    
                    if (string.IsNullOrWhiteSpace(transcript))
                    {
                        _logger.LogWarning("Whisper returned empty transcript - audio may be too short, unclear, or in unsupported format");
                        return BadRequest(new { 
                            error = "Could not transcribe audio. The audio may be too short, unclear, or in an unsupported format. Please try recording again.",
                            details = "Whisper API returned empty transcript"
                        });
                    }

                    _logger.LogInformation("Step 1c: Audio transcribed successfully. Transcript length: {Length} chars, Preview: {Preview}",
                        transcript.Length, transcript.Substring(0, Math.Min(100, transcript.Length)));
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("API key") || ex.Message.Contains("invalid") || ex.Message.Contains("expired"))
                {
                    _logger.LogError(ex, "Whisper API key error: {Message}", ex.Message);
                    return StatusCode(500, new
                    {
                        success = false,
                        error = "OpenAI API key error. Please check your API key configuration.",
                        message = ex.Message,
                        details = "The Whisper API key may be invalid, expired, or not have access to the Whisper API."
                    });
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogError(ex, "Whisper API authentication failed: {Message}", ex.Message);
                    return StatusCode(401, new
                    {
                        success = false,
                        error = "OpenAI API authentication failed. Please verify your API key.",
                        message = ex.Message,
                        details = "The API key may not have access to the Whisper API or may be incorrect."
                    });
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "Whisper API HTTP error: {Message}", ex.Message);
                    return StatusCode(500, new
                    {
                        success = false,
                        error = "Failed to connect to Whisper API. Please check your internet connection and try again.",
                        message = ex.Message,
                        details = "Network error or Whisper API service issue."
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error transcribing audio with Whisper: {Message}\n{StackTrace}", ex.Message, ex.StackTrace);
                    return StatusCode(500, new
                    {
                        success = false,
                        error = "Failed to transcribe audio",
                        message = ex.Message,
                        details = $"Error type: {ex.GetType().Name}. Check application logs for more details."
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
                    avatarTranscript = ragResponse.Response,
                    sources = sourceLinks.Where(link => !string.IsNullOrWhiteSpace(link.Url)).ToList(),
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

                // Step 3: Return success with RAG response
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
                    transcript = ragResponse.Response,
                    sources = sourceLinks.Where(link => !string.IsNullOrWhiteSpace(link.Url)).ToList(),
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

            var url = Url.Action("GetSourceFile", "Chat", new { filename = safeFileName });
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning("BuildSourceUrl: Url.Action returned null for filename: {SafeFileName}", safeFileName);
                return string.Empty;
            }
            
            _logger.LogDebug("BuildSourceUrl: Created URL {Url} for filename: {SafeFileName}", url, safeFileName);
            return url;
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

