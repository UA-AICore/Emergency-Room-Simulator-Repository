using System.Text;
using System.Text.Json;

namespace ERSimulatorApp.Services
{
    public interface IWhisperService
    {
        Task<string> TranscribeAudioAsync(Stream audioStream, string fileName = "audio.webm", CancellationToken cancellationToken = default);
    }

    public class WhisperService : IWhisperService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<WhisperService> _logger;
        private readonly string _apiKey;
        private readonly string _apiUrl;

        public WhisperService(
            HttpClient httpClient,
            ILogger<WhisperService> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey is required for Whisper");
            _apiUrl = "https://api.openai.com/v1/audio/transcriptions";

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("OpenAI API key is not configured");
            }

            _logger.LogInformation("Whisper Service initialized");
        }

        public async Task<string> TranscribeAudioAsync(Stream audioStream, string fileName = "audio.webm", CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting audio transcription for file: {FileName}", fileName);

                // Reset stream position to beginning
                if (audioStream.CanSeek)
                {
                    audioStream.Position = 0;
                }

                // Create multipart form data
                using var content = new MultipartFormDataContent();
                
                // Add the audio file
                var audioContent = new StreamContent(audioStream);
                audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/webm");
                content.Add(audioContent, "file", fileName);

                // Add model parameter
                content.Add(new StringContent("whisper-1"), "model");

                // Add language (optional, but helps with accuracy)
                content.Add(new StringContent("en"), "language");

                // Create request
                var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl)
                {
                    Content = content
                };

                request.Headers.Add("Authorization", $"Bearer {_apiKey}");

                // Send request
                var response = await _httpClient.SendAsync(request, cancellationToken);
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Whisper API error: {StatusCode} - {Error}", response.StatusCode, responseContent);
                    throw new HttpRequestException($"Whisper API returned {response.StatusCode}: {responseContent}");
                }

                // Parse response
                using var jsonDoc = JsonDocument.Parse(responseContent);
                var transcript = jsonDoc.RootElement.GetProperty("text").GetString() ?? string.Empty;

                _logger.LogInformation("Audio transcription successful: {TranscriptPreview}...", 
                    transcript.Substring(0, Math.Min(50, transcript.Length)));

                return transcript.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error transcribing audio");
                throw;
            }
        }
    }
}

