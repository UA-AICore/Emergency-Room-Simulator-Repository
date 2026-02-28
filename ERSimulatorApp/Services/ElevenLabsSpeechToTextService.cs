using System.IO;
using System.Text.Json;

namespace ERSimulatorApp.Services
{
    /// <summary>
    /// Speech-to-text for the Dr. Dexter avatar using ElevenLabs STT API.
    /// </summary>
    public interface IElevenLabsSpeechToTextService
    {
        Task<string> TranscribeAudioAsync(Stream audioStream, string fileName = "audio.webm", CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Calls ElevenLabs POST /v1/speech-to-text (multipart: file, model_id).
    /// API key via xi-api-key header. Response JSON has "text" for the transcript.
    /// </summary>
    public class ElevenLabsSpeechToTextService : IElevenLabsSpeechToTextService
    {
        private const string DefaultApiUrl = "https://api.elevenlabs.io/v1/speech-to-text";
        private readonly HttpClient _httpClient;
        private readonly ILogger<ElevenLabsSpeechToTextService> _logger;
        private readonly string _apiKey;
        private readonly string _modelId;

        public ElevenLabsSpeechToTextService(
            HttpClient httpClient,
            ILogger<ElevenLabsSpeechToTextService> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _apiKey = (configuration["ElevenLabs:ApiKey"] ?? Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY") ?? "").Trim();
            _modelId = (configuration["ElevenLabs:SpeechToTextModelId"] ?? "scribe_v2").Trim();

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                _logger.LogWarning("ElevenLabs API key is not set. Set ElevenLabs:ApiKey or ELEVENLABS_API_KEY for Dr. Dexter voice input.");
                return;
            }

            _httpClient.BaseAddress = new Uri("https://api.elevenlabs.io/");
            _logger.LogInformation("ElevenLabs STT service initialized (model: {ModelId})", _modelId);
        }

        public async Task<string> TranscribeAudioAsync(Stream audioStream, string fileName = "audio.webm", CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException("ElevenLabs API key is not configured. Set ElevenLabs:ApiKey or ELEVENLABS_API_KEY for voice input.");

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "audio.webm";

            var contentType = GetContentTypeFromFileName(fileName);
            if (audioStream.CanSeek && audioStream.Position > 0)
                audioStream.Position = 0;

            using var content = new MultipartFormDataContent();
            var audioContent = new StreamContent(audioStream);
            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            content.Add(audioContent, "file", fileName);
            content.Add(new StringContent(_modelId), "model_id");

            var request = new HttpRequestMessage(HttpMethod.Post, "v1/speech-to-text") { Content = content };
            request.Headers.Add("xi-api-key", _apiKey);
            request.Headers.Add("Accept", "application/json");

            _logger.LogInformation("Sending audio to ElevenLabs STT (file: {FileName}, model: {ModelId})", fileName, _modelId);
            var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("ElevenLabs STT error {StatusCode}: {Response}", response.StatusCode, responseContent);
                throw new HttpRequestException($"ElevenLabs speech-to-text returned {response.StatusCode}: {responseContent}");
            }

            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;
            string? text = null;
            if (root.TryGetProperty("text", out var textEl))
                text = textEl.GetString();
            if (root.TryGetProperty("transcripts", out var transcripts) && transcripts.GetArrayLength() > 0)
                text = transcripts[0].GetProperty("text").GetString();

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning("ElevenLabs STT returned empty transcript");
                throw new InvalidOperationException("ElevenLabs returned empty transcript.");
            }

            _logger.LogInformation("ElevenLabs transcription successful: {Preview}...", text.Substring(0, Math.Min(50, text.Length)));
            return text.Trim();
        }

        private static string GetContentTypeFromFileName(string fileName)
        {
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            return ext switch
            {
                ".webm" => "audio/webm",
                ".ogg" => "audio/ogg",
                ".mp4" or ".m4a" => "audio/mp4",
                ".mp3" or ".mpeg" => "audio/mpeg",
                ".wav" => "audio/wav",
                _ => "audio/webm"
            };
        }
    }
}
