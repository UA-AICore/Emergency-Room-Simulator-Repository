using ERSimulatorApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace ERSimulatorApp.Controllers
{
    /// <summary>
    /// Controller for HeyGen video generation via proxy server
    /// This is for asynchronous video generation, separate from streaming avatar
    /// </summary>
    [ApiController]
    [Route("api/video")]
    public class VideoProxyController : ControllerBase
    {
        private readonly IHeyGenVideoProxyService _videoProxyService;
        private readonly ILogger<VideoProxyController> _logger;

        public VideoProxyController(
            IHeyGenVideoProxyService videoProxyService,
            ILogger<VideoProxyController> logger)
        {
            _videoProxyService = videoProxyService;
            _logger = logger;
        }

        /// <summary>
        /// Check if the video proxy server is healthy
        /// </summary>
        [HttpGet("health")]
        public async Task<IActionResult> CheckHealth(CancellationToken ct = default)
        {
            try
            {
                var isHealthy = await _videoProxyService.CheckProxyHealthAsync(ct);
                return Ok(new
                {
                    healthy = isHealthy,
                    message = isHealthy ? "Video proxy is healthy" : "Video proxy is not reachable"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking video proxy health");
                return StatusCode(500, new
                {
                    healthy = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// Create a video via the proxy server
        /// </summary>
        [HttpPost("create")]
        public async Task<IActionResult> CreateVideo([FromBody] CreateVideoRequest request, CancellationToken ct = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.AvatarId))
                {
                    return BadRequest(new { error = "AvatarId is required" });
                }

                if (string.IsNullOrWhiteSpace(request.Script))
                {
                    return BadRequest(new { error = "Script is required" });
                }

                _logger.LogInformation("Creating video via proxy - Avatar: {AvatarId}, Script length: {Length}", 
                    request.AvatarId, request.Script.Length);

                var result = await _videoProxyService.CreateVideoAsync(request, ct);

                return Ok(new
                {
                    success = true,
                    requestId = result.RequestId,
                    videoId = result.VideoId,
                    status = result.Status
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating video via proxy");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// Get the status of a video creation request
        /// </summary>
        [HttpGet("status")]
        public async Task<IActionResult> GetVideoStatus([FromQuery] string requestId, CancellationToken ct = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(requestId))
                {
                    return BadRequest(new { error = "RequestId is required" });
                }

                var result = await _videoProxyService.GetVideoStatusAsync(requestId, ct);

                return Ok(new
                {
                    success = true,
                    status = result.Status,
                    videoUrl = result.VideoUrl,
                    requestId = result.RequestId,
                    error = result.Error
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting video status");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }
    }
}

