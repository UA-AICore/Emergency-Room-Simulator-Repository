using System.IO;
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
            
            // Resolve API key with multiple fallbacks (matching MarvelCrud pattern)
            _apiKey = configuration["OpenAI:ApiKey"]
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                ?? Environment.GetEnvironmentVariable("OpenAI__ApiKey");
            
            _apiUrl = "https://api.openai.com/v1/audio/transcriptions";

            // Validate API key (matching MarvelCrud validation)
            if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey.Contains("USE USER", StringComparison.OrdinalIgnoreCase))
            {
                var errorMsg = "OpenAI API key is not configured. Set with: dotnet user-secrets set \"OpenAI:ApiKey\" \"sk-...\" or set OPENAI_API_KEY environment variable";
                _logger.LogError(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            // Log key status (without exposing the actual key)
            _logger.LogInformation("Whisper Service initialized with API key (length: {KeyLength}, starts with: {KeyPrefix})", 
                _apiKey.Length, _apiKey.Substring(0, Math.Min(7, _apiKey.Length)));
        }

        public async Task<string> TranscribeAudioAsync(Stream audioStream, string fileName = "audio.webm", CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting audio transcription for file: {FileName}", fileName);

                // Determine content type from file extension
                var contentType = GetContentTypeFromFileName(fileName);
                _logger.LogInformation("Detected content type: {ContentType} for file: {FileName}", contentType, fileName);

                // Copy stream to MemoryStream to ensure it's readable and seekable
                // This is important because the original stream might be consumed or not seekable
                MemoryStream? memoryStreamToDispose = null;
                Stream streamToUse;
                
                if (audioStream is MemoryStream ms && ms.CanSeek)
                {
                    // If it's already a MemoryStream and seekable, just reset position
                    ms.Position = 0;
                    streamToUse = ms;
                    _logger.LogInformation("Using existing MemoryStream, size: {Size} bytes", ms.Length);
                }
                else
                {
                    // Copy to new MemoryStream
                    memoryStreamToDispose = new MemoryStream();
                    await audioStream.CopyToAsync(memoryStreamToDispose, cancellationToken);
                    memoryStreamToDispose.Position = 0;
                    streamToUse = memoryStreamToDispose;
                    _logger.LogInformation("Copied audio stream to MemoryStream, size: {Size} bytes", memoryStreamToDispose.Length);
                }

                try
                {
                    // Validate stream has data
                    if (streamToUse.Length == 0)
                    {
                        _logger.LogError("Audio stream is empty");
                        throw new InvalidOperationException("Audio stream is empty - no audio data to transcribe");
                    }

                    // Create multipart form data
                    using var content = new MultipartFormDataContent();
                    
                    // Add the audio file with proper content type
                    var audioContent = new StreamContent(streamToUse);
                    audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
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

                    // Add Authorization header
                    request.Headers.Add("Authorization", $"Bearer {_apiKey}");
                    
                    // Log API key status (without exposing the actual key)
                    _logger.LogInformation("Sending audio to Whisper API: {Size} bytes, ContentType: {ContentType}, API Key length: {KeyLength}, Key prefix: {KeyPrefix}", 
                        streamToUse.Length, contentType, _apiKey.Length, _apiKey.Substring(0, Math.Min(10, _apiKey.Length)));

                    // Send request
                    var response = await _httpClient.SendAsync(request, cancellationToken);
                    var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError("Whisper API error: {StatusCode} - {Error}", response.StatusCode, responseContent);
                        
                        // Parse error details if available
                        try
                        {
                            using var errorDoc = JsonDocument.Parse(responseContent);
                            if (errorDoc.RootElement.TryGetProperty("error", out var errorElement))
                            {
                                var errorMessage = errorElement.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown error";
                                var errorCode = errorElement.TryGetProperty("code", out var code) ? code.GetString() : "Unknown code";
                                var errorType = errorElement.TryGetProperty("type", out var type) ? type.GetString() : "Unknown type";
                                
                                _logger.LogError("Whisper API error details - Type: {Type}, Code: {Code}, Message: {Message}", 
                                    errorType, errorCode, errorMessage);
                                
                                if (errorCode == "invalid_api_key" || errorCode == "invalid_api_key_format")
                                {
                                    throw new InvalidOperationException(
                                        $"OpenAI API key is invalid or expired. Please check your API key in appsettings.json. " +
                                        $"Error: {errorMessage}");
                                }
                                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                                {
                                    throw new UnauthorizedAccessException(
                                        $"OpenAI API authentication failed. Please verify your API key is valid and has access to Whisper API. " +
                                        $"Error: {errorMessage}");
                                }
                            }
                        }
                        catch (JsonException)
                        {
                            // If response isn't JSON, use the raw content
                        }
                        
                        throw new HttpRequestException($"Whisper API returned {response.StatusCode}: {responseContent}");
                    }

                    // Parse response
                    using var jsonDoc = JsonDocument.Parse(responseContent);
                    
                    if (!jsonDoc.RootElement.TryGetProperty("text", out var textElement))
                    {
                        _logger.LogError("Whisper API response missing 'text' property. Response: {Response}", responseContent);
                        throw new InvalidOperationException("Whisper API response missing 'text' property");
                    }

                    var transcript = textElement.GetString() ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(transcript))
                    {
                        _logger.LogWarning("Whisper API returned empty transcript");
                        throw new InvalidOperationException("Whisper API returned empty transcript - audio may be too short or unclear");
                    }

                    _logger.LogInformation("Audio transcription successful: {TranscriptPreview}...", 
                        transcript.Substring(0, Math.Min(50, transcript.Length)));

                    return transcript.Trim();
                }
                finally
                {
                    // Dispose the memory stream if we created it
                    memoryStreamToDispose?.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error transcribing audio: {Message}\n{StackTrace}", ex.Message, ex.StackTrace);
                throw;
            }
        }

        private string GetContentTypeFromFileName(string fileName)
        {
            var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
            return extension switch
            {
                ".webm" => "audio/webm",
                ".ogg" => "audio/ogg",
                ".mp4" => "audio/mp4",
                ".mpeg" => "audio/mpeg",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".m4a" => "audio/mp4",
                _ => "audio/webm" // Default fallback
            };
        }
    }
}

