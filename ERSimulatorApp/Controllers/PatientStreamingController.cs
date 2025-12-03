using ERSimulatorApp.Models;
using ERSimulatorApp.Services;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;

namespace ERSimulatorApp.Controllers
{
    /// <summary>
    /// API controller for Patient Avatar HeyGen Streaming integration
    /// Uses OpenAI for patient personality (Option 2) - no RAG needed
    /// Tracks conversation state and emotional escalation/de-escalation
    /// </summary>
    [ApiController]
    [Route("api/patient/v2/streaming")]
    public class PatientStreamingController : ControllerBase
    {
        private readonly IPatientStreamingService _patientStreamingService;
        private readonly IPatientPersonalityService _patientPersonalityService;
        private readonly IWhisperService _whisperService;
        private readonly ILogger<PatientStreamingController> _logger;
        
        // In-memory conversation state tracking (keyed by session ID)
        private static readonly ConcurrentDictionary<string, PatientConversationState> _conversationStates = new();

        public PatientStreamingController(
            IPatientStreamingService patientStreamingService,
            IPatientPersonalityService patientPersonalityService,
            IWhisperService whisperService,
            ILogger<PatientStreamingController> logger)
        {
            _patientStreamingService = patientStreamingService;
            _patientPersonalityService = patientPersonalityService;
            _whisperService = whisperService;
            _logger = logger;
        }

