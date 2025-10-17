using ERSimulatorApp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ERSimulatorApp.Tests
{
    public class OllamaConnectionTest
    {
        public static async Task TestOllamaConnection()
        {
            // Create a simple configuration
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Ollama:Endpoint"] = "http://127.0.0.1:11434/api/generate"
                })
                .Build();

            // Create logger
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactory.CreateLogger<OllamaService>();

            // Create HttpClient
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            // Create OllamaService
            var ollamaService = new OllamaService(httpClient, logger, configuration);

            try
            {
                Console.WriteLine("Testing Ollama connection...");
                Console.WriteLine("Sending test prompt: 'Hello, are you working?'");
                
                var response = await ollamaService.GetResponseAsync("Hello, are you working?");
                
                Console.WriteLine($"✅ SUCCESS! Ollama responded: {response}");
                Console.WriteLine("🎉 Your .NET app is ready to connect to Ollama!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ERROR: {ex.Message}");
                Console.WriteLine("Make sure Ollama is running on http://127.0.0.1:11434");
            }
        }
    }
}
