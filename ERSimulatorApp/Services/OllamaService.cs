using ERSimulatorApp.Models;
using System.Text;
using System.Text.Json;

namespace ERSimulatorApp.Services
{
    public interface ILLMService
    {
        Task<string> GetResponseAsync(string prompt);
    }

    public class OllamaService : ILLMService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OllamaService> _logger;
        private readonly string _ollamaEndpoint;

        public OllamaService(HttpClient httpClient, ILogger<OllamaService> logger, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _ollamaEndpoint = configuration["Ollama:Endpoint"] ?? "http://127.0.0.1:11434/api/generate";
        }

        public async Task<string> GetResponseAsync(string prompt)
        {
            try
            {
                _logger.LogInformation($"Sending prompt to Ollama: {prompt.Substring(0, Math.Min(50, prompt.Length))}...");

                var requestBody = new
                {
                    model = "phi3:mini",
                    prompt = prompt,
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_ollamaEndpoint, content);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"Ollama API error: {response.StatusCode} - {errorContent}");
                    throw new HttpRequestException($"Ollama API returned {response.StatusCode}: {errorContent}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var ollamaResponse = JsonSerializer.Deserialize<OllamaResponse>(responseContent);

                if (ollamaResponse?.Response == null)
                {
                    _logger.LogError("Ollama returned null response");
                    throw new InvalidOperationException("Ollama returned null response");
                }

                _logger.LogInformation($"Ollama response received: {ollamaResponse.Response.Substring(0, Math.Min(50, ollamaResponse.Response.Length))}...");
                return ollamaResponse.Response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Ollama API");
                throw;
            }
        }
    }

    public class OllamaResponse
    {
        public string Model { get; set; } = string.Empty;
        public string Response { get; set; } = string.Empty;
        public bool Done { get; set; }
    }

    public class ChatLogService
    {
        private readonly string _logFilePath;
        private readonly object _lockObject = new object();

        public ChatLogService()
        {
            _logFilePath = Path.Combine(Directory.GetCurrentDirectory(), "chat_logs.txt");
        }

        public void LogChat(ChatLogEntry entry)
        {
            lock (_lockObject)
            {
                var logLine = $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] Session: {entry.SessionId}\n" +
                             $"User: {entry.UserMessage}\n" +
                             $"AI: {entry.AIResponse}\n" +
                             $"Response Time: {entry.ResponseTime.TotalMilliseconds}ms\n" +
                             "---\n";
                
                File.AppendAllText(_logFilePath, logLine);
            }
        }

        public List<ChatLogEntry> GetRecentLogs(int count = 10)
        {
            lock (_lockObject)
            {
                if (!File.Exists(_logFilePath))
                    return new List<ChatLogEntry>();

                var lines = File.ReadAllLines(_logFilePath);
                var entries = new List<ChatLogEntry>();
                
                // Simple parsing - in a real app, you'd want more robust parsing
                for (int i = 0; i < lines.Length - 4; i += 5)
                {
                    if (lines[i].StartsWith("[") && lines[i].Contains("Session:"))
                    {
                        try
                        {
                            var timestampStr = lines[i].Substring(1, 19);
                            var timestamp = DateTime.ParseExact(timestampStr, "yyyy-MM-dd HH:mm:ss", null);
                            
                            var sessionId = lines[i].Split("Session: ")[1];
                            var userMessage = lines[i + 1].Replace("User: ", "");
                            var aiResponse = lines[i + 2].Replace("AI: ", "");
                            var responseTimeStr = lines[i + 3].Replace("Response Time: ", "").Replace("ms", "");
                            
                            entries.Add(new ChatLogEntry
                            {
                                Timestamp = timestamp,
                                SessionId = sessionId,
                                UserMessage = userMessage,
                                AIResponse = aiResponse,
                                ResponseTime = TimeSpan.FromMilliseconds(double.Parse(responseTimeStr))
                            });
                        }
                        catch
                        {
                            // Skip malformed entries
                            continue;
                        }
                    }
                }
                
                return entries.TakeLast(count).ToList();
            }
        }
    }
}
