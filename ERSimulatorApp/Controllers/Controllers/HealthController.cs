using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ERSimulatorApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class HealthController : ControllerBase
    {
        private readonly ILogger<HealthController> _logger;
        private readonly IConfiguration _configuration;

        public HealthController(ILogger<HealthController> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
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
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var ragBaseUrl = _configuration["RAG:BaseUrl"] ?? "https://unchid-promonopoly-tiera.ngrok-free.dev";
                
                // Add ngrok header to bypass browser warning page
                if (ragBaseUrl.Contains("ngrok-free.dev"))
                {
                    httpClient.DefaultRequestHeaders.Add("ngrok-skip-browser-warning", "true");
                }
                
                var response = await httpClient.GetAsync($"{ragBaseUrl}/health");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    try
                    {
                        // Parse the JSON to extract LLM connection info
                        var healthData = JsonSerializer.Deserialize<JsonElement>(content);
                        var llmMode = healthData.TryGetProperty("llm_mode", out var llmModeProp) ? llmModeProp.GetString() : "unknown";
                        var model = healthData.TryGetProperty("model", out var modelProp) ? modelProp.GetString() : "unknown";
                        var docsIndexed = healthData.TryGetProperty("docs_indexed", out var docsProp) ? docsProp.GetInt32() : 0;
                        
                        return new 
                        { 
                            status = "up", 
                            llmMode = llmMode,
                            model = model,
                            docsIndexed = docsIndexed,
                            rawData = content
                        };
                    }
                    catch
                    {
                        // If parsing fails, return raw data
                        return new { status = "up", data = content };
                    }
                }
                return new { status = "down", statusCode = (int)response.StatusCode };
            }
            catch (Exception ex)
            {
                return new { status = "down", error = ex.Message };
            }
        }
    }
}








