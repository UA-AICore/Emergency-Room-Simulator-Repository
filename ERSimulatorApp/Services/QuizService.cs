using ERSimulatorApp.Models;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ERSimulatorApp.Services;

public class QuizService
{
    private readonly HttpClient _client;
    private readonly ILogger<QuizService> _logger;
    private readonly string _ragBaseUrl;
    private readonly string _apiKey;
    private readonly string _model;

    public QuizService(HttpClient client, ILogger<QuizService> logger, IConfiguration configuration)
    {
        _client = client;
        _logger = logger;
        _ragBaseUrl = configuration["RAG:BaseUrl"] ?? "https://aicore-healthcareteam-llm-server.tra220030.projects.jetstream-cloud.org/v1/chat/completions";
        _apiKey = configuration["RAG:ApiKey"] ?? string.Empty;
        _model = configuration["RAG:Model"] ?? "meta-llama/Llama-3.2-1B-instruct";
        _client.Timeout = TimeSpan.FromSeconds(configuration.GetValue("RAG:TimeoutSeconds", 120));
    }

    public async Task<QuizResponse?> GetQuizAsync(string topic, int questionCount = 5, int choicesPerQuestion = 4)
    {
        try
        {
            // Create a prompt that asks the RAG model to generate quiz questions in JSON format
            var prompt = $@"Generate {questionCount} multiple-choice quiz questions about {topic} for medical students learning emergency medicine and trauma care.

Each question should have:
- A clear, clinically relevant question
- {choicesPerQuestion} answer options (only one correct answer)
- A brief rationale explaining why the correct answer is right
- Source hints indicating which medical documents or protocols support the answer

Return the response as a valid JSON array with this exact structure:
[
  {{
    ""question"": ""Question text here"",
    ""options"": [""Option A"", ""Option B"", ""Option C"", ""Option D""],
    ""correctIndex"": 0,
    ""rationale"": ""Explanation of the correct answer"",
    ""sourceHints"": [""Source document name or protocol""]
  }}
]

Make sure the questions are based on evidence-based medical practices and trauma care protocols. Focus on practical, clinically relevant scenarios.";

            // OpenAI-compatible chat completions request format
            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                },
                temperature = 0.7,
                max_tokens = 3000 // More tokens for quiz generation
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation($"Generating quiz for topic: {topic}");

            // Create request with headers to avoid thread-safety issues
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, _ragBaseUrl)
            {
                Content = content
            };

            // Add API key if configured
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                requestMessage.Headers.Add("Authorization", $"Bearer {_apiKey}");
            }

            var response = await _client.SendAsync(requestMessage);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Quiz generation failed: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var chatResponse = JsonSerializer.Deserialize<OpenAIChatResponse>(responseContent);

            if (chatResponse == null || chatResponse.Choices == null || chatResponse.Choices.Count == 0)
            {
                _logger.LogWarning("Quiz API returned empty response");
                return null;
            }

            var answerText = chatResponse.Choices[0]?.Message?.Content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(answerText))
            {
                _logger.LogWarning("Quiz API returned empty content");
                return null;
            }

            // Try to extract JSON from the response (might be wrapped in markdown code blocks)
            var jsonText = ExtractJsonFromResponse(answerText);
            
            // Parse the quiz questions from JSON
            var questions = JsonSerializer.Deserialize<List<QuizQuestion>>(jsonText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (questions == null || questions.Count == 0)
            {
                _logger.LogWarning("Failed to parse quiz questions from response");
                return null;
            }

            // Extract sources if available
            var sources = new List<ChatSourceLink>();
            if (chatResponse.Sources != null && chatResponse.Sources.Count > 0)
            {
                sources = chatResponse.Sources.Select(s => new ChatSourceLink
                {
                    Title = Path.GetFileNameWithoutExtension(s.Filename) ?? s.Filename,
                    Preview = s.Preview,
                    Similarity = s.Similarity,
                    Url = string.Empty // Sources URLs would need to be built similar to ChatController
                }).ToList();
            }

            _logger.LogInformation($"Successfully generated {questions.Count} quiz questions for topic: {topic}");

            return new QuizResponse
            {
                Questions = questions,
                Sources = sources
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse quiz JSON response for topic {Topic}", topic);
            return null;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Quiz request timed out for topic {Topic}", topic);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error generating quiz for topic {Topic}", topic);
            return null;
        }
    }

    private string ExtractJsonFromResponse(string response)
    {
        // Try to extract JSON from markdown code blocks
        var jsonBlockMatch = System.Text.RegularExpressions.Regex.Match(
            response, 
            @"```(?:json)?\s*(\[[\s\S]*?\])\s*```",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        
        if (jsonBlockMatch.Success)
        {
            return jsonBlockMatch.Groups[1].Value;
        }

        // Try to find JSON array directly
        var arrayMatch = System.Text.RegularExpressions.Regex.Match(
            response,
            @"(\[[\s\S]*\])"
        );

        if (arrayMatch.Success)
        {
            return arrayMatch.Groups[1].Value;
        }

        // If no JSON found, return the original response (might be plain JSON)
        return response.Trim();
    }
}


