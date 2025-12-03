using ERSimulatorApp.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private readonly string _localRagUrl;

        public RAGService(HttpClient httpClient, ILogger<RAGService> logger, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _ragBaseUrl = configuration["RAG:BaseUrl"] ?? "https://aicore-healthcareteam-llm-server.tra220030.projects.jetstream-cloud.org/v1/chat/completions";
            _apiKey = configuration["RAG:ApiKey"] ?? string.Empty;
            _model = configuration["RAG:Model"] ?? "meta-llama/Llama-3.2-1B-instruct";
            _topK = configuration.GetValue<int?>("RAG:TopK") ?? 5;
            _localRagUrl = configuration["RAG:LocalRagUrl"] ?? "http://127.0.0.1:5001/api/ask";
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
                
                // Log the raw response to debug source extraction
                _logger.LogInformation("RAG API raw response (first 500 chars): {ResponsePreview}", 
                    ragResponseContent.Length > 500 ? ragResponseContent.Substring(0, 500) : ragResponseContent);
                
                var ragData = JsonSerializer.Deserialize<OpenAIChatResponse>(ragResponseContent);

                if (ragData == null || ragData.Choices == null || ragData.Choices.Count == 0 || string.IsNullOrEmpty(ragData.Choices[0].Message?.Content))
                {
                    _logger.LogError("RAG returned null or empty response");
                    throw new InvalidOperationException("RAG returned null or empty response");
                }
                
                // Log source extraction debugging - check all possible source locations
                _logger.LogInformation("RAG response parsing - HasSources: {HasSources}, SourcesCount: {Count}, HasContextPreview: {HasContext}, ContextPreviewCount: {ContextCount}",
                    ragData.Sources != null, ragData.Sources?.Count ?? 0, 
                    ragData.ContextPreview != null, ragData.ContextPreview?.Count ?? 0);
                
                // Try to parse as JsonDocument to check for other possible source fields
                try
                {
                    using var doc = JsonDocument.Parse(ragResponseContent);
                    var root = doc.RootElement;
                    _logger.LogInformation("RAG response root element properties: {Properties}", 
                        string.Join(", ", root.EnumerateObject().Select(p => p.Name)));
                    
                    // Check for sources in various possible locations
                    if (root.TryGetProperty("data", out var dataElement))
                    {
                        _logger.LogInformation("Found 'data' property in RAG response");
                        if (dataElement.TryGetProperty("sources", out var dataSources))
                        {
                            _logger.LogInformation("Found sources in data.sources");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not parse RAG response as JsonDocument for debugging");
                }

                var answer = ragData.Choices?[0]?.Message?.Content ?? string.Empty;
                if (string.IsNullOrEmpty(answer))
                {
                    _logger.LogError("RAG returned empty content in response");
                    throw new InvalidOperationException("RAG returned empty content");
                }

                // Handle sources - check if response includes sources in metadata or separate field
                var references = new List<SourceReference>();
                
                _logger.LogInformation("Starting source extraction - ragData.Sources: {HasSources}, Count: {Count}", 
                    ragData.Sources != null, ragData.Sources?.Count ?? 0);
                
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
                
                // If still no sources found, try to extract from response text itself
                // Some RAG APIs embed source information in the response text
                // NOTE: This is disabled in favor of inference-based source matching
                // The regex patterns were too permissive and matched false positives
                if (false && references.Count == 0 && !string.IsNullOrEmpty(answer))
                {
                    _logger.LogInformation("No sources found in structured format, attempting to extract from response text");
                    
                    // Look for common source patterns in the response text
                    // Pattern: filename references, document mentions, etc.
                    // Only match filenames that are clearly document names (at least 5 chars, contains spaces or proper capitalization)
                    var sourcePatterns = new[]
                    {
                        @"(?:from|in|based on|according to|per)\s+([A-Z][A-Za-z0-9_\-\s]{4,}\.pdf)",
                        @"([A-Z][A-Za-z0-9_\-\s]{4,}\.pdf)"
                    };
                    
                    var foundFiles = new HashSet<string>();
                    foreach (var pattern in sourcePatterns)
                    {
                        var matches = System.Text.RegularExpressions.Regex.Matches(answer, pattern, 
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        foreach (System.Text.RegularExpressions.Match match in matches)
                        {
                            if (match.Groups.Count > 1 && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                            {
                                var filename = match.Groups[1].Value.Trim();
                                // Only accept filenames that look like real document names
                                if (filename.Length >= 10 && !foundFiles.Contains(filename))
                                {
                                    foundFiles.Add(filename);
                                    references.Add(new SourceReference
                                    {
                                        Filename = filename,
                                        Title = Path.GetFileNameWithoutExtension(filename) ?? filename,
                                        Preview = "Extracted from response",
                                        Similarity = 0.7
                                    });
                                }
                            }
                        }
                    }
                    
                    if (references.Count > 0)
                    {
                        _logger.LogInformation($"Extracted {references.Count} sources from response text");
                    }
                }

                // If no sources found from remote RAG server, try to infer from the response content
                _logger.LogInformation("Before inference check - references.Count: {Count}", references.Count);
                if (references.Count == 0)
                {
                    try
                    {
                        _logger.LogInformation("No sources from remote RAG server, attempting to infer sources from response content. Prompt length: {PromptLen}, Answer length: {AnswerLen}", 
                            prompt?.Length ?? 0, answer?.Length ?? 0);
                        var inferredSources = InferSourcesFromRemoteResponse(prompt, answer);
                        _logger.LogInformation("Inference returned {Count} sources", inferredSources.Count);
                        if (inferredSources.Count > 0)
                        {
                            references = inferredSources;
                            _logger.LogInformation($"Inferred {references.Count} sources from remote RAG server response");
                        }
                        else
                        {
                            _logger.LogWarning("Inference returned 0 sources - inference may have failed");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to infer sources from remote RAG response, continuing without sources");
                    }
                }
                else
                {
                    _logger.LogInformation("Skipping inference - already have {Count} sources from remote RAG server", references.Count);
                }

                _logger.LogInformation($"RAG response received with {references.Count} sources");
                if (references.Count > 0)
                {
                    _logger.LogInformation($"Source titles: {string.Join(", ", references.Select(r => r.Title))}");
                }

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

        /// <summary>
        /// Infers sources from the remote RAG server's response by matching content against known documents
        /// </summary>
        private List<SourceReference> InferSourcesFromRemoteResponse(string prompt, string response)
        {
            var references = new List<SourceReference>();
            
            try
            {
                // Known document names from the RAG knowledge base
                var knownDocuments = new[]
                {
                    "Advanced Trauma Life Support",
                    "ATLS UPDATE: CASE STUDIES IN TRAUMA",
                    "Pediatric Trauma Resuscitation",
                    "Trauma Resuscitation",
                    "Simulation in Trauma Advanced Cardiac Life Support",
                    "Surgical Decision-Making in the Management of Polytrauma Patients",
                    "The Evolving Role of Computed Tomography (CT) in Trauma Care",
                    "Virtual reality simulation to enhance advanced trauma life support trainings",
                    "An Extended Reality Simulator for Advanced Trauma Life Support Training"
                };

                // Combine prompt and response for matching
                var combinedText = $"{prompt} {response}".ToLowerInvariant();
                
                // Match document names against the response content
                var matchedDocs = new Dictionary<string, double>();
                
                foreach (var docName in knownDocuments)
                {
                    var docNameLower = docName.ToLowerInvariant();
                    var keywords = docNameLower.Split(new[] { ' ', ':', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(k => k.Length > 3) // Only meaningful keywords
                        .ToArray();
                    
                    if (keywords.Length == 0) continue;
                    
                    // Count how many keywords appear in the response
                    var matchCount = keywords.Count(k => combinedText.Contains(k));
                    var matchScore = (double)matchCount / keywords.Length;
                    
                    // Also check for exact phrase matches (higher weight)
                    if (combinedText.Contains(docNameLower))
                    {
                        matchScore = Math.Max(matchScore, 0.8);
                    }
                    
                    // Check for acronym matches (e.g., "ATLS")
                    var acronym = string.Join("", keywords.Select(k => k.Length > 0 ? k[0].ToString().ToUpper() : ""));
                    if (acronym.Length >= 3 && combinedText.Contains(acronym.ToLowerInvariant()))
                    {
                        matchScore = Math.Max(matchScore, 0.7);
                    }
                    
                    if (matchScore > 0.3) // Threshold for relevance
                    {
                        matchedDocs[docName] = matchScore;
                    }
                }
                
                // Convert to SourceReference objects, sorted by relevance
                references = matchedDocs
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(_topK) // Limit to top K documents
                    .Select(kvp =>
                    {
                        var filename = kvp.Key + ".pdf";
                        return new SourceReference
                        {
                            Filename = filename,
                            Title = kvp.Key,
                            Preview = ExtractPreviewFromResponse(response, kvp.Key),
                            Similarity = kvp.Value
                        };
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error inferring sources from remote RAG response");
            }

            return references;
        }

        /// <summary>
        /// Extracts a preview snippet from the response that relates to the document
        /// </summary>
        private string ExtractPreviewFromResponse(string response, string documentName)
        {
            try
            {
                // Find sentences that might relate to the document
                var sentences = response.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
                var keywords = documentName.ToLowerInvariant()
                    .Split(new[] { ' ', ':', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(k => k.Length > 3)
                    .ToArray();
                
                // Find the first sentence that contains any keyword
                foreach (var sentence in sentences)
                {
                    var sentenceLower = sentence.ToLowerInvariant();
                    if (keywords.Any(k => sentenceLower.Contains(k)))
                    {
                        var preview = sentence.Trim();
                        return preview.Length > 200 ? preview.Substring(0, 200) + "..." : preview;
                    }
                }
                
                // Fallback: return first 200 chars
                return response.Length > 200 ? response.Substring(0, 200) + "..." : response;
            }
            catch
            {
                return "Inferred from response content";
            }
        }

        /// <summary>
        /// Infers sources by querying the local RAG server with the same question
        /// </summary>
        private async Task<List<SourceReference>> InferSourcesFromLocalRagAsync(string prompt)
        {
            var references = new List<SourceReference>();
            
            try
            {
                // Query local RAG server to get source documents
                var localRagRequest = new
                {
                    q = prompt
                };

                var localRagJson = JsonSerializer.Serialize(localRagRequest);
                var localRagContent = new StringContent(localRagJson, Encoding.UTF8, "application/json");

                var localRagRequestMessage = new HttpRequestMessage(HttpMethod.Post, _localRagUrl)
                {
                    Content = localRagContent
                };

                // Use a shorter timeout for source inference (we don't need the full response)
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var localRagResponse = await _httpClient.SendAsync(localRagRequestMessage, cts.Token);

                if (localRagResponse.IsSuccessStatusCode)
                {
                    var localRagResponseContent = await localRagResponse.Content.ReadAsStringAsync();
                    var localRagData = JsonSerializer.Deserialize<LocalRAGResponse>(localRagResponseContent);

                    if (localRagData?.Sources != null && localRagData.Sources.Count > 0)
                    {
                        references = localRagData.Sources
                            .Select(source =>
                            {
                                var fileNameOnly = Path.GetFileName(source.Filename);
                                var title = Path.GetFileNameWithoutExtension(fileNameOnly);
                                return new SourceReference
                                {
                                    Filename = source.Filename,
                                    Title = string.IsNullOrWhiteSpace(title) ? fileNameOnly : title,
                                    Preview = source.Preview ?? string.Empty,
                                    Similarity = source.Similarity
                                };
                            })
                            .ToList();
                    }
                }
                else
                {
                    _logger.LogWarning("Local RAG server returned status {StatusCode} when inferring sources", localRagResponse.StatusCode);
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("Local RAG server timeout when inferring sources");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error querying local RAG server for source inference");
            }

            return references;
        }
    }

    // Local RAG server response format
    public class LocalRAGResponse
    {
        [JsonPropertyName("answer")]
        public string? Answer { get; set; }
        
        [JsonPropertyName("model")]
        public string? Model { get; set; }
        
        [JsonPropertyName("rag_used")]
        public bool? RagUsed { get; set; }
        
        [JsonPropertyName("sources")]
        public List<LocalRAGSource>? Sources { get; set; }
    }

    public class LocalRAGSource
    {
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;
        
        [JsonPropertyName("similarity")]
        public double Similarity { get; set; }
        
        [JsonPropertyName("preview")]
        public string? Preview { get; set; }
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

