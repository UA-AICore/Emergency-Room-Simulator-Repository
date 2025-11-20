using ERSimulatorApp.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ERSimulatorApp.Services
{
    /// <summary>
    /// Clean implementation of HeyGen Streaming API following official documentation
    /// </summary>
    public interface IHeyGenStreamingService
    {
        Task<string> GetStreamingTokenAsync();
        Task<HeyGenStreamingSessionData> CreateStreamingSessionAsync();
        Task StartStreamingSessionAsync(string sessionId, string? streamingToken = null);
        Task SendStreamingTaskAsync(string sessionId, string text, string? streamingToken = null);
        Task StopStreamingSessionAsync(string sessionId, string? streamingToken = null);
    }

    public class HeyGenStreamingService : IHeyGenStreamingService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<HeyGenStreamingService> _logger;
        private readonly string _apiKey;
        private readonly string _apiUrl;
        private readonly string _avatarName;

        public HeyGenStreamingService(
            HttpClient httpClient,
            ILogger<HeyGenStreamingService> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _apiKey = configuration["HeyGen:ApiKey"] ?? throw new InvalidOperationException("HeyGen:ApiKey is required");
            _apiUrl = "https://api.heygen.com/v1/";
            // Use AvatarId from config (stored as _avatarName for API compatibility)
            _avatarName = configuration["HeyGen:AvatarId"] ?? throw new InvalidOperationException("HeyGen:AvatarId is required");

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("HeyGen API key is not configured");
            }
            if (string.IsNullOrWhiteSpace(_avatarName))
            {
                throw new InvalidOperationException("HeyGen Avatar ID is not configured");
            }

            _logger.LogInformation("HeyGen Streaming Service initialized with Avatar: {AvatarName}", _avatarName);
        }

        /// <summary>
        /// Step 1: Get authentication token for streaming API
        /// </summary>
        public async Task<string> GetStreamingTokenAsync()
        {
            try
            {
                _logger.LogInformation("Getting HeyGen streaming token");

                var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}streaming.create_token");
                requestMessage.Headers.Add("X-Api-Key", _apiKey);

                var response = await _httpClient.SendAsync(requestMessage);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("HeyGen streaming token error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                    throw new HttpRequestException($"HeyGen API returned {response.StatusCode}: {errorContent}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(responseContent);
                
                if (jsonDoc.RootElement.TryGetProperty("data", out var dataElement) &&
                    dataElement.TryGetProperty("token", out var tokenElement))
                {
                    var token = tokenElement.GetString();
                    if (!string.IsNullOrEmpty(token))
                    {
                        _logger.LogInformation("HeyGen streaming token obtained successfully");
                        return token;
                    }
                }

                _logger.LogError("Failed to parse token from response: {Response}", responseContent);
                throw new InvalidOperationException("Failed to get streaming token from HeyGen");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting HeyGen streaming token");
                throw;
            }
        }

        /// <summary>
        /// Step 2: Create a new streaming session
        /// Returns LiveKit connection details (URL and access token)
        /// </summary>
        public async Task<HeyGenStreamingSessionData> CreateStreamingSessionAsync()
        {
            try
            {
                _logger.LogInformation("Creating HeyGen streaming session for avatar: {AvatarName}", _avatarName);

                var token = await GetStreamingTokenAsync();

                // Create streaming session request
                // NOTE: HeyGen Interactive Avatars have built-in AI that processes text
                // We may need to add system_prompt or other parameters to override default behavior
                var request = new
                {
                    version = "v2",
                    avatar_name = _avatarName,
                    quality = "high",
                    video_encoding = "H264"
                    // TODO: Check HeyGen API docs for system_prompt, personality, or other parameters
                    // that can override the avatar's default AI behavior to make it speak verbatim
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}streaming.new")
                {
                    Content = content
                };
                requestMessage.Headers.Add("Authorization", $"Bearer {token}");

                var response = await _httpClient.SendAsync(requestMessage);
                var responseContent = await response.Content.ReadAsStringAsync();

                _logger.LogDebug("Session creation response: {StatusCode} - {Content}", response.StatusCode, responseContent);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("HeyGen streaming session creation error: {StatusCode} - {Error}", response.StatusCode, responseContent);
                    throw new HttpRequestException($"HeyGen API returned {response.StatusCode}: {responseContent}");
                }

                using var jsonDoc = JsonDocument.Parse(responseContent);
                var root = jsonDoc.RootElement;

                // Check for error code
                if (root.TryGetProperty("code", out var codeElement) && codeElement.GetInt32() != 100)
                {
                    var message = root.TryGetProperty("message", out var msgElement) ? msgElement.GetString() : "Unknown error";
                    _logger.LogError("HeyGen API returned error code {Code}: {Message}", codeElement.GetInt32(), message);
                    throw new InvalidOperationException($"HeyGen API error: {message}");
                }

                // Parse data section
                if (!root.TryGetProperty("data", out var dataElement))
                {
                    _logger.LogError("No data section in response: {Response}", responseContent);
                    throw new InvalidOperationException("Failed to create streaming session: No data in response");
                }

                var sessionId = dataElement.TryGetProperty("session_id", out var sidElement) ? sidElement.GetString() : null;
                var url = dataElement.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                var accessToken = dataElement.TryGetProperty("access_token", out var tokenElement) ? tokenElement.GetString() : null;

                if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(url) || string.IsNullOrEmpty(accessToken))
                {
                    _logger.LogError("Missing required fields. SessionId: {SessionId}, Url: {Url}, AccessToken present: {HasToken}",
                        sessionId, url, !string.IsNullOrEmpty(accessToken));
                    throw new InvalidOperationException("Failed to create streaming session: Missing required fields");
                }

                var result = new HeyGenStreamingSessionData
                {
                    SessionId = sessionId,
                    Url = url,
                    AccessToken = accessToken,
                    StreamingToken = token // Cache the token used to create the session
                };

                _logger.LogInformation("HeyGen streaming session created successfully: {SessionId}", result.SessionId);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating HeyGen streaming session");
                throw;
            }
        }

        /// <summary>
        /// Step 2.5: Start the streaming session (required before sending tasks)
        /// </summary>
        /// <param name="sessionId">The HeyGen session ID</param>
        /// <param name="streamingToken">Optional: The token used to create the session. If not provided, a new token will be generated.</param>
        public async Task StartStreamingSessionAsync(string sessionId, string? streamingToken = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("Session ID cannot be empty", nameof(sessionId));
            }

            try
            {
                _logger.LogInformation("Starting HeyGen streaming session: {SessionId}", sessionId);

                // Use provided token or generate a new one
                var token = streamingToken ?? await GetStreamingTokenAsync();
                if (string.IsNullOrWhiteSpace(streamingToken))
                {
                    _logger.LogWarning("No streaming token provided for session {SessionId}. Generating new token.", sessionId);
                }

                var request = new { session_id = sessionId };
                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}streaming.start")
                {
                    Content = content
                };
                requestMessage.Headers.Add("Authorization", $"Bearer {token}");

                var response = await _httpClient.SendAsync(requestMessage);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("HeyGen streaming.start returned {StatusCode}: {Error}", response.StatusCode, errorContent);
                    // Don't throw - session might already be started or this may not be required
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        _logger.LogInformation("streaming.start returned Unauthorized - session may already be ready from streaming.new");
                    }
                }
                else
                {
                    _logger.LogInformation("HeyGen streaming session started successfully: {SessionId}", sessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting HeyGen streaming session (non-critical)");
                // Don't throw - this may not be required for all sessions
            }
        }

        /// <summary>
        /// Step 3: Send text to avatar for real-time speech generation
        /// </summary>
        /// <param name="sessionId">The HeyGen session ID</param>
        /// <param name="text">The text to send to the avatar</param>
        /// <param name="streamingToken">Optional: The token used to create the session. If not provided, a new token will be generated (may cause Unauthorized errors).</param>
        public async Task SendStreamingTaskAsync(string sessionId, string text, string? streamingToken = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("Session ID cannot be empty", nameof(sessionId));
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Text cannot be empty", nameof(text));
            }

            try
            {
                _logger.LogInformation("Sending streaming task to session {SessionId}: {TextPreview}...", 
                    sessionId, text.Substring(0, Math.Min(50, text.Length)));

                // Use provided token or generate a new one (but reuse is recommended)
                var token = streamingToken ?? await GetStreamingTokenAsync();
                if (string.IsNullOrWhiteSpace(streamingToken))
                {
                    _logger.LogWarning("No streaming token provided for session {SessionId}. Generating new token - this may cause Unauthorized errors if HeyGen requires token reuse.", sessionId);
                }

                // Log the text being sent to HeyGen for debugging
                _logger.LogDebug("Sending text to HeyGen avatar (first 300 chars): {TextPreview}", 
                    text.Substring(0, Math.Min(300, text.Length)));
                
                var request = new
                {
                    session_id = sessionId,
                    text = text,
                    task_type = "talk"
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}streaming.task")
                {
                    Content = content
                };
                requestMessage.Headers.Add("Authorization", $"Bearer {token}");

                var response = await _httpClient.SendAsync(requestMessage);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("HeyGen streaming task error: {StatusCode} - {Error}\nRequest was: {RequestJson}", 
                        response.StatusCode, errorContent, json);
                    throw new HttpRequestException($"HeyGen API returned {response.StatusCode}: {errorContent}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("HeyGen streaming task response: {Response}", responseContent);

                _logger.LogInformation("HeyGen streaming task sent successfully - avatar should now be animating");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending HeyGen streaming task");
                throw;
            }
        }

        /// <summary>
        /// Step 4: Stop and cleanup streaming session
        /// </summary>
        /// <param name="sessionId">The HeyGen session ID</param>
        /// <param name="streamingToken">Optional: The token used to create the session. If not provided, a new token will be generated.</param>
        public async Task StopStreamingSessionAsync(string sessionId, string? streamingToken = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                _logger.LogWarning("StopStreamingSessionAsync called with empty session ID");
                return;
            }

            try
            {
                _logger.LogInformation("Stopping HeyGen streaming session: {SessionId}", sessionId);

                // Use provided token or generate a new one
                var token = streamingToken ?? await GetStreamingTokenAsync();

                var request = new { session_id = sessionId };
                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}streaming.stop")
                {
                    Content = content
                };
                requestMessage.Headers.Add("Authorization", $"Bearer {token}");

                var response = await _httpClient.SendAsync(requestMessage);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("HeyGen streaming stop returned {StatusCode}: {Error}", response.StatusCode, errorContent);
                    // Don't throw - session might already be stopped
                }
                else
                {
                    _logger.LogInformation("HeyGen streaming session stopped successfully: {SessionId}", sessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping HeyGen streaming session (non-critical)");
                // Don't throw - cleanup operation
            }
        }
    }
}

