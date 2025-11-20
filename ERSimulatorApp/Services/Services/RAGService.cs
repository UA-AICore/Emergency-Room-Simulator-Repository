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
                var ragRequest = new
                {
                    model = _model,
                    messages = new[]
                    {
                        new { role = "user", content = prompt }
                    },
                    temperature = 0.7,
                    max_tokens = 2000
                };

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

                // Handle sources - check if response includes sources in metadata or separate field
                var references = new List<SourceReference>();
                
                // Try to extract sources from response metadata or additional fields
                if (ragData.Sources != null && ragData.Sources.Count > 0)
                {
                    // Structured sources format
                    references = ragData.Sources
                        .Select(source =>
                        {
                            var fileNameOnly = Path.GetFileName(source.Filename);
                            var title = Path.GetFileNameWithoutExtension(fileNameOnly);
                            return new SourceReference
                            {
                                Filename = source.Filename,
                                Title = string.IsNullOrWhiteSpace(title) ? fileNameOnly : title,
                                Preview = source.Preview,
                                Similarity = source.Similarity
                            };
                        })
                        .ToList();
                }
                else if (ragData.ContextPreview != null && ragData.ContextPreview.Count > 0)
                {
                    // Context preview format - extract filenames from preview strings
                    var seenFiles = new Dictionary<string, (int index, string preview)>();
                    
                    for (int i = 0; i < ragData.ContextPreview.Count; i++)
                    {
                        var preview = ragData.ContextPreview[i];
                        var match = System.Text.RegularExpressions.Regex.Match(preview, @"\(([^,]+),\s*chunk");
                        if (match.Success)
                        {
                            var filename = match.Groups[1].Value.Trim();
                            if (!seenFiles.ContainsKey(filename) || seenFiles[filename].index > i)
                            {
                                seenFiles[filename] = (i, preview);
                            }
                        }
                    }
                    
                    var index = 0;
                    foreach (var kvp in seenFiles.OrderBy(x => x.Value.index))
                    {
                        var filename = kvp.Key;
                        var preview = kvp.Value.preview;
                        var title = Path.GetFileNameWithoutExtension(filename);
                        var similarity = Math.Max(0.5, 0.95 - (index * 0.1));
                        
                        references.Add(new SourceReference
                        {
                            Filename = filename,
                            Title = string.IsNullOrWhiteSpace(title) ? filename : title,
                            Preview = preview.Length > 200 ? preview.Substring(0, 200) + "..." : preview,
                            Similarity = similarity
                        });
                        index++;
                    }
                }

                _logger.LogInformation($"RAG response received with {references.Count} sources");

                // Format response with sources inline for backward compatibility
                var formattedResponse = answer;
                if (references.Count > 0)
                {
                    formattedResponse += "\n\n📚 Sources:\n";
                    foreach (var reference in references)
                    {
                        if (reference.Similarity > 0)
                        {
                            formattedResponse += $"• {reference.Title} (match: {reference.Similarity:P0})\n";
                        }
                        else
                        {
                            formattedResponse += $"• {reference.Title}\n";
                        }
                    }
                }

                return new LLMResponse
                {
                    Response = formattedResponse,
                    Sources = references ?? new List<SourceReference>(),
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

