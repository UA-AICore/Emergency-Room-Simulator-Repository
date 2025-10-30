using Microsoft.AspNetCore.Mvc;

namespace ERSimulatorApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class HealthController : ControllerBase
    {
        private readonly ILogger<HealthController> _logger;

        public HealthController(ILogger<HealthController> logger)
        {
            _logger = logger;
        }

        [HttpGet]
        public IActionResult Get()
        {
            return Ok(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                version = "1.0.0"
            });
        }

        [HttpGet("detailed")]
        public async Task<IActionResult> GetDetailed()
        {
            var healthChecks = new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                checks = new
                {
                    ollama = await CheckOllamaHealth(),
                    ragServer = await CheckRAGServerHealth()
                }
            };

            return Ok(healthChecks);
        }

        private async Task<object> CheckOllamaHealth()
        {
            try
            {
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var response = await httpClient.GetAsync("http://127.0.0.1:11434/api/tags");
                return new { status = response.IsSuccessStatusCode ? "up" : "down", statusCode = (int)response.StatusCode };
            }
            catch
            {
                return new { status = "down", error = "Connection refused" };
            }
        }

        private async Task<object> CheckRAGServerHealth()
        {
            try
            {
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var response = await httpClient.GetAsync("http://127.0.0.1:5001/health");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return new { status = "up", data = content };
                }
                return new { status = "down", statusCode = (int)response.StatusCode };
            }
            catch
            {
                return new { status = "down", error = "Connection refused" };
            }
        }
    }
}


