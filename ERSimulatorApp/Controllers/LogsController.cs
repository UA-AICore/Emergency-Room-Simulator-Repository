using ERSimulatorApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace ERSimulatorApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LogsController : ControllerBase
    {
        private readonly ChatLogService _logService;
        private readonly ILogger<LogsController> _logger;

        public LogsController(ChatLogService logService, ILogger<LogsController> logger)
        {
            _logService = logService;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult GetRecentLogs([FromQuery] int count = 10)
        {
            try
            {
                var logs = _logService.GetRecentLogs(count);
                return Ok(logs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving logs");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }
    }
}