        /// <summary>
        /// Create a new patient streaming session
        /// Returns LiveKit connection details
        /// </summary>
        [HttpPost("session/create")]
        public async Task<IActionResult> CreateSession()
        {
            try
            {
                _logger.LogInformation("Creating new patient HeyGen streaming session");

                var sessionData = await _patientStreamingService.CreateStreamingSessionAsync();

                // Initialize conversation state for this session
                var conversationState = new PatientConversationState
                {
                    SessionId = sessionData.SessionId,
                    EmotionalState = PatientEmotionalState.Neutral, // Start neutral/anxious
                    CreatedAt = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow
                };
                _conversationStates[sessionData.SessionId] = conversationState;

                _logger.LogInformation("Initialized conversation state for patient session {SessionId} with emotional state: {State}", 
                    sessionData.SessionId, conversationState.EmotionalState);

                return Ok(new
                {
                    success = true,
                    sessionId = sessionData.SessionId,
                    url = sessionData.Url,
                    accessToken = sessionData.AccessToken,
                    streamingToken = sessionData.StreamingToken
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating patient streaming session");
                return StatusCode(500, new
                {
                    success = false,
                    error = "Failed to create patient streaming session",
                    message = ex.Message
                });
            }
        }

        /// <summary>
        /// Process audio input: Whisper ASR → Patient Personality → HeyGen
        /// </summary>
        [HttpPost("audio")]
        public async Task<IActionResult> ProcessAudio([FromForm] string conversationId, [FromForm] string? streamingToken)
        {
            try
            {
                _logger.LogInformation("ProcessAudio called for patient - Files.Count: {FileCount}", Request.Form.Files.Count);

                if (string.IsNullOrWhiteSpace(conversationId))
                {
                    return BadRequest(new { error = "Conversation ID is required" });
                }

                if (!Request.HasFormContentType || Request.Form.Files.Count == 0)
                {
                    return BadRequest(new { error = "Audio file is required" });
                }

                var audioFile = Request.Form.Files["audio"] ?? Request.Form.Files[0];
                if (audioFile == null || audioFile.Length == 0)
                {
                    return BadRequest(new { error = "Audio file is empty" });
                }

                if (audioFile.Length < 1024)
                {
                    return BadRequest(new { error = "Audio file is too small. Please record at least 1 second of audio." });
                }
                if (audioFile.Length > 25 * 1024 * 1024)
                {
                    return BadRequest(new { error = "Audio file is too large. Maximum size is 25MB." });
                }

                if (string.IsNullOrWhiteSpace(streamingToken))
                {
                    return BadRequest(new { error = "Streaming token is required" });
                }

                // Step 1: Transcribe audio using Whisper
                string transcript;
                try
                {
                    var fileName = audioFile.FileName;
                    if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
                    {
                        fileName = "audio.webm";
                    }

                    using var audioStream = audioFile.OpenReadStream();
                    transcript = await _whisperService.TranscribeAudioAsync(audioStream, fileName);

                    if (string.IsNullOrWhiteSpace(transcript))
                    {
                        return BadRequest(new { error = "Could not transcribe audio. Please try recording again." });
                    }

                    _logger.LogInformation("Patient audio transcribed: {TranscriptPreview}...", 
                        transcript.Substring(0, Math.Min(100, transcript.Length)));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error transcribing patient audio");
                    return StatusCode(500, new { error = "Failed to transcribe audio", message = ex.Message });
                }

                // Step 2: Get conversation state and update it
                var conversationState = _conversationStates.GetOrAdd(conversationId, 
                    new PatientConversationState 
                    { 
                        SessionId = conversationId, 
                        EmotionalState = PatientEmotionalState.Neutral 
                    });

                // Add user message to history
                conversationState.History.Add(new ConversationMessage
                {
                    Role = "user",
                    Content = transcript,
                    Timestamp = DateTime.UtcNow
                });

                // Step 3: Get patient response using OpenAI with conversation context
                string patientResponse;
                try
                {
                    patientResponse = await _patientPersonalityService.GetPatientResponseAsync(
                        transcript, 
                        conversationState.History, 
                        conversationState.EmotionalState);
                    
                    _logger.LogInformation("Patient response generated (length: {Length} chars, emotional state: {State})", 
                        patientResponse.Length, conversationState.EmotionalState);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error generating patient response");
                    return StatusCode(500, new { error = "Failed to generate patient response", message = ex.Message });
                }

                // Add patient response to history
                conversationState.History.Add(new ConversationMessage
                {
                    Role = "patient",
                    Content = patientResponse,
                    Timestamp = DateTime.UtcNow
                });

                // Update emotional state based on interaction
                conversationState.EmotionalState = UpdateEmotionalState(
                    conversationState.EmotionalState, 
                    transcript, 
                    patientResponse, 
                    conversationState.History);
                conversationState.LastUpdated = DateTime.UtcNow;

                _logger.LogInformation("Updated patient emotional state to: {NewState} for session {SessionId}", 
                    conversationState.EmotionalState, conversationId);

                // Step 4: Start session if needed
                try
                {
                    await _patientStreamingService.StartStreamingSessionAsync(conversationId, streamingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error starting patient streaming session (may already be started)");
                }

                // Step 5: Send to HeyGen for avatar speech
                try
                {
                    await _patientStreamingService.SendStreamingTaskAsync(conversationId, patientResponse, streamingToken);
                    _logger.LogInformation("Patient response sent to HeyGen successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending patient response to HeyGen");
                    return StatusCode(500, new
                    {
                        success = false,
                        error = "Failed to send message to patient avatar",
                        message = ex.Message,
                        transcript = patientResponse
                    });
                }

                return Ok(new
                {
                    success = true,
                    userTranscript = transcript,
                    patientTranscript = patientResponse
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing patient audio");
                return StatusCode(500, new
                {
                    success = false,
                    error = "Failed to process audio",
                    message = ex.Message
                });
            }
        }

        /// <summary>
        /// Send a text message to the patient avatar
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

                if (string.IsNullOrWhiteSpace(request.StreamingToken))
                {
                    return BadRequest(new { error = "Streaming token is required" });
                }

                _logger.LogInformation("Processing message for patient session {SessionId}", request.ConversationId);

                // Step 1: Get conversation state and update it
                var conversationState = _conversationStates.GetOrAdd(request.ConversationId, 
                    new PatientConversationState 
                    { 
                        SessionId = request.ConversationId, 
                        EmotionalState = PatientEmotionalState.Neutral 
                    });

                // Add user message to history
                conversationState.History.Add(new ConversationMessage
                {
                    Role = "user",
                    Content = request.Message,
                    Timestamp = DateTime.UtcNow
                });

                // Step 2: Get patient response using OpenAI with conversation context
                string patientResponse;
                try
                {
                    patientResponse = await _patientPersonalityService.GetPatientResponseAsync(
                        request.Message, 
                        conversationState.History, 
                        conversationState.EmotionalState);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error generating patient response");
                    return StatusCode(500, new { error = "Failed to generate patient response", message = ex.Message });
                }

                // Add patient response to history
                conversationState.History.Add(new ConversationMessage
                {
                    Role = "patient",
                    Content = patientResponse,
                    Timestamp = DateTime.UtcNow
                });

                // Update emotional state based on interaction
                conversationState.EmotionalState = UpdateEmotionalState(
                    conversationState.EmotionalState, 
                    request.Message, 
                    patientResponse, 
                    conversationState.History);
                conversationState.LastUpdated = DateTime.UtcNow;

                _logger.LogInformation("Updated patient emotional state to: {NewState} for session {SessionId}", 
                    conversationState.EmotionalState, request.ConversationId);

                // Step 3: Start session if needed
                try
                {
                    await _patientStreamingService.StartStreamingSessionAsync(request.ConversationId, request.StreamingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error starting patient streaming session (may already be started)");
                }

                // Step 4: Send to HeyGen
                try
                {
                    await _patientStreamingService.SendStreamingTaskAsync(request.ConversationId, patientResponse, request.StreamingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending patient response to HeyGen");
                    return StatusCode(500, new
                    {
                        success = false,
                        error = "Failed to send message to patient avatar",
                        message = ex.Message,
                        transcript = patientResponse
                    });
                }

                return Ok(new
                {
                    success = true,
                    transcript = patientResponse
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing patient message");
                return StatusCode(500, new
                {
                    success = false,
                    error = "Failed to process message",
                    message = ex.Message
                });
            }
        }

        /// <summary>
        /// Stop a patient streaming session
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

                _logger.LogInformation("Stopping patient streaming session: {SessionId}", request.SessionId);

                await _patientStreamingService.StopStreamingSessionAsync(request.SessionId, request.StreamingToken);

                // Clean up conversation state
                _conversationStates.TryRemove(request.SessionId, out _);
                _logger.LogInformation("Cleaned up conversation state for session {SessionId}", request.SessionId);

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping patient streaming session");
                return StatusCode(500, new
                {
                    success = false,
                    error = "Failed to stop patient streaming session",
                    message = ex.Message
                });
            }
        }

        /// <summary>
        /// Updates the patient's emotional state based on the interaction
        /// </summary>
        private PatientEmotionalState UpdateEmotionalState(
            PatientEmotionalState currentState, 
            string userMessage, 
            string patientResponse, 
            List<ConversationMessage> history)
        {
            var lowerUserMessage = userMessage.ToLower();
            var lowerPatientResponse = patientResponse.ToLower();

            // Check for escalation triggers
            var hasEscalationTriggers = 
                lowerUserMessage.Contains("wait") || 
                lowerUserMessage.Contains("later") || 
                lowerUserMessage.Contains("patience") ||
                lowerUserMessage.Contains("calm down") ||
                (lowerUserMessage.Contains("same") && lowerUserMessage.Contains("question")) ||
                lowerUserMessage.Contains("already asked") ||
                lowerUserMessage.Contains("repeat");

            // Check for de-escalation factors
            var hasDeEscalationFactors =
                lowerUserMessage.Contains("understand") ||
                lowerUserMessage.Contains("sorry") ||
                lowerUserMessage.Contains("apologize") ||
                lowerUserMessage.Contains("help") ||
                lowerUserMessage.Contains("explain") ||
                lowerUserMessage.Contains("listen") ||
                lowerUserMessage.Contains("empathy") ||
                lowerUserMessage.Contains("validate") ||
                lowerUserMessage.Contains("appreciate");

            // Check if patient response shows agitation
            var patientShowsAgitation = 
                lowerPatientResponse.Contains("angry") ||
                lowerPatientResponse.Contains("frustrated") ||
                lowerPatientResponse.Contains("waiting") ||
                lowerPatientResponse.Contains("hours") ||
                lowerPatientResponse.Contains("nobody") ||
                lowerPatientResponse.Contains("helping");

            // Check if patient response shows calming
            var patientShowsCalming =
                lowerPatientResponse.Contains("thank") ||
                lowerPatientResponse.Contains("appreciate") ||
                lowerPatientResponse.Contains("understand") ||
                lowerPatientResponse.Contains("better") ||
                lowerPatientResponse.Contains("relief");

            // Check for repeated questions
            var isRepeatedQuestion = false;
            if (history.Count >= 4)
            {
                var recentUserMessages = history
                    .Where(m => m.Role == "user")
                    .TakeLast(3)
                    .Select(m => m.Content.ToLower())
                    .ToList();
                
                var currentLower = userMessage.ToLower();
                isRepeatedQuestion = recentUserMessages.Any(msg => 
                    msg.Length > 10 && currentLower.Length > 10 && 
                    (msg.Contains(currentLower.Substring(0, Math.Min(20, currentLower.Length))) || 
                     currentLower.Contains(msg.Substring(0, Math.Min(20, msg.Length)))));
            }

            // Update state based on factors
            var newState = currentState;

            // Escalation logic
            if (hasEscalationTriggers || isRepeatedQuestion || patientShowsAgitation)
            {
                if (currentState < PatientEmotionalState.Agitated)
                {
                    newState = currentState + 1; // Escalate one level
                    _logger.LogInformation("Patient emotional state escalating from {OldState} to {NewState}", 
                        currentState, newState);
                }
                else if (currentState < PatientEmotionalState.Angry)
                {
                    newState = PatientEmotionalState.Angry; // Can escalate to angry
                    _logger.LogInformation("Patient emotional state escalating to {NewState}", newState);
                }
            }

            // De-escalation logic (only if provider is being helpful)
            if (hasDeEscalationFactors && !hasEscalationTriggers && !isRepeatedQuestion)
            {
                if (patientShowsCalming && currentState > PatientEmotionalState.Neutral)
                {
                    newState = currentState - 1; // De-escalate one level
                    _logger.LogInformation("Patient emotional state de-escalating from {OldState} to {NewState}", 
                        currentState, newState);
                }
            }

            // Ensure state doesn't go below Calm or above Angry
            if (newState < PatientEmotionalState.Calm) newState = PatientEmotionalState.Calm;
            if (newState > PatientEmotionalState.Angry) newState = PatientEmotionalState.Angry;

            return newState;
        }
    }
}

