using System.Collections.Generic;

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

    /// <summary>
    /// Conversation state for patient avatar - tracks emotional state and conversation history
    /// </summary>
    public class PatientConversationState
    {
        public string SessionId { get; set; } = string.Empty;
        public List<ConversationMessage> History { get; set; } = new();
        public PatientEmotionalState EmotionalState { get; set; } = PatientEmotionalState.Neutral;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }

    public class ConversationMessage
    {
        public string Role { get; set; } = string.Empty; // "user" or "patient"
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public enum PatientEmotionalState
    {
        Calm = 0,
        Neutral = 1,
        Anxious = 2,
        Confused = 3,
        Agitated = 4,
        Angry = 5
    }
}
