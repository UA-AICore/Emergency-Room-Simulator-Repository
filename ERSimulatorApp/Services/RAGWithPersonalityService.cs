using System.Text;
using System.Text.Json;

namespace ERSimulatorApp.Services
{
    public class RAGWithPersonalityService : ILLMService
    {
        private readonly RAGService _ragService;
        private readonly ICharacterGateway _characterGateway;
        private readonly ILogger<RAGWithPersonalityService> _logger;
        private readonly IConfiguration _configuration;

        public RAGWithPersonalityService(
            RAGService ragService,
            ICharacterGateway characterGateway,
            ILogger<RAGWithPersonalityService> logger,
            IConfiguration configuration)
        {
            _ragService = ragService;
            _characterGateway = characterGateway;
            _logger = logger;
            _configuration = configuration;
        }

        public async Task<string> GetResponseAsync(string prompt)
        {
            try
            {
                // Check if personality is enabled
                var personalityEnabled = _configuration.GetValue<bool?>("Personality:Enabled") ?? true;

                if (!personalityEnabled)
                {
                    _logger.LogInformation("Personality layer disabled, using raw RAG response");
                    return await _ragService.GetResponseAsync(prompt);
                }

                // Get medical response from RAG + MedGemma
                _logger.LogInformation($"Getting medical response from RAG for: {prompt.Substring(0, Math.Min(50, prompt.Length))}...");
                var medicalResponse = await _ragService.GetResponseAsync(prompt);

                // Add personality layer using Character Gateway
                _logger.LogInformation("Adding medical instructor personality to response");
                var finalResponse = await _characterGateway.AddPersonalityAsync(medicalResponse, prompt);

                return finalResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RAG with Personality Service");
                // Fallback to basic response if personality fails
                try
                {
                    return await _ragService.GetResponseAsync(prompt);
                }
                catch
                {
                    throw;
                }
            }
        }
    }
}

