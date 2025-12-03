using System.Text;
using System.Text.Json;

namespace ERSimulatorApp.Services
{
    /// <summary>
    /// Service for HeyGen video generation via proxy server
    /// This is separate from streaming avatar - used for asynchronous video generation
    /// </summary>
    public interface IHeyGenVideoProxyService
    {
        Task<bool> CheckProxyHealthAsync(CancellationToken cancellationToken = default);
        Task<CreateVideoResponse> CreateVideoAsync(CreateVideoRequest request, CancellationToken cancellationToken = default);
        Task<VideoStatusResponse> GetVideoStatusAsync(string requestId, CancellationToken cancellationToken = default);
    }

    public class HeyGenVideoProxyService : IHeyGenVideoProxyService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<HeyGenVideoProxyService> _logger;
        private readonly string _proxyBaseUrl;

        public HeyGenVideoProxyService(
            HttpClient httpClient,
            ILogger<HeyGenVideoProxyService> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _proxyBaseUrl = configuration["HeyGen:ProxyBaseUrl"] ?? "http://149.165.154.35:8095";

            // Set base address to proxy
            _httpClient.BaseAddress = new Uri(_proxyBaseUrl);
            
            _logger.LogInformation("HeyGen Video Proxy Service initialized with base URL: {ProxyUrl}", _proxyBaseUrl);
        }

        /// <summary>
        /// Check if the proxy server is healthy and reachable
        /// </summary>
        public async Task<bool> CheckProxyHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Checking proxy health at: {Url}/health", _proxyBaseUrl);
                
                var response = await _httpClient.GetAsync("/health", cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                
                _logger.LogInformation("Proxy health check - Status: {StatusCode}, Response: {Response}", 
                    response.StatusCode, content);
                
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking proxy health");
                return false;
            }
        }

        /// <summary>
        /// Create a video via the proxy server
        /// </summary>
        public async Task<CreateVideoResponse> CreateVideoAsync(CreateVideoRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Creating video via proxy - Avatar: {AvatarId}, Script length: {Length}", 
                    request.AvatarId, request.Script?.Length ?? 0);

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("/v1/video/create", content, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Proxy /v1/video/create failed - Status: {StatusCode}, Response: {Response}", 
                        response.StatusCode, responseBody);
                    throw new HttpRequestException($"Proxy video creation failed: {response.StatusCode} - {responseBody}");
                }

                var result = JsonSerializer.Deserialize<CreateVideoResponse>(responseBody);
                if (result == null)
                {
                    throw new InvalidOperationException("Failed to deserialize create video response");
                }

                _logger.LogInformation("Video creation request submitted - Request ID: {RequestId}", result.RequestId);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating video via proxy");
                throw;
            }
        }

        /// <summary>
        /// Get the status of a video creation request
        /// </summary>
        public async Task<VideoStatusResponse> GetVideoStatusAsync(string requestId, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(requestId))
                {
                    throw new ArgumentException("Request ID cannot be empty", nameof(requestId));
                }

                _logger.LogInformation("Checking video status - Request ID: {RequestId}", requestId);

                var url = $"/v1/video/status?id={Uri.EscapeDataString(requestId)}";
                var response = await _httpClient.GetAsync(url, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Proxy /v1/video/status failed - Status: {StatusCode}, Response: {Response}", 
                        response.StatusCode, responseBody);
                    throw new HttpRequestException($"Proxy video status check failed: {response.StatusCode} - {responseBody}");
                }

                var result = JsonSerializer.Deserialize<VideoStatusResponse>(responseBody);
                if (result == null)
                {
                    throw new InvalidOperationException("Failed to deserialize video status response");
                }

                _logger.LogInformation("Video status retrieved - Status: {Status}, Video URL: {VideoUrl}", 
                    result.Status, result.VideoUrl ?? "Not ready");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting video status");
                throw;
            }
        }
    }

    // Request/Response models for video proxy
    public class CreateVideoRequest
    {
        public string AvatarId { get; set; } = string.Empty;
        public string Script { get; set; } = string.Empty;
        public string? VoiceId { get; set; }
        public string? AspectRatio { get; set; }
        // Add other fields as needed based on proxy API
    }

    public class CreateVideoResponse
    {
        public string RequestId { get; set; } = string.Empty;
        public string? VideoId { get; set; }
        public string? Status { get; set; }
        // Add other fields as needed
    }

    public class VideoStatusResponse
    {
        public string Status { get; set; } = string.Empty;
        public string? VideoUrl { get; set; }
        public string? RequestId { get; set; }
        public string? Error { get; set; }
        // Add other fields as needed
    }
}

