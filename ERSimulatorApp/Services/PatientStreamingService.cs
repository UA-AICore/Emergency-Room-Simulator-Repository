using ERSimulatorApp.Models;
using System.Text;
using System.Text.Json;

namespace ERSimulatorApp.Services
{
    /// <summary>
    /// HeyGen Streaming Service for Patient Avatar
    /// Uses HeyGenPatient configuration section
    /// </summary>
    public interface IPatientStreamingService
    {
        Task<string> GetStreamingTokenAsync();
        Task<HeyGenStreamingSessionData> CreateStreamingSessionAsync();
        Task StartStreamingSessionAsync(string sessionId, string? streamingToken = null);
        Task SendStreamingTaskAsync(string sessionId, string text, string? streamingToken = null);
        Task StopStreamingSessionAsync(string sessionId, string? streamingToken = null);
    }

    public class PatientStreamingService : IPatientStreamingService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<PatientStreamingService> _logger;
        private readonly string _apiKey;
        private readonly string _apiUrl;
        private readonly string _avatarName;

        public PatientStreamingService(
            HttpClient httpClient,
            ILogger<PatientStreamingService> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _apiKey = configuration["HeyGenPatient:ApiKey"] ?? throw new InvalidOperationException("HeyGenPatient:ApiKey is required");
            _apiUrl = "https://api.heygen.com/v1/";
            _avatarName = configuration["HeyGenPatient:AvatarId"] ?? throw new InvalidOperationException("HeyGenPatient:AvatarId is required");

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("HeyGen Patient API key is not configured");
            }
            if (string.IsNullOrWhiteSpace(_avatarName))
            {
                throw new InvalidOperationException("HeyGen Patient Avatar ID is not configured");
            }

            _logger.LogInformation("Patient Streaming Service initialized with Avatar: {AvatarName}", _avatarName);
        }

        public async Task<string> GetStreamingTokenAsync()
        {
            try
            {
                _logger.LogInformation("Getting HeyGen streaming token for patient");

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

        public async Task<HeyGenStreamingSessionData> CreateStreamingSessionAsync()
        {
            try
            {
                _logger.LogInformation("Creating HeyGen streaming session for patient avatar: {AvatarName}", _avatarName);

                var token = await GetStreamingTokenAsync();

                var request = new
                {
                    version = "v2",
                    avatar_name = _avatarName,
                    quality = "high",
                    video_encoding = "H264"
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

                if (root.TryGetProperty("code", out var codeElement) && codeElement.GetInt32() != 100)
                {
                    var message = root.TryGetProperty("message", out var msgElement) ? msgElement.GetString() : "Unknown error";
                    _logger.LogError("HeyGen API returned error code {Code}: {Message}", codeElement.GetInt32(), message);
                    throw new InvalidOperationException($"HeyGen API error: {message}");
                }

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
                    StreamingToken = token
                };

                _logger.LogInformation("HeyGen patient streaming session created successfully: {SessionId}", result.SessionId);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating HeyGen patient streaming session");
                throw;
            }
        }

        public async Task StartStreamingSessionAsync(string sessionId, string? streamingToken = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("Session ID cannot be empty", nameof(sessionId));
            }

            try
            {
                _logger.LogInformation("Starting HeyGen patient streaming session: {SessionId}", sessionId);

                var token = streamingToken ?? await GetStreamingTokenAsync();

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
                }
                else
                {
                    _logger.LogInformation("HeyGen patient streaming session started successfully: {SessionId}", sessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting HeyGen patient streaming session (non-critical)");
            }
        }

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
                _logger.LogInformation(
                    "Sending streaming task to patient session {SessionId} in REPEAT mode: {TextPreview}...",
                    sessionId, text.Substring(0, Math.Min(50, text.Length)));

                if (string.IsNullOrWhiteSpace(streamingToken))
                {
                    _logger.LogError("Streaming token is REQUIRED for streaming.task - Session ID: {SessionId}", sessionId);
                    throw new InvalidOperationException("Streaming token is required for streaming.task");
                }

                var token = streamingToken;

                var request = new
                {
                    session_id = sessionId,
                    text = text,
                    task_type = "repeat",
                    task_mode = "sync"
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}streaming.task")
                {
                    Content = content
                };

                requestMessage.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var response = await _httpClient.SendAsync(requestMessage);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("HeyGen patient streaming task error: {StatusCode} - {Error}", response.StatusCode, responseContent);
                    throw new HttpRequestException($"HeyGen API returned {response.StatusCode}: {responseContent}");
                }

                _logger.LogInformation("HeyGen patient streaming task sent successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending HeyGen patient streaming task");
                throw;
            }
        }

        public async Task StopStreamingSessionAsync(string sessionId, string? streamingToken = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                _logger.LogWarning("StopStreamingSessionAsync called with empty session ID");
                return;
            }

            try
            {
                _logger.LogInformation("Stopping HeyGen patient streaming session: {SessionId}", sessionId);

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
                }
                else
                {
                    _logger.LogInformation("HeyGen patient streaming session stopped successfully: {SessionId}", sessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping HeyGen patient streaming session (non-critical)");
            }
        }
    }
}

