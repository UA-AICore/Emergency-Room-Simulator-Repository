using ERSimulatorApp.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ERSimulatorApp.Services
{
    public interface ICharacterGateway
    {
        Task<string> AddPersonalityAsync(string medicalResponse, string userQuery);
    }

    public class CharacterGatewayService : ICharacterGateway
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<CharacterGatewayService> _logger;
        private readonly bool _useOllama;
        private readonly string? _ollamaEndpoint;
        private readonly string? _ollamaModel;
        private readonly string? _apiKey;
        private readonly string _model;
        private readonly string _openAIBaseUrl;
        /// <summary>True when Ollama or OpenAI is configured. Ctor does not throw so DI can build the graph when Personality:Enabled is false.</summary>
        private readonly bool _configured;

        public CharacterGatewayService(
            HttpClient httpClient,
            ILogger<CharacterGatewayService> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _ollamaEndpoint = configuration["Ollama:Endpoint"]?.Trim();
            _ollamaModel = configuration["Ollama:Model"]?.Trim();
            _useOllama = !string.IsNullOrEmpty(_ollamaEndpoint) && !string.IsNullOrEmpty(_ollamaModel);
            _apiKey = configuration["OpenAI:ApiKey"]?.Trim();
            _model = configuration["OpenAI:Model"] ?? "gpt-4.1";
            _openAIBaseUrl = configuration["OpenAI:BaseUrl"]?.Trim() ?? "https://api.openai.com/v1/chat/completions";
            _configured = _useOllama || !string.IsNullOrEmpty(_apiKey);
        }

        public async Task<string> AddPersonalityAsync(string medicalResponse, string userQuery)
        {
            try
            {
                if (!_configured)
                {
                    throw new InvalidOperationException(
                        "Personality:Enabled requires Ollama (Endpoint + Model) or OpenAI:ApiKey in configuration. " +
                        "Either add those values or set Personality:Enabled to false.");
                }

                _logger.LogInformation("Adding medical instructor personality to response");

                // Validate inputs
                if (string.IsNullOrWhiteSpace(medicalResponse))
                {
                    _logger.LogWarning("Medical response is empty, skipping personality layer");
                    return medicalResponse;
                }

                if (string.IsNullOrWhiteSpace(userQuery))
                {
                    _logger.LogWarning("User query is empty, skipping personality layer");
                    return medicalResponse;
                }

                // Extract sources from medical response before sending to personality layer
                var sourcesSection = ExtractSourcesSection(medicalResponse);
                var medicalContent = RemoveSourcesSection(medicalResponse);

                // Truncate medical content if too long (to avoid token limits)
                const int maxLength = 4000;
                var truncatedContent = medicalContent.Length > maxLength 
                    ? "..." + medicalContent.Substring(medicalContent.Length - maxLength)
                    : medicalContent;

                var personalityPrompt = CreatePersonalityPrompt(truncatedContent, userQuery);
                string? finalContent;

                if (_useOllama)
                {
                    finalContent = await CallOllamaAsync(personalityPrompt);
                }
                else
                {
                    var requestBody = new
                    {
                        model = _model,
                        messages = new[]
                        {
                            new { role = "user", content = personalityPrompt }
                        },
                        temperature = 0.7,
                        max_tokens = 1500
                    };

                    var json = JsonSerializer.Serialize(requestBody);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var request = new HttpRequestMessage(HttpMethod.Post, _openAIBaseUrl) { Content = content };
                    request.Headers.Add("Authorization", $"Bearer {_apiKey}");

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var response = await _httpClient.SendAsync(request, cts.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        _logger.LogError("OpenAI API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                        return medicalResponse;
                    }

                    var responseContent = await response.Content.ReadAsStringAsync();
                    OpenAIResponse? openAIResponse = null;
                    try
                    {
                        openAIResponse = JsonSerializer.Deserialize<OpenAIResponse>(responseContent);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, "Failed to deserialize OpenAI response. JSON: {JsonContent}",
                            responseContent.Length > 500 ? responseContent.Substring(0, 500) : responseContent);
                        return medicalResponse;
                    }

                    finalContent = openAIResponse?.Choices?.FirstOrDefault()?.Message?.Content;
                }
                if (string.IsNullOrEmpty(finalContent))
                {
                    _logger.LogWarning("OpenAI returned null response, using original");
                    return medicalResponse;
                }

                _logger.LogInformation("Successfully added medical instructor personality");
                
                // Append sources section back to the response
                var finalResponseWithSources = finalContent;
                if (!string.IsNullOrEmpty(sourcesSection))
                {
                    finalResponseWithSources += "\n\n" + sourcesSection;
                }
                
                return finalResponseWithSources;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Personality layer canceled or timed out, using original RAG response");
                return medicalResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding personality to response");
                return medicalResponse;
            }
        }

        private const string OllamaSystemPersonality = "You are Dr. Dexter, an ER physician teaching a student. Reply in 2–3 short, natural sentences. Sound like a real person talking—warm and conversational, use 'you,' no stiff or list-like answers. Stay in character; never refuse. Do not sound like an AI or a textbook.";

        private async Task<string?> CallOllamaAsync(string prompt)
        {
            var requestBody = new
            {
                model = _ollamaModel,
                messages = new[]
                {
                    new { role = "system", content = OllamaSystemPersonality },
                    new { role = "user", content = prompt }
                },
                stream = false,
                options = new { num_predict = 120 }
            };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            // Short timeout so we don't hold HeyGen session; on timeout we use raw RAG response
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await _httpClient.PostAsync(_ollamaEndpoint!, content, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Ollama API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            OllamaChatResponse? ollamaResponse = null;
            try
            {
                ollamaResponse = JsonSerializer.Deserialize<OllamaChatResponse>(responseContent);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to deserialize Ollama response");
                return null;
            }

            return ollamaResponse?.Message?.Content;
        }

        private string CreatePersonalityPrompt(string medicalResponse, string userQuery)
        {
            return $@"Student asked: {userQuery}

Medical info (use this, stay accurate):
{medicalResponse}

Reply as Dr. Dexter in 2–3 short sentences. Reply in the SAME language the student used (e.g. if they asked in Spanish, reply in Spanish; if in English, reply in English). Sound like a real person talking to a student—warm, natural, use ""you."" No lists, no bullet points, no textbook tone. Teach the key point from the info above and maybe one practical takeaway or follow-up question.";
        }

        /// <summary>
        /// Extracts the sources section (📚 Sources: ...) from the medical response
        /// </summary>
        private string ExtractSourcesSection(string medicalResponse)
        {
            var sourcesMarker = "📚 Sources:";
            var sourcesIndex = medicalResponse.IndexOf(sourcesMarker);
            
            if (sourcesIndex >= 0)
            {
                return medicalResponse.Substring(sourcesIndex);
            }
            
            return string.Empty;
        }

        /// <summary>
        /// Removes the sources section from the medical response
        /// </summary>
        private string RemoveSourcesSection(string medicalResponse)
        {
            if (string.IsNullOrEmpty(medicalResponse))
                return medicalResponse;

            // Use regex to find and remove sources section more reliably
            // Pattern matches: "📚 Sources:" or "Sources:" followed by optional newlines and ALL bullet points until end
            // This pattern will match everything from "📚 Sources:" to the end of the string
            var sourcesPattern = @"(\n\s*)?📚\s*Sources:?\s*\n?(\s*[•\-\*]\s+[^\n]+(\n|$))*.+$";
            var match = System.Text.RegularExpressions.Regex.Match(medicalResponse, sourcesPattern, 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | 
                System.Text.RegularExpressions.RegexOptions.Multiline | 
                System.Text.RegularExpressions.RegexOptions.Singleline);
            
            string cleaned;
            if (match.Success)
            {
                cleaned = medicalResponse.Substring(0, match.Index).TrimEnd();
                _logger.LogInformation($"Regex matched sources section starting at index {match.Index}");
            }
            else
            {
                cleaned = medicalResponse;
            }

            // Also try simpler patterns if regex didn't catch it
            if (cleaned == medicalResponse)
            {
                var sourceMarkers = new[]
                {
                    "\n📚 Sources:\n",
                    "\n📚 Sources\n",
                    "\n📚 Sources:",
                    "📚 Sources:\n",
                    "📚 Sources:",
                    "\nSources:\n",
                    "\nSources\n",
                    "\nSources:",
                    "Sources:\n",
                    "Sources:"
                };

                foreach (var marker in sourceMarkers)
                {
                    var index = medicalResponse.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0)
                    {
                        _logger.LogInformation($"Found sources marker '{marker}' at index {index}, removing sources section");
                        cleaned = medicalResponse.Substring(0, index).TrimEnd();
                        break;
                    }
                }
            }
            else
            {
                _logger.LogInformation("Removed sources section using regex pattern");
            }

            // If still not cleaned, try a more aggressive approach: find "📚" and remove everything after it
            // Try multiple ways to find the emoji
            if (cleaned == medicalResponse)
            {
                var bookIndex = -1;
                
                // Try different methods to find the emoji
                bookIndex = medicalResponse.IndexOf("📚", StringComparison.Ordinal);
                if (bookIndex < 0)
                {
                    // Try with Unicode normalization
                    bookIndex = medicalResponse.IndexOf("\U0001F4DA", StringComparison.Ordinal);
                }
                if (bookIndex < 0)
                {
                    // Try case-insensitive search for "Sources" and work backwards
                    var sourcesIndex = medicalResponse.LastIndexOf("Sources", StringComparison.OrdinalIgnoreCase);
                    if (sourcesIndex >= 0)
                    {
                        // Look backwards for the emoji
                        var beforeSources = medicalResponse.Substring(0, sourcesIndex);
                        bookIndex = beforeSources.LastIndexOf("📚", StringComparison.Ordinal);
                        if (bookIndex < 0)
                        {
                            // If no emoji found, just remove from "Sources"
                            bookIndex = sourcesIndex;
                        }
                    }
                }
                
                if (bookIndex >= 0)
                {
                    _logger.LogInformation($"Found sources marker at index {bookIndex}, removing everything after it");
                    cleaned = medicalResponse.Substring(0, bookIndex).TrimEnd();
                }
                else
                {
                    _logger.LogWarning("Could not find sources marker in response");
                }
            }

            // Final cleanup - remove any trailing whitespace, newlines, or bullet points
            cleaned = cleaned.TrimEnd('\n', '\r', ' ', '\t');
            
            // Remove any trailing bullet points that might have been left
            while (cleaned.EndsWith("•") || cleaned.EndsWith("-") || cleaned.EndsWith("*"))
            {
                cleaned = cleaned.TrimEnd('•', '-', '*', ' ', '\n', '\r', '\t');
            }

            return cleaned;
        }
    }

    public class OpenAIResponse
    {
        [JsonPropertyName("choices")]
        public List<OpenAIChoice>? Choices { get; set; }
    }

    public class OpenAIChoice
    {
        [JsonPropertyName("message")]
        public OpenAIMessage? Message { get; set; }
    }

    public class OpenAIMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    public class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaMessage? Message { get; set; }
    }

    public class OllamaMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}

