using ERSimulatorApp.Models;

namespace ERSimulatorApp.Services;

public class QuizService
{
    private readonly HttpClient _client;
    private readonly ILogger<QuizService> _logger;

    public QuizService(HttpClient client, ILogger<QuizService> logger, IConfiguration configuration)
    {
        _client = client;
        _logger = logger;

        var baseAddress = configuration["RAG:Endpoint"] ?? "http://127.0.0.1:5001";
        _client.BaseAddress = new Uri(baseAddress);
        _client.Timeout = TimeSpan.FromSeconds(configuration.GetValue("RAG:QuizTimeoutSeconds", 120));
    }

    public async Task<QuizResponse?> GetQuizAsync(string topic, int questionCount = 5, int choicesPerQuestion = 4)
    {
        var payload = new
        {
            topic,
            question_count = questionCount,
            choices_per_question = choicesPerQuestion
        };

        try
        {
            var response = await _client.PostAsJsonAsync("/api/quiz", payload);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Quiz endpoint returned {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<QuizResponse>();
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Quiz request timed out for topic {Topic}", topic);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling quiz endpoint for topic {Topic}", topic);
            return null;
        }
    }
}

