using System.Collections.Generic;

namespace ERSimulatorApp.Models
{
    public class ChatRequest
    {
        public string Message { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        /// <summary>When true, use Claude (RAG:ClaudeModel) instead of local Ollama/gemma for the answer.</summary>
        public bool UseClaude { get; set; }
    }

    public class ChatResponse
    {
        public string Response { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public List<ChatSourceLink> Sources { get; set; } = new();
        public bool IsFallback { get; set; }
    }

    public class ChatLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string UserMessage { get; set; } = string.Empty;
        public string AIResponse { get; set; } = string.Empty;
        public TimeSpan ResponseTime { get; set; }
    }

    public class ChatSourceLink
    {
        public string Title { get; set; } = string.Empty;
        public string Preview { get; set; } = string.Empty;
        public double Similarity { get; set; }
        public string Url { get; set; } = string.Empty;
    }

    public class SourceReference
    {
        public string Filename { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Preview { get; set; } = string.Empty;
        public double Similarity { get; set; }
    }

    public class LLMResponse
    {
        public string Response { get; set; } = string.Empty;
        public List<SourceReference> Sources { get; set; } = new();
        public bool IsFallback { get; set; }
    }

    /// <summary>Single message in conversation history (e.g. Avatar context).</summary>
    public class ConversationMessage
    {
        public string Role { get; set; } = string.Empty; // "user" or "assistant"
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
