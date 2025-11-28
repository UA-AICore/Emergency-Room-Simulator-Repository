using ERSimulatorApp.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ERSimulatorApp.Services
{
    public interface ILLMService
    {
        Task<LLMResponse> GetResponseAsync(string prompt);
    }

    public class OllamaService : ILLMService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OllamaService> _logger;
        private readonly string _ollamaEndpoint;
        private readonly string _model;

        public OllamaService(HttpClient httpClient, ILogger<OllamaService> logger, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _ollamaEndpoint = configuration["Ollama:Endpoint"] ?? "http://127.0.0.1:11434/api/generate";
            _model = configuration["Ollama:Model"] ?? "phi3:mini";
        }

        public async Task<LLMResponse> GetResponseAsync(string prompt)
        {
            try
            {
                _logger.LogInformation($"Sending prompt to Ollama: {prompt.Substring(0, Math.Min(50, prompt.Length))}...");

                var requestBody = new
                {
                    model = _model,
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
                return new LLMResponse
                {
                    Response = ollamaResponse.Response
                };
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
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;
        
        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;
        
        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }

    public class ChatLogService
    {
        private readonly string _logFilePath;
        private readonly object _lockObject = new object();

        public ChatLogService()
        {
            // Use /app/data for persistent storage in container, fallback to current directory for local dev
            var dataDir = Directory.Exists("/app/data") ? "/app/data" : Directory.GetCurrentDirectory();
            _logFilePath = Path.Combine(dataDir, "chat_logs.txt");
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

                var fileContent = File.ReadAllText(_logFilePath);
                var entries = new List<ChatLogEntry>();
                
                // Split by entry separator "---"
                var entryBlocks = fileContent.Split(new[] { "---\n", "---\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var block in entryBlocks)
                {
                    try
                    {
                        var lines = block.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length < 4) continue;
                        
                        // First line should be timestamp and session
                        if (!lines[0].StartsWith("[") || !lines[0].Contains("Session:")) continue;
                        
                        // Extract timestamp
                        var timestampMatch = Regex.Match(lines[0], @"\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]");
                        if (!timestampMatch.Success) continue;
                        var timestamp = DateTime.ParseExact(timestampMatch.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", null);
                        
                        // Extract session ID
                        var sessionMatch = Regex.Match(lines[0], @"Session: (.+)");
                        if (!sessionMatch.Success) continue;
                        var sessionId = sessionMatch.Groups[1].Value;
                        
                        // Find User: and AI: lines
                        string userMessage = "";
                        string aiResponse = "";
                        string responseTimeStr = "";
                        
                        for (int i = 1; i < lines.Length; i++)
                        {
                            if (lines[i].StartsWith("User: "))
                            {
                                userMessage = lines[i].Substring(6);
                            }
                            else if (lines[i].StartsWith("AI: "))
                            {
                                // AI response can span multiple lines until "Response Time:"
                                var aiLines = new System.Collections.Generic.List<string>();
                                aiLines.Add(lines[i].Substring(4));
                                
                                // Continue adding lines until we hit "Response Time:"
                                i++;
                                while (i < lines.Length && !lines[i].StartsWith("Response Time: "))
                                {
                                    if (!string.IsNullOrWhiteSpace(lines[i]))
                                        aiLines.Add(lines[i]);
                                    i++;
                                }
                                aiResponse = string.Join("\n", aiLines);
                                
                                // Get response time
                                if (i < lines.Length && lines[i].StartsWith("Response Time: "))
                                {
                                    responseTimeStr = lines[i].Substring(15).Replace("ms", "").Trim();
                                }
                                break;
                            }
                        }
                        
                        if (!double.TryParse(responseTimeStr, out var responseTimeMs))
                        {
                            responseTimeMs = 0;
                        }
                        
                        entries.Add(new ChatLogEntry
                        {
                            Timestamp = timestamp,
                            SessionId = sessionId,
                            UserMessage = userMessage,
                            AIResponse = aiResponse,
                            ResponseTime = TimeSpan.FromMilliseconds(responseTimeMs)
                        });
                    }
                    catch
                    {
                        // Skip malformed entries
                        continue;
                    }
                }
                
                return entries.TakeLast(count).ToList();
            }
        }
    }
}
