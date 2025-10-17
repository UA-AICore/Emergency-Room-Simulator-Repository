using ERSimulatorApp.Models;

namespace ERSimulatorApp.Services
{
    public interface ILLMService
    {
        Task<string> GetResponseAsync(string prompt);
    }

    public class MockLLMService : ILLMService
    {
        private readonly Random _random = new Random();
        private readonly List<string> _responses = new List<string>
        {
            "I understand your concern. Let me help you with that.",
            "That's an interesting question. Based on my medical knowledge...",
            "I can see you're dealing with a complex situation. Here's what I recommend...",
            "From an emergency medicine perspective, this requires immediate attention.",
            "Let me analyze the symptoms you've described and provide guidance.",
            "This is a common presentation in the ER. Here's the standard approach...",
            "I need more information to provide accurate medical advice.",
            "Based on the patient's condition, I would suggest the following protocol...",
            "That's a critical situation that requires immediate intervention.",
            "Let me walk you through the differential diagnosis for this case."
        };

        public async Task<string> GetResponseAsync(string prompt)
        {
            // Simulate API call delay
            await Task.Delay(_random.Next(1000, 3000));
            
            // Return a random response for now
            return _responses[_random.Next(_responses.Count)];
        }
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
