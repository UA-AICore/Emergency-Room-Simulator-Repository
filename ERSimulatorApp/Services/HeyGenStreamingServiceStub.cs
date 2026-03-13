using ERSimulatorApp.Models;

namespace ERSimulatorApp.Services
{
    /// <summary>
    /// Stub used when HeyGen:ApiKey or HeyGen:AvatarId are missing or placeholders.
    /// Throws a clear message so the UI can show "Configure HeyGen" instead of calling the API and getting 401.
    /// </summary>
    public class HeyGenStreamingServiceStub : IHeyGenStreamingService
    {
        private readonly ILogger<HeyGenStreamingServiceStub> _logger;
        private const string Message = "HeyGen is not configured. Set HeyGen:ApiKey and HeyGen:AvatarId in appsettings.Development.json (or appsettings.json) with your HeyGen credentials. Use appsettings.Development.Example.json as a template.";

        public HeyGenStreamingServiceStub(ILogger<HeyGenStreamingServiceStub> logger)
        {
            _logger = logger;
        }

        public Task<string> GetStreamingTokenAsync()
        {
            _logger.LogWarning("HeyGen stub: GetStreamingTokenAsync called - HeyGen is not configured");
            throw new InvalidOperationException(Message);
        }

        public Task<HeyGenStreamingSessionData> CreateStreamingSessionAsync()
        {
            _logger.LogWarning("HeyGen stub: CreateStreamingSessionAsync called - HeyGen is not configured");
            throw new InvalidOperationException(Message);
        }

        public Task StartStreamingSessionAsync(string sessionId, string? streamingToken = null) => Task.CompletedTask;
        public Task SendStreamingTaskAsync(string sessionId, string text, string? streamingToken = null) => Task.CompletedTask;
        public Task StopStreamingSessionAsync(string sessionId, string? streamingToken = null) => Task.CompletedTask;
    }
}
