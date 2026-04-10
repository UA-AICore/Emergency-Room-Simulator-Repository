using System.Text;
using System.Text.Json;

namespace ERSimulatorApp.Services
{
    /// <summary>PCM 16-bit LE mono 24 kHz from TTS, or empty with <see cref="ErrorMessage"/>.</summary>
    public sealed record ElevenLabsPcmTtsResult(byte[] Pcm, string? ErrorMessage);

    /// <summary>Text-to-speech that returns raw PCM suitable for streaming clients expecting 24 kHz.</summary>
    public interface IElevenLabsTextToSpeechService
    {
        Task<ElevenLabsPcmTtsResult> SynthesizePcm24kAsync(string text, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// ElevenLabs <c>POST /v1/text-to-speech/{voice_id}</c> with <c>output_format=pcm_24000</c>.
    /// Use <see cref="MicrosoftEdgeFreePcmTtsService"/> when <c>ElevenLabs:TtsEngine</c> is <c>MicrosoftEdgeFree</c>.
    /// </summary>
    public sealed class ElevenLabsTextToSpeechService : IElevenLabsTextToSpeechService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<ElevenLabsTextToSpeechService> _logger;
        private readonly string _apiKey;
        private readonly string _voiceId;
        private readonly string _modelId;

        public ElevenLabsTextToSpeechService(
            HttpClient httpClient,
            ILogger<ElevenLabsTextToSpeechService> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            var mainKey = (configuration["ElevenLabs:ApiKey"] ?? Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY") ?? "").Trim();
            var ttsKey = (configuration["ElevenLabs:TextToSpeechApiKey"] ?? "").Trim();
            _apiKey = string.IsNullOrEmpty(ttsKey) ? mainKey : ttsKey;
            _voiceId = (configuration["ElevenLabs:TextToSpeechVoiceId"] ?? "").Trim();
            _modelId = (configuration["ElevenLabs:TextToSpeechModelId"] ?? "eleven_turbo_v2").Trim();
            var ttsTimeout = configuration.GetValue("ElevenLabs:TextToSpeechTimeoutSeconds", 120);
            _httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(ttsTimeout, 10, 600));
            _httpClient.BaseAddress = new Uri("https://api.elevenlabs.io/");
        }

        public async Task<ElevenLabsPcmTtsResult> SynthesizePcm24kAsync(string text, CancellationToken cancellationToken = default)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
                return new ElevenLabsPcmTtsResult(Array.Empty<byte>(), "Empty text after trim.");

            if (string.IsNullOrWhiteSpace(_apiKey))
                return new ElevenLabsPcmTtsResult(Array.Empty<byte>(), "ElevenLabs API key is not configured for TTS.");

            if (string.IsNullOrWhiteSpace(_voiceId))
                return new ElevenLabsPcmTtsResult(Array.Empty<byte>(), "ElevenLabs:TextToSpeechVoiceId is not set.");

            var uri = $"v1/text-to-speech/{Uri.EscapeDataString(_voiceId)}?output_format=pcm_24000";
            var body = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["text"] = trimmed,
                ["model_id"] = _modelId
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("xi-api-key", _apiKey);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ElevenLabs TTS request failed");
                return new ElevenLabsPcmTtsResult(Array.Empty<byte>(), ex.Message);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var preview = bytes.Length > 0 ? Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 500))) : "";
                _logger.LogError("ElevenLabs TTS HTTP {Status}: {Preview}", response.StatusCode, preview);
                if (IsMissingTextToSpeechPermission(preview))
                {
                    return new ElevenLabsPcmTtsResult(Array.Empty<byte>(),
                        "ElevenLabs API key does not allow Text-to-Speech. In the ElevenLabs dashboard, enable the Text to Speech permission on this key, or set ElevenLabs:TextToSpeechApiKey to a separate key that has TTS (keep your current key for speech-to-text only). Or set LiveAvatar:UseAgentSpeakWebSocket to false to use LiveAvatar speak_text without server TTS.");
                }
                return new ElevenLabsPcmTtsResult(Array.Empty<byte>(),
                    $"ElevenLabs TTS returned {(int)response.StatusCode}: {preview}".Trim());
            }

            if (bytes.Length == 0)
                return new ElevenLabsPcmTtsResult(Array.Empty<byte>(), "ElevenLabs TTS returned empty body.");

            _logger.LogInformation("ElevenLabs TTS: {Bytes} bytes PCM 24kHz", bytes.Length);
            return new ElevenLabsPcmTtsResult(bytes, null);
        }

        private static bool IsMissingTextToSpeechPermission(string preview)
        {
            if (preview.Contains("missing_permissions", StringComparison.OrdinalIgnoreCase))
                return true;
            if (preview.Contains("text_to_speech", StringComparison.OrdinalIgnoreCase)
                && preview.Contains("permission", StringComparison.OrdinalIgnoreCase))
                return true;
            try
            {
                using var doc = JsonDocument.Parse(preview);
                if (!doc.RootElement.TryGetProperty("detail", out var detail))
                    return false;
                if (detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("status", out var st)
                    && st.GetString() is { } s
                    && s.Equals("missing_permissions", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (JsonException)
            {
                /* not JSON */
            }
            return false;
        }
    }
}
