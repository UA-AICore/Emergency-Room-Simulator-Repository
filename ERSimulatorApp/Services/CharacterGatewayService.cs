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
        private readonly string _apiKey;
        private readonly string _model;
        private readonly string _openAIBaseUrl;

        public CharacterGatewayService(
            HttpClient httpClient, 
            ILogger<CharacterGatewayService> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI API key not found");
            _model = configuration["OpenAI:Model"] ?? "gpt-4o-mini";
            _openAIBaseUrl = "https://api.openai.com/v1/chat/completions";
        }

        public async Task<string> AddPersonalityAsync(string medicalResponse, string userQuery)
        {
            try
            {
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

                var requestBody = new
                {
                    model = _model,
                    messages = new[]
                    {
                        new { role = "user", content = personalityPrompt }
                    },
                    temperature = 0.7,
                    max_tokens = 1500  // Reduced for faster response
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Create request with headers to avoid thread-safety issues
                var request = new HttpRequestMessage(HttpMethod.Post, _openAIBaseUrl)
                {
                    Content = content
                };
                request.Headers.Add("Authorization", $"Bearer {_apiKey}");

                // Use a shorter timeout for personality layer (20 seconds)
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var response = await _httpClient.SendAsync(request, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"OpenAI API error: {response.StatusCode} - {errorContent}");
                    // Return original response if personality layer fails
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

                var finalContent = openAIResponse?.Choices?.FirstOrDefault()?.Message?.Content;
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
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("Personality layer timed out after 20 seconds, using original response");
                // Return original response if personality layer times out
                return medicalResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding personality to response");
                // Return original response if personality layer fails
                return medicalResponse;
            }
        }

        private string CreatePersonalityPrompt(string medicalResponse, string userQuery)
        {
            return $@"You are an experienced medical instructor teaching emergency medicine and trauma care. You have decades of experience in the ER, have trained countless residents and medical students, and have a deep passion for emergency medicine.

Your teaching style:
- Enthusiastic but focused on practical, life-saving knowledge
- Uses real-world scenarios and clinical pearls
- Encourages critical thinking and systematic approach
- Balances theoretical knowledge with practical application
- Sometimes shares brief, relevant clinical anecdotes when appropriate
- Maintains professionalism while being approachable

**User's Question:** {userQuery}

**Medical Information from MedGemma (RAG-enhanced medical AI):**
{medicalResponse}

**Your Task:**
Transform this medical information into a response that sounds like an experienced medical instructor teaching in the ER. The response should:
1. Be accurate and based on the provided medical information
2. Have personality and enthusiasm for emergency medicine
3. Provide clinical context and real-world application
4. Be clear, educational, and inspiring
5. Maintain the medical accuracy from the source material

Start your response now:";
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
            var sourcesMarker = "📚 Sources:";
            var sourcesIndex = medicalResponse.IndexOf(sourcesMarker);
            
            if (sourcesIndex >= 0)
            {
                return medicalResponse.Substring(0, sourcesIndex).TrimEnd();
            }
            
            return medicalResponse;
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
}

