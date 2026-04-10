using ERSimulatorApp.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ERSimulatorApp.Services
{
    /// <summary>
    /// LiveAvatar API (LITE mode): session token + start → LiveKit.
    /// Per LiveAvatar LITE lifecycle docs, TTS audio is normally sent on the session <c>ws_url</c> as <c>agent.speak</c> (PCM 24 kHz);
    /// alternatively the frontend may use LiveKit <c>publishData</c> <c>avatar.speak_text</c> on topic <c>agent-control</c>.
    /// </summary>
    public class LiveAvatarStreamingService : IHeyGenStreamingService
    {
        public bool DeliversSpeechViaServer => false;

        private readonly HttpClient _httpClient;
        private readonly ILogger<LiveAvatarStreamingService> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _avatarId;

        public LiveAvatarStreamingService(
            HttpClient httpClient,
            ILogger<LiveAvatarStreamingService> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _configuration = configuration;
            _apiKey = (configuration["LiveAvatar:ApiKey"] ?? Environment.GetEnvironmentVariable("LIVEAVATAR_API_KEY") ?? "").Trim();
            _baseUrl = (configuration["LiveAvatar:BaseUrl"] ?? "https://api.liveavatar.com").TrimEnd('/');
            _avatarId = (configuration["LiveAvatar:AvatarId"] ?? "").Trim();

            if (string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException("LiveAvatar:ApiKey (or LIVEAVATAR_API_KEY) is required when UseLiveAvatar is true");
            if (string.IsNullOrWhiteSpace(_avatarId) || !Guid.TryParse(_avatarId, out _))
                throw new InvalidOperationException("LiveAvatar:AvatarId must be a valid avatar UUID when UseLiveAvatar is true");

            _logger.LogInformation("LiveAvatar streaming service initialized (LITE), BaseUrl: {Base}", _baseUrl);
        }

        private static bool IsApiSuccessCode(int code) => code is 100 or 1000;

        public async Task<string> GetStreamingTokenAsync()
        {
            var (_, sessionToken) = await CreateSessionTokenAsync();
            return sessionToken;
        }

        public async Task<HeyGenStreamingSessionData> CreateStreamingSessionAsync()
        {
            var (sessionIdFromToken, sessionToken) = await CreateSessionTokenAsync();
            _logger.LogInformation("LiveAvatar session token created, session_id: {SessionId}", sessionIdFromToken);

            using var startRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/sessions/start");
            startRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);

            var startResponse = await _httpClient.SendAsync(startRequest);
            var startBody = await startResponse.Content.ReadAsStringAsync();

            if (!startResponse.IsSuccessStatusCode)
            {
                _logger.LogError("LiveAvatar sessions/start failed: {Status} {Body}", startResponse.StatusCode, startBody);
                throw new HttpRequestException($"LiveAvatar sessions/start returned {(int)startResponse.StatusCode}: {startBody}");
            }

            using var startDoc = JsonDocument.Parse(startBody);
            var root = startDoc.RootElement;
            if (!root.TryGetProperty("code", out var codeEl) || !IsApiSuccessCode(codeEl.GetInt32()))
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : startBody;
                _logger.LogError("LiveAvatar sessions/start API error: {Body}", startBody);
                throw new InvalidOperationException($"LiveAvatar sessions/start error: {msg}");
            }

            if (!root.TryGetProperty("data", out var data))
            {
                _logger.LogError("LiveAvatar sessions/start missing data: {Body}", startBody);
                throw new InvalidOperationException("LiveAvatar sessions/start: missing data");
            }

            var sessionId = data.TryGetProperty("session_id", out var sid) ? sid.GetString() : sessionIdFromToken;
            var livekitUrl = data.TryGetProperty("livekit_url", out var lu) ? lu.GetString() : null;
            var livekitClientToken = data.TryGetProperty("livekit_client_token", out var lct) ? lct.GetString() : null;
            string? wsUrl = null;
            if (data.TryGetProperty("ws_url", out var ws) && ws.ValueKind == JsonValueKind.String)
                wsUrl = ws.GetString();

            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(livekitUrl) || string.IsNullOrWhiteSpace(livekitClientToken))
            {
                _logger.LogError("LiveAvatar start response missing fields: {Body}", startBody);
                throw new InvalidOperationException("LiveAvatar sessions/start: missing session_id, livekit_url, or livekit_client_token");
            }

            return new HeyGenStreamingSessionData
            {
                SessionId = sessionId!,
                Url = livekitUrl!,
                AccessToken = livekitClientToken!,
                StreamingToken = sessionToken,
                WsUrl = wsUrl,
                Provider = "liveavatar"
            };
        }

        public async Task StartStreamingSessionAsync(string sessionId, string? streamingToken = null)
        {
            if (string.IsNullOrWhiteSpace(streamingToken))
            {
                _logger.LogWarning("LiveAvatar keep-alive skipped: no session token");
                return;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/sessions/keep-alive");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", streamingToken);
                var response = await _httpClient.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("LiveAvatar keep-alive {Status}: {Body}", response.StatusCode, body);
                    return;
                }

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("code", out var c) && !IsApiSuccessCode(c.GetInt32()))
                    _logger.LogDebug("LiveAvatar keep-alive non-success code in body: {Body}", body);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LiveAvatar keep-alive failed (non-critical)");
            }
        }

        public Task SendStreamingTaskAsync(string sessionId, string text, string? streamingToken = null)
        {
            _logger.LogDebug("LiveAvatar: server-side speech skipped; browser publishes speak_text; session {SessionId}, {Len} chars",
                sessionId, text?.Length ?? 0);
            return Task.CompletedTask;
        }

        public async Task StopStreamingSessionAsync(string sessionId, string? streamingToken = null)
        {
            if (string.IsNullOrWhiteSpace(streamingToken))
            {
                _logger.LogWarning("LiveAvatar stop skipped: empty token");
                return;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/sessions/stop");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", streamingToken);
                var response = await _httpClient.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    _logger.LogWarning("LiveAvatar sessions/stop {Status}: {Body}", response.StatusCode, body);
                else
                    _logger.LogInformation("LiveAvatar session stopped: {SessionId}", sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LiveAvatar sessions/stop failed (non-critical)");
            }
        }

        private async Task<(string SessionId, string SessionToken)> CreateSessionTokenAsync()
        {
            var isSandbox = _configuration.GetValue<bool?>("LiveAvatar:IsSandbox")
                ?? (bool.TryParse(Environment.GetEnvironmentVariable("LIVEAVATAR_SANDBOX"), out var envSb) && envSb);

            var payload = new Dictionary<string, object?>
            {
                ["mode"] = "LITE",
                ["avatar_id"] = _avatarId,
                ["video_settings"] = new Dictionary<string, string>
                {
                    ["quality"] = "high",
                    ["encoding"] = "H264"
                },
                ["is_sandbox"] = isSandbox
            };

            var catalogVoice = (_configuration["LiveAvatar:CatalogVoiceId"] ?? "").Trim();
            if (!string.IsNullOrEmpty(catalogVoice) && Guid.TryParse(catalogVoice, out _))
            {
                payload["voice_id"] = catalogVoice;
                _logger.LogInformation("LiveAvatar sessions/token: including catalog voice_id");
            }

            var json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/sessions/token");
            request.Headers.TryAddWithoutValidation("X-API-KEY", _apiKey);
            request.Headers.TryAddWithoutValidation("accept", "application/json");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("LiveAvatar sessions/token failed: {Status} {Body}", response.StatusCode, body);
                throw new HttpRequestException($"LiveAvatar sessions/token returned {(int)response.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("code", out var codeEl) || !IsApiSuccessCode(codeEl.GetInt32()))
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : body;
                _logger.LogError("LiveAvatar token API error: {Body}", body);
                throw new InvalidOperationException($"LiveAvatar sessions/token error: {msg}");
            }

            if (!root.TryGetProperty("data", out var data))
                throw new InvalidOperationException("LiveAvatar sessions/token: missing data");

            var sessionId = data.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
            var sessionToken = data.TryGetProperty("session_token", out var st) ? st.GetString() : null;

            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(sessionToken))
            {
                _logger.LogError("LiveAvatar token response missing id/token: {Body}", body);
                throw new InvalidOperationException("LiveAvatar sessions/token: missing session_id or session_token");
            }

            return (sessionId!, sessionToken!);
        }
    }
}
