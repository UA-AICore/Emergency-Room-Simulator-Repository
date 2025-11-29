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

                // Determine MIME type from file extension
                string mimeType = "audio/webm"; // Default
                string lowerFileName = fileName.ToLowerInvariant();
                
                if (lowerFileName.EndsWith(".webm"))
                {
                    mimeType = "audio/webm";
                }
                else if (lowerFileName.EndsWith(".mp4") || lowerFileName.EndsWith(".m4a"))
                {
                    mimeType = "audio/mp4";
                }
                else if (lowerFileName.EndsWith(".mp3") || lowerFileName.EndsWith(".mpeg"))
                {
                    mimeType = "audio/mpeg";
                }
                else if (lowerFileName.EndsWith(".wav"))
                {
                    mimeType = "audio/wav";
                }
                else if (lowerFileName.EndsWith(".ogg"))
                {
                    mimeType = "audio/ogg";
                }

                _logger.LogInformation("Detected MIME type: {MimeType} for file: {FileName}", mimeType, fileName);

                // Create multipart form data
                using var content = new MultipartFormDataContent();
                
                // Add the audio file
                // Note: Don't set Content-Type header - let the API detect it from the file
                // Some APIs are sensitive to Content-Type mismatches
                var audioContent = new StreamContent(audioStream);
                // Only set Content-Type if it's a well-known format that Whisper definitely supports
                if (mimeType == "audio/wav" || mimeType == "audio/mpeg" || mimeType == "audio/mp4")
                {
                    audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
                }
                // For webm, let the API auto-detect
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

                // Log request details
                _logger.LogInformation("Sending audio to Whisper API: FileName={FileName}, MimeType={MimeType}, StreamLength={StreamLength}", 
                    fileName, mimeType, audioStream.CanSeek ? audioStream.Length : -1);

                // Send request
                var response = await _httpClient.SendAsync(request, cancellationToken);
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Whisper API error: {StatusCode} - {Error}", response.StatusCode, responseContent);
                    
                    // Provide more helpful error messages
                    string errorMessage = $"Whisper API returned {response.StatusCode}";
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        errorMessage += ": Invalid audio format or corrupted file. Please try recording again.";
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        errorMessage += ": Invalid API key. Please check your OpenAI API key configuration.";
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge)
                    {
                        errorMessage += ": Audio file is too large. Please record a shorter audio clip.";
                    }
                    else
                    {
                        errorMessage += $": {responseContent}";
                    }
                    
                    throw new HttpRequestException(errorMessage);
                }

                // Parse response
                try
                {
                    using var jsonDoc = JsonDocument.Parse(responseContent);
                    
                    if (!jsonDoc.RootElement.TryGetProperty("text", out var textElement))
                    {
                        _logger.LogError("Whisper API response missing 'text' property. Full response: {Response}", responseContent);
                        throw new InvalidOperationException("Whisper API response is missing the 'text' property");
                    }
                    
                    var transcript = textElement.GetString() ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(transcript))
                    {
                        _logger.LogWarning("Whisper API returned empty transcript. Response: {Response}", responseContent);
                        throw new InvalidOperationException("Whisper API returned an empty transcript. The audio may be too quiet, too short, or contain no speech.");
                    }

                    _logger.LogInformation("Audio transcription successful: {TranscriptPreview}...", 
                        transcript.Substring(0, Math.Min(50, transcript.Length)));

                    return transcript.Trim();
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to parse Whisper API response. Response content: {Response}", responseContent);
                    throw new InvalidOperationException($"Failed to parse Whisper API response: {ex.Message}", ex);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error transcribing audio");
                throw;
            }
        }
    }
}

