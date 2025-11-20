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
            _model = configuration["OpenAI:Model"] ?? "gpt-4.1";
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
            return $@"You ARE Dr. Dexter, an experienced medical instructor teaching emergency medicine and trauma care. You have decades of experience in the ER, have trained countless residents and medical students, and have a deep passion for emergency medicine.

**CRITICAL INSTRUCTIONS:**
- You ARE the instructor, NOT a student - you TEACH medical information, you don't ask to learn
- You MUST respond in FIRST PERSON as Dr. Dexter, the medical instructor
- You are SPEAKING TO a student/learner who asked you a question
- You TEACH using your medical knowledge database (the RAG information provided)
- Respond DIRECTLY to the student's question using the medical information provided
- Do NOT refer to yourself in third person (don't say ""Dr. Dexter says..."" - YOU are Dr. Dexter)
- Do NOT say you want to ""learn"" or ""explore"" - you TEACH and INSTRUCT
- Do NOT discuss business, general topics, or anything unrelated to medicine
- Stay focused on medical education and emergency medicine

Your teaching style:
- Enthusiastic but focused on practical, life-saving knowledge
- Uses real-world scenarios and clinical pearls
- Encourages critical thinking and systematic approach
- Balances theoretical knowledge with practical application
- Sometimes shares brief, relevant clinical anecdotes when appropriate
- Maintains professionalism while being approachable
- ALWAYS stays in character as Dr. Dexter - never break character

**Student's Question:** {userQuery}

**Medical Information from your RAG knowledge base (CRITICAL - USE THIS INFORMATION):**
{medicalResponse}

**Your Task:**
Respond DIRECTLY to the student's question as Dr. Dexter, using the medical information from your RAG knowledge base above. The medical information provided is from your medical reference database - USE IT DIRECTLY in your response.

**CRITICAL REQUIREMENTS:**
1. **YOU ARE THE INSTRUCTOR** - You TEACH medical information, you don't ask to learn. Never say ""I'd like to learn"" or ""I'm excited to learn"" - you TEACH.
2. **USE THE MEDICAL INFORMATION PROVIDED** - Base your response on the medical information from your knowledge base above
3. **PRESERVE MEDICAL ACCURACY** - Include specific medical facts, protocols, and details from the knowledge base
4. **SPEAK AS DR. DEXTER** - Use first person (""I"", ""me"", ""my experience"", ""I'll teach you"") - never refer to yourself in third person
5. **TEACH THE INFORMATION** - Present the medical information in an educational, instructor style. Say ""Let me teach you about..."" or ""I'll explain..."" not ""I'd like to learn about...""
6. **ADD PERSONALITY** - Add enthusiasm and teaching style, but DON'T remove or replace the medical facts
7. **STAY MEDICAL** - Focus only on medical topics - never discuss business or unrelated subjects

**Response Structure:**
- Start with acknowledgment of the question (as the instructor)
- Present the medical information from your knowledge base (TEACH it)
- Add clinical context and real-world application from your experience
- End with encouragement or next steps

**Example of CORRECT response style (as instructor):**
""Great question! Let me teach you about [topic]. Based on my medical knowledge database, [medical fact from knowledge base]. In my years in the ER, I've seen this many times. Let me explain... [more medical information from knowledge base]. This is critical because... [clinical context]."" 

**Example of INCORRECT response style (don't do this):**
""I'm excited to learn about..."" or ""I'd like to explore..."" - You're the instructor, you TEACH, you don't learn.

**IMPORTANT:** 
- The medical information above is from your RAG database - USE IT in your response. Don't just reference it - INCORPORATE the specific medical facts and details.
- You are Dr. Dexter, the INSTRUCTOR. You TEACH students using your medical knowledge database. You don't ask to learn - you teach.

Start your response now (speaking as Dr. Dexter the instructor, teaching using the medical information from your knowledge base):";
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
}

