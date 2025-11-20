using ERSimulatorApp.Models;
using System.Collections.Generic;
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

        public async Task<LLMResponse> GetResponseAsync(string prompt)
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

                if (medicalResponse.IsFallback)
                {
                    _logger.LogWarning("Returning fallback message without personality layer because upstream services are offline.");
                    return medicalResponse;
                }

                // Log the raw RAG response to verify it's medical and contains information from RAG database
                _logger.LogInformation("Raw RAG response received from knowledge base (first 300 chars): {RAGPreview}", 
                    medicalResponse.Response.Substring(0, Math.Min(300, medicalResponse.Response.Length)));
                _logger.LogInformation("RAG response contains {SourceCount} source references", medicalResponse.Sources?.Count ?? 0);

                // Add personality layer using Character Gateway
                // This will transform the RAG medical information into Dr. Dexter's teaching style
                // while preserving the medical facts from the RAG database
                _logger.LogInformation("Adding medical instructor personality to RAG response (preserving medical facts)");
                var finalResponse = await _characterGateway.AddPersonalityAsync(medicalResponse.Response, prompt);
                
                // Log the final response after personality layer to verify medical information is preserved
                _logger.LogInformation("Final response after personality layer (first 300 chars): {FinalPreview}", 
                    finalResponse.Substring(0, Math.Min(300, finalResponse.Length)));
                _logger.LogInformation("Medical information from RAG database has been incorporated into Dr. Dexter's response");

                medicalResponse.Response = finalResponse;
                return medicalResponse;
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
                    return new LLMResponse
                    {
                        Response = "I’m sorry, I can’t answer right now because the upstream services are unavailable.",
                        Sources = new List<SourceReference>(),
                        IsFallback = true
                    };
                }
            }
        }
    }
}

