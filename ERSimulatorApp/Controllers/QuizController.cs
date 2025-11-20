using ERSimulatorApp.Models;
using ERSimulatorApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace ERSimulatorApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class QuizController : ControllerBase
{
    private readonly QuizService _quizService;
    private readonly ILogger<QuizController> _logger;

    public QuizController(QuizService quizService, ILogger<QuizController> logger)
    {
        _quizService = quizService;
        _logger = logger;
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] QuizRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Topic))
        {
            return BadRequest(new { error = "Topic is required." });
        }

        var quiz = await _quizService.GetQuizAsync(request.Topic, request.QuestionCount, request.ChoicesPerQuestion);

        if (quiz == null || quiz.Questions.Count == 0)
        {
            _logger.LogWarning("Quiz generation failed for topic {Topic}", request.Topic);
            return StatusCode(503, new { error = "Unable to generate quiz right now." });
        }

        return Ok(quiz);
    }
}

public class QuizRequest
{
    public string Topic { get; set; } = string.Empty;
    public int QuestionCount { get; set; } = 5;
    public int ChoicesPerQuestion { get; set; } = 4;
}








