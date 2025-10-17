using ERSimulatorApp.Models;
using ERSimulatorApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace ERSimulatorApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly ILLMService _llmService;
        private readonly ChatLogService _logService;
        private readonly ILogger<ChatController> _logger;

        public ChatController(ILLMService llmService, ChatLogService logService, ILogger<ChatController> logger)
        {
            _llmService = llmService;
            _logService = logService;
            _logger = logger;
        }

        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromBody] ChatRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Message))
                {
                    return BadRequest(new { error = "Message cannot be empty" });
                }

                var startTime = DateTime.UtcNow;
                _logger.LogInformation($"Processing chat message for session {request.SessionId}");

                // Get AI response
                var aiResponse = await _llmService.GetResponseAsync(request.Message);
                
                var endTime = DateTime.UtcNow;
                var responseTime = endTime - startTime;

                // Log the conversation
                var logEntry = new ChatLogEntry
                {
                    Timestamp = startTime,
                    SessionId = request.SessionId,
                    UserMessage = request.Message,
                    AIResponse = aiResponse,
                    ResponseTime = responseTime
                };

                _logService.LogChat(logEntry);

                var response = new ChatResponse
                {
                    Response = aiResponse,
                    SessionId = request.SessionId,
                    Timestamp = endTime
                };

                _logger.LogInformation($"Chat response generated in {responseTime.TotalMilliseconds}ms");
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing chat message");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        [HttpGet("logs")]
        public IActionResult GetRecentLogs([FromQuery] int count = 10)
        {
            try
            {
                var logs = _logService.GetRecentLogs(count);
                return Ok(logs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving chat logs");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }
    }
}
