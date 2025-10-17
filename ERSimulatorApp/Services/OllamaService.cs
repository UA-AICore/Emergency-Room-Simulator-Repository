using ERSimulatorApp.Models;
using System.Text;
using System.Text.Json;

namespace ERSimulatorApp.Services
{
    public class OllamaService : ILLMService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OllamaService> _logger;
        private readonly string _ollamaEndpoint;

        public OllamaService(HttpClient httpClient, ILogger<OllamaService> logger, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _ollamaEndpoint = configuration["Ollama:Endpoint"] ?? "http://127.0.0.1:11434/api/generate";
        }

        public async Task<string> GetResponseAsync(string prompt)
        {
            try
            {
                _logger.LogInformation($"Sending prompt to Ollama: {prompt.Substring(0, Math.Min(50, prompt.Length))}...");

                var requestBody = new
                {
                    model = "phi3:mini",
                    prompt = prompt,
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_ollamaEndpoint, content);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"Ollama API error: {response.StatusCode} - {errorContent}");
                    throw new HttpRequestException($"Ollama API returned {response.StatusCode}: {errorContent}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var ollamaResponse = JsonSerializer.Deserialize<OllamaResponse>(responseContent);

                if (ollamaResponse?.Response == null)
                {
                    _logger.LogError("Ollama returned null response");
                    throw new InvalidOperationException("Ollama returned null response");
                }

                _logger.LogInformation($"Ollama response received: {ollamaResponse.Response.Substring(0, Math.Min(50, ollamaResponse.Response.Length))}...");
                return ollamaResponse.Response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Ollama API");
                throw;
            }
        }
    }

    public class OllamaResponse
    {
        public string Model { get; set; } = string.Empty;
        public string Response { get; set; } = string.Empty;
        public bool Done { get; set; }
    }
}
