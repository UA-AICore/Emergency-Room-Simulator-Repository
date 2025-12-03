using System.IO;
using System.Text;
using System.Text.Json;

namespace ERSimulatorApp.Services
{
    /// <summary>
    /// Service for transcribing audio using OpenAI's Audio Transcription API.
    /// Uses whisper-1 model (standard and most reliable).
    /// </summary>
    public interface IWhisperService
    {
        Task<string> TranscribeAudioAsync(Stream audioStream, string fileName = "audio.webm", CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Implementation of WhisperService using OpenAI's Audio Transcription API.
    /// Uses whisper-1 model (standard and most reliable).
    /// </summary>
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
            
            // Resolve API key with multiple fallbacks
            _apiKey = configuration["OpenAI:ApiKey"]
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                ?? Environment.GetEnvironmentVariable("OpenAI__ApiKey");
            
            _apiUrl = "https://api.openai.com/v1/audio/transcriptions";

            // Validate API key
            if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey.Contains("USE USER", StringComparison.OrdinalIgnoreCase))
            {
                var errorMsg = "OpenAI API key is not configured. Set with: dotnet user-secrets set \"OpenAI:ApiKey\" \"sk-...\" or set OPENAI_API_KEY environment variable";
                _logger.LogError(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            // Set base address (matching working WhisperTest app pattern)
            _httpClient.BaseAddress = new Uri("https://api.openai.com/v1/");
            
            // DO NOT set Authorization header on HttpClient - set it per request instead
            // This prevents conflicts and ensures the correct key is used for each request

            // Log key status (without exposing the actual key)
            _logger.LogInformation("Whisper Service initialized with API key (length: {KeyLength}, starts with: {KeyPrefix}, ends with: {KeySuffix})", 
                _apiKey.Length, 
                _apiKey.Substring(0, Math.Min(10, _apiKey.Length)),
                _apiKey.Substring(Math.Max(0, _apiKey.Length - 10)));
            
            // Verify API key format
            if (!_apiKey.StartsWith("sk-"))
            {
                _logger.LogWarning("API key does not start with 'sk-' - may be invalid format");
            }
            
            // Log where the key came from for debugging
            if (configuration["OpenAI:ApiKey"] != null)
            {
                var keySource = "appsettings.json";
                // Check if we're in Development mode (which uses appsettings.Development.json)
                var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                if (env == "Development")
                {
                    keySource = "appsettings.Development.json (overrides appsettings.json)";
                }
                _logger.LogInformation("API key loaded from {Source}. Key length: {Length}, starts with sk-: {StartsWithSk}", 
                    keySource, _apiKey.Length, _apiKey.StartsWith("sk-"));
            }
            else if (Environment.GetEnvironmentVariable("OPENAI_API_KEY") != null)
            {
                _logger.LogInformation("API key loaded from OPENAI_API_KEY environment variable. Key length: {Length}, starts with sk-: {StartsWithSk}", 
                    _apiKey.Length, _apiKey.StartsWith("sk-"));
            }
            else if (Environment.GetEnvironmentVariable("OpenAI__ApiKey") != null)
            {
                _logger.LogInformation("API key loaded from OpenAI__ApiKey environment variable. Key length: {Length}, starts with sk-: {StartsWithSk}", 
                    _apiKey.Length, _apiKey.StartsWith("sk-"));
            }
        }

        public async Task<string> TranscribeAudioAsync(Stream audioStream, string fileName = "audio.webm", CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting audio transcription for file: {FileName}", fileName);

                // Validate fileName
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = "audio.webm";
                    _logger.LogWarning("FileName was null or empty, defaulting to: {FileName}", fileName);
                }

                // Determine content type from file extension
                var contentType = GetContentTypeFromFileName(fileName);
                _logger.LogInformation("Detected content type: {ContentType} for file: {FileName}", contentType, fileName);

                // Validate stream is readable
                if (!audioStream.CanRead)
                {
                    _logger.LogError("Audio stream is not readable");
                    throw new InvalidOperationException("Audio stream is not readable - cannot transcribe");
                }

                // Reset stream position if seekable (matching working WhisperTest app pattern)
                if (audioStream.CanSeek && audioStream.Position > 0)
                {
                    audioStream.Position = 0;
                    _logger.LogInformation("Reset audio stream position to 0");
                }

                // Create multipart form data (matching working WhisperTest app pattern)
                using var content = new MultipartFormDataContent();
                
                // Add the audio file with proper content type
                var audioContent = new StreamContent(audioStream);
                audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                content.Add(audioContent, "file", fileName);

                // Add model parameter - using standard Whisper model
                // whisper-1 is the standard and most reliable model
                var modelName = "whisper-1";
                content.Add(new StringContent(modelName), "model");
                _logger.LogInformation("Using transcription model: {Model}", modelName);

                // Add response format (matching working WhisperTest app)
                content.Add(new StringContent("json"), "response_format");

                // Add language (optional, but helps with accuracy)
                content.Add(new StringContent("en"), "language");

                // Log request details
                _logger.LogInformation("Sending audio to Whisper API: ContentType: {ContentType}, FileName: {FileName}", 
                    contentType, fileName);
                _logger.LogInformation("Using API key (length: {KeyLength}, prefix: {KeyPrefix})", 
                    _apiKey?.Length ?? 0, _apiKey?.Substring(0, Math.Min(10, _apiKey?.Length ?? 0)) ?? "null");

                // Create request message to ensure Authorization header is set correctly
                var request = new HttpRequestMessage(HttpMethod.Post, "audio/transcriptions")
                {
                    Content = content
                };
                
                // CRITICAL: Set Authorization header on the request (not on HttpClient)
                // This ensures the correct API key is used and prevents header conflicts
                if (string.IsNullOrWhiteSpace(_apiKey))
                {
                    _logger.LogError("API key is null or empty when trying to send request!");
                    throw new InvalidOperationException("API key is not configured");
                }
                
                // Remove any existing Authorization header first
                if (request.Headers.Contains("Authorization"))
                {
                    request.Headers.Remove("Authorization");
                }
                
                // Set Authorization header using proper AuthenticationHeaderValue
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
                
                _logger.LogInformation("Set Authorization header on request (key length: {KeyLength}, prefix: {KeyPrefix}, starts with sk-: {StartsWithSk})", 
                    _apiKey.Length, 
                    _apiKey.Substring(0, Math.Min(10, _apiKey.Length)),
                    _apiKey.StartsWith("sk-"));
                
                // Verify header was set correctly
                if (request.Headers.Authorization == null || request.Headers.Authorization.Scheme != "Bearer")
                {
                    _logger.LogError("Authorization header was not set correctly on request! Scheme: {Scheme}", 
                        request.Headers.Authorization?.Scheme ?? "null");
                    throw new InvalidOperationException("Failed to set Authorization header correctly");
                }

                // Send request
                _logger.LogInformation("Sending audio to Whisper API...");
                var response = await _httpClient.SendAsync(request, cancellationToken);
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError("=== WHISPER API ERROR ===");
                        _logger.LogError("Status Code: {StatusCode}", response.StatusCode);
                        _logger.LogError("Full Response: {Response}", responseContent);
                        _logger.LogError("Model Used: whisper-1");
                        _logger.LogError("Request URL: {Url}", _apiUrl);
                        _logger.LogError("API Key Length: {KeyLength}, Prefix: {KeyPrefix}, Starts with sk-: {StartsWithSk}", 
                            _apiKey?.Length ?? 0, 
                            _apiKey?.Substring(0, Math.Min(10, _apiKey?.Length ?? 0)) ?? "null",
                            _apiKey?.StartsWith("sk-") ?? false);
                        
                        // Parse error details if available
                        string? errorMessage = null;
                        string? errorCode = null;
                        string? errorType = null;
                        
                        try
                        {
                            using var errorDoc = JsonDocument.Parse(responseContent);
                            if (errorDoc.RootElement.TryGetProperty("error", out var errorElement))
                            {
                                errorMessage = errorElement.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown error";
                                errorCode = errorElement.TryGetProperty("code", out var code) ? code.GetString() : "Unknown code";
                                errorType = errorElement.TryGetProperty("type", out var type) ? type.GetString() : "Unknown type";
                                
                                _logger.LogError("Whisper API error details - Type: {Type}, Code: {Code}, Message: {Message}", 
                                    errorType, errorCode, errorMessage);
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(ex, "Could not parse error response as JSON, using raw content");
                            errorMessage = responseContent;
                        }
                        
                        // Handle specific error cases - distinguish between auth errors and model errors
                        // Model errors should NOT be treated as authentication failures
                        if (errorCode == "model_not_found" || errorMessage?.Contains("model") == true && errorMessage?.Contains("not found") == true)
                        {
                            var modelError = $"OpenAI model error: {errorMessage ?? responseContent}. " +
                                           $"Status: {response.StatusCode}, Code: {errorCode ?? "N/A"}";
                            _logger.LogError(modelError);
                            _logger.LogError("This is a MODEL error, not an authentication error. Check if the model name is correct.");
                            throw new InvalidOperationException(modelError);
                        }
                        
                        // Only treat actual authentication errors as UnauthorizedAccessException
                        if (errorCode == "invalid_api_key" || errorCode == "invalid_api_key_format" || 
                            (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && errorCode != "model_not_found"))
                        {
                            var detailedError = $"OpenAI API authentication failed. Status: {response.StatusCode}, " +
                                              $"Code: {errorCode ?? "N/A"}, Message: {errorMessage ?? responseContent}";
                            _logger.LogError(detailedError);
                            _logger.LogError("API Key being used - Length: {KeyLength}, Prefix: {KeyPrefix}, Full response: {Response}", 
                                _apiKey?.Length ?? 0, 
                                _apiKey?.Substring(0, Math.Min(15, _apiKey?.Length ?? 0)) ?? "null",
                                responseContent);
                            throw new UnauthorizedAccessException(detailedError);
                        }
                        
                        // Generic HTTP error
                        var httpError = $"Whisper API returned {response.StatusCode}. " +
                                      $"Error: {errorMessage ?? responseContent}";
                        _logger.LogError(httpError);
                    throw new HttpRequestException(httpError);
                }

                // Parse response
                _logger.LogInformation("Whisper API response received");
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

