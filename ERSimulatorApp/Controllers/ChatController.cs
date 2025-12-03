using ERSimulatorApp.Models;
using ERSimulatorApp.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Net.Http;
using System.IO;
using System.Linq;

namespace ERSimulatorApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly ILLMService _llmService;
        private readonly ChatLogService _logService;
        private readonly ILogger<ChatController> _logger;
        private readonly string _sourceDocumentsPath;
        private const string OfflineMessage = "I'm sorry, my reference services are offline right now. Please try again later.";

        public ChatController(
            ILLMService llmService, 
            ChatLogService logService, 
            ILogger<ChatController> logger,
            IConfiguration configuration,
            IWebHostEnvironment environment)
        {
            _llmService = llmService;
            _logService = logService;
            _logger = logger;
            var configuredPath = configuration["RAG:SourceDocumentsPath"];
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                _sourceDocumentsPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "rag-local", "sample_data"));
            }
            else
            {
                _sourceDocumentsPath = Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredPath));
            }

            if (!Directory.Exists(_sourceDocumentsPath))
            {
                _logger.LogWarning("Source documents path does not exist: {SourcePath}", _sourceDocumentsPath);
            }
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
                    AIResponse = aiResponse.Response,
                    ResponseTime = responseTime
                };

                _logService.LogChat(logEntry);

                // Build source links with improved logging
                var sourceLinks = aiResponse.Sources
                    .Select(source =>
                    {
                        var url = BuildSourceUrl(source.Filename);
                        var link = new ChatSourceLink
                        {
                            Title = string.IsNullOrWhiteSpace(source.Title)
                                ? Path.GetFileName(source.Filename) ?? "Source"
                                : source.Title,
                            Preview = source.Preview,
                            Similarity = source.Similarity,
                            Url = url
                        };
                        
                        // Log source link creation for debugging
                        _logger.LogInformation("Source link created: Title={Title}, HasUrl={HasUrl}, Filename={Filename}",
                            link.Title, !string.IsNullOrWhiteSpace(url), source.Filename);
                        
                        return link;
                    })
                    .ToList();
                
                var sourcesWithUrls = sourceLinks.Where(link => !string.IsNullOrWhiteSpace(link.Url)).ToList();
                var sourcesWithoutUrls = sourceLinks.Where(link => string.IsNullOrWhiteSpace(link.Url)).ToList();
                
                _logger.LogInformation("Returning {Total} source links - {WithUrls} with URLs, {WithoutUrls} without URLs",
                    sourceLinks.Count,
                    sourcesWithUrls.Count,
                    sourcesWithoutUrls.Count);
                
                // Log details about sources without URLs for debugging
                if (sourcesWithoutUrls.Count > 0)
                {
                    _logger.LogWarning("Sources without URLs (will be filtered out):");
                    foreach (var source in sourcesWithoutUrls)
                    {
                        _logger.LogWarning("  - Title: {Title}, Filename: {Filename}", source.Title, 
                            aiResponse.Sources.FirstOrDefault(s => Path.GetFileName(s.Filename) == source.Title)?.Filename ?? "unknown");
                    }
                }
                
                var response = new ChatResponse
                {
                    Response = aiResponse.Response,
                    SessionId = request.SessionId,
                    Timestamp = endTime,
                    IsFallback = aiResponse.IsFallback,
                    Sources = sourceLinks
                };
                
                // Log the actual response being sent
                _logger.LogInformation("ChatResponse being sent - Response length: {Length}, Sources count: {Count}, IsFallback: {Fallback}",
                    response.Response?.Length ?? 0, response.Sources.Count, response.IsFallback);

                _logger.LogInformation($"Chat response generated in {responseTime.TotalMilliseconds}ms");
                return Ok(response);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Chat request failed due to upstream connection issue.");

                var endTime = DateTime.UtcNow;
                var response = new ChatResponse
                {
                    Response = OfflineMessage,
                    SessionId = request.SessionId,
                    Timestamp = endTime,
                    IsFallback = true,
                    Sources = new List<ChatSourceLink>()
                };

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

        [HttpGet("source-file")]
        public IActionResult GetSourceFile([FromQuery] string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
            {
                return BadRequest(new { error = "Filename is required" });
            }

            var safeFileName = Path.GetFileName(filename);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                return BadRequest(new { error = "Invalid filename" });
            }

            var filePath = Path.Combine(_sourceDocumentsPath, safeFileName);

            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("Requested source file not found: {File}", filePath);
                return NotFound();
            }

            var contentType = GetContentType(filePath);
            return PhysicalFile(filePath, contentType, safeFileName);
        }

        private string BuildSourceUrl(string? sourceFilename)
        {
            if (string.IsNullOrWhiteSpace(sourceFilename))
            {
                _logger.LogDebug("BuildSourceUrl: sourceFilename is null or empty");
                return string.Empty;
            }

            var safeFileName = Path.GetFileName(sourceFilename);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                _logger.LogDebug("BuildSourceUrl: safeFileName is null or empty for {SourceFilename}", sourceFilename);
                return string.Empty;
            }

            var filePath = Path.Combine(_sourceDocumentsPath, safeFileName);
            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("BuildSourceUrl: File not found at {FilePath}, but still creating URL for source: {SafeFileName}", 
                    filePath, safeFileName);
                // Still create the URL even if file doesn't exist - the file might be in a different location
                // or the URL might work anyway
            }

            var url = Url.Action(nameof(GetSourceFile), new { filename = safeFileName });
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning("BuildSourceUrl: Url.Action returned null for filename: {SafeFileName}", safeFileName);
                return string.Empty;
            }
            
            _logger.LogDebug("BuildSourceUrl: Created URL {Url} for filename: {SafeFileName}", url, safeFileName);
            return url;
        }

        private static string GetContentType(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension switch
            {
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".md" => "text/markdown",
                ".html" => "text/html",
                ".htm" => "text/html",
                _ => "application/octet-stream"
            };
        }
    }
}
