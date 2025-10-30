using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ERSimulatorApp.Services
{
    public class RAGService : ILLMService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<RAGService> _logger;
        private readonly string _ragEndpoint;
        private readonly string _ollamaEndpoint;
        private readonly string _model;

        public RAGService(HttpClient httpClient, ILogger<RAGService> logger, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _ragEndpoint = configuration["RAG:Endpoint"] ?? "http://127.0.0.1:5001";
            _ollamaEndpoint = configuration["Ollama:Endpoint"] ?? "http://127.0.0.1:11434/api/generate";
            _model = configuration["Ollama:Model"] ?? "alibayram/medgemma:4b";
        }

        public async Task<string> GetResponseAsync(string prompt)
        {
            try
            {
                // Try RAG server first
                try
                {
                    var ragRequest = new
                    {
                        q = prompt
                    };

                    var ragJson = JsonSerializer.Serialize(ragRequest);
                    var ragContent = new StringContent(ragJson, Encoding.UTF8, "application/json");

                    _logger.LogInformation($"Sending prompt to RAG server: {prompt.Substring(0, Math.Min(50, prompt.Length))}...");

                    var ragResponse = await _httpClient.PostAsync($"{_ragEndpoint}/api/ask", ragContent);
                    
                    if (ragResponse.IsSuccessStatusCode)
                    {
                        var ragResponseContent = await ragResponse.Content.ReadAsStringAsync();
                        var ragData = JsonSerializer.Deserialize<RAGResponse>(ragResponseContent);

                        if (ragData != null && !string.IsNullOrEmpty(ragData.Answer))
                        {
                            _logger.LogInformation($"RAG response received with {ragData.Sources?.Count ?? 0} sources");
                            
                            // Format response with sources
                            var formattedResponse = ragData.Answer;
                            if (ragData.Sources != null && ragData.Sources.Any())
                            {
                                formattedResponse += "\n\n📚 Sources:\n";
                                foreach (var source in ragData.Sources)
                                {
                                    var filename = source.Filename.Split('/').LastOrDefault() ?? source.Filename;
                                    formattedResponse += $"• {filename} (match: {source.Similarity:P0})\n";
                                }
                            }
                            
                            return formattedResponse;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"RAG server not available, falling back to direct Ollama: {ex.Message}");
                }

                // Fallback to direct Ollama
                var requestBody = new
                {
                    model = _model,
                    prompt = prompt,
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _logger.LogInformation($"Sending prompt to Ollama: {prompt.Substring(0, Math.Min(50, prompt.Length))}...");

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
                _logger.LogError(ex, "Error calling RAG/Ollama API");
                throw;
            }
        }
    }

    public class RAGResponse
    {
        [JsonPropertyName("answer")]
        public string Answer { get; set; } = string.Empty;
        
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;
        
        [JsonPropertyName("rag_used")]
        public bool RagUsed { get; set; }
        
        [JsonPropertyName("sources")]
        public List<RAGSource>? Sources { get; set; }
    }

    public class RAGSource
    {
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;
        
        [JsonPropertyName("similarity")]
        public double Similarity { get; set; }
        
        [JsonPropertyName("preview")]
        public string Preview { get; set; } = string.Empty;
    }

}

