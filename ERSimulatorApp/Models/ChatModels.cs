namespace ERSimulatorApp.Models
{
    public class ChatRequest
    {
        public string Message { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
    }

    public class ChatResponse
    {
        public string Response { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }

    public class ChatLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string UserMessage { get; set; } = string.Empty;
        public string AIResponse { get; set; } = string.Empty;
        public TimeSpan ResponseTime { get; set; }
    }
}
