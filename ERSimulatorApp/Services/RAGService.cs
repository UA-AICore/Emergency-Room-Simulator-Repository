using ERSimulatorApp.Models;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ERSimulatorApp.Services
{
    public class RAGService : ILLMService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<RAGService> _logger;
        private readonly string _ragBaseUrl;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly int _topK;

        public RAGService(HttpClient httpClient, ILogger<RAGService> logger, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _ragBaseUrl = configuration["RAG:BaseUrl"] ?? "https://aicore-healthcareteam-llm-server.tra220030.projects.jetstream-cloud.org/v1/chat/completions";
            _apiKey = configuration["RAG:ApiKey"] ?? string.Empty;
            _model = configuration["RAG:Model"] ?? "meta-llama/Llama-3.2-1B-instruct";
            _topK = configuration.GetValue<int?>("RAG:TopK") ?? 5;
        }

        private const string FallbackMessage = "I'm sorry, my reference services are offline right now. Please try again later.";

        public async Task<LLMResponse> GetResponseAsync(string prompt)
        {
            try
            {
                // OpenAI-compatible chat completions request format
                // Try to request sources by including additional parameters
                var ragRequest = new Dictionary<string, object>
                {
                    { "model", _model },
                    { "messages", new[]
                        {
                            new { role = "user", content = prompt }
                        }
                    },
                    { "temperature", 0.7 },
                    { "max_tokens", 2000 }
                };
                
                // Add parameters that might request sources (if supported by the RAG API)
                // Different RAG implementations use different parameter names
                ragRequest["include_sources"] = true;
                ragRequest["return_sources"] = true;
                ragRequest["include_metadata"] = true;
                ragRequest["top_k"] = _topK; // Request top K sources

                // Use default serialization (camelCase for dictionary keys)
                var ragJson = JsonSerializer.Serialize(ragRequest);
                var ragContent = new StringContent(ragJson, Encoding.UTF8, "application/json");

                _logger.LogInformation($"Sending prompt to RAG server: {prompt.Substring(0, Math.Min(50, prompt.Length))}...");

                // Create request with headers to avoid thread-safety issues
                var requestMessage = new HttpRequestMessage(HttpMethod.Post, _ragBaseUrl)
                {
                    Content = ragContent
                };
                
                // Add API key if configured
                if (!string.IsNullOrWhiteSpace(_apiKey))
                {
                    requestMessage.Headers.Add("Authorization", $"Bearer {_apiKey}");
                }

                var ragResponse = await _httpClient.SendAsync(requestMessage);
                
                if (!ragResponse.IsSuccessStatusCode)
                {
                    var errorContent = await ragResponse.Content.ReadAsStringAsync();
                    _logger.LogError($"RAG API error: {ragResponse.StatusCode} - {errorContent}");
                    throw new HttpRequestException($"RAG API returned {ragResponse.StatusCode}: {errorContent}");
                }

                var ragResponseContent = await ragResponse.Content.ReadAsStringAsync();
                
                // Log the raw response for debugging
                _logger.LogInformation($"RAG API raw response (first 500 chars): {ragResponseContent.Substring(0, Math.Min(500, ragResponseContent.Length))}");
                
                var ragData = JsonSerializer.Deserialize<OpenAIChatResponse>(ragResponseContent);

                if (ragData == null || ragData.Choices == null || ragData.Choices.Count == 0 || string.IsNullOrEmpty(ragData.Choices[0].Message?.Content))
                {
                    _logger.LogError("RAG returned null or empty response");
                    throw new InvalidOperationException("RAG returned null or empty response");
                }

                var answer = ragData.Choices?[0]?.Message?.Content ?? string.Empty;
                if (string.IsNullOrEmpty(answer))
                {
                    _logger.LogError("RAG returned empty content in response");
                    throw new InvalidOperationException("RAG returned empty content");
                }

                return new LLMResponse
                {
                    Response = answer,
                    Sources = new List<SourceReference>(),
                    IsFallback = false
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling RAG API");
                return new LLMResponse
                {
                    Response = FallbackMessage,
                    Sources = new List<SourceReference>(),
                    IsFallback = true
                };
            }
        }
    }

    // OpenAI-compatible chat completions response
    public class OpenAIChatResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
        
        [JsonPropertyName("object")]
        public string? Object { get; set; }
        
        [JsonPropertyName("created")]
        public long? Created { get; set; }
        
        [JsonPropertyName("model")]
        public string? Model { get; set; }
        
        [JsonPropertyName("choices")]
        public List<OpenAIChoice>? Choices { get; set; }
        
        [JsonPropertyName("usage")]
        public OpenAIUsage? Usage { get; set; }
        
        // Extended fields for RAG sources (if provided)
        [JsonPropertyName("sources")]
        public List<RAGSource>? Sources { get; set; }
        
        [JsonPropertyName("context_preview")]
        public List<string>? ContextPreview { get; set; }
    }
    
    public class OpenAIUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int? PromptTokens { get; set; }
        
        [JsonPropertyName("completion_tokens")]
        public int? CompletionTokens { get; set; }
        
        [JsonPropertyName("total_tokens")]
        public int? TotalTokens { get; set; }
    }
    
    // Keep old RAGResponse for backward compatibility if needed
    public class RAGResponse
    {
        [JsonPropertyName("answer")]
        public string Answer { get; set; } = string.Empty;
        
        [JsonPropertyName("model")]
        public string? Model { get; set; }
        
        [JsonPropertyName("rag_used")]
        public bool? RagUsed { get; set; }
        
        [JsonPropertyName("sources")]
        public List<RAGSource>? Sources { get; set; }
        
        [JsonPropertyName("context_preview")]
        public List<string>? ContextPreview { get; set; }
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

