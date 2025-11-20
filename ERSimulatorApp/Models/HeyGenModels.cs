using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ERSimulatorApp.Models
{
    public class HeyGenStartConversationRequest
    {
        public string AvatarId { get; set; } = string.Empty;
        public string LookId { get; set; } = string.Empty;
        public string Language { get; set; } = "en-US";
        public string VoiceId { get; set; } = string.Empty;
        public string InitialPrompt { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
    }

    public class HeyGenStartConversationResponse
    {
        public string ConversationId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public class HeyGenSendMessageRequest
    {
        public string ConversationId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
    }

    public class HeyGenSendMessageResponse
    {
        public string VideoUrl { get; set; } = string.Empty;
        public string Transcript { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public List<ChatSourceLink> Sources { get; set; } = new();
    }

    public class AvatarSessionRequest
    {
        public string SessionId { get; set; } = string.Empty;
        /// <summary>
        /// Optional: The streaming token used to create the session.
        /// If provided, this token will be reused for the streaming.stop call.
        /// </summary>
        public string? StreamingToken { get; set; }
    }

    public class AvatarMessageRequest
    {
        public string Message { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string ConversationId { get; set; } = string.Empty;
        /// <summary>
        /// Optional: The streaming token used to create the session.
        /// If provided, this token will be reused for the streaming.task call.
        /// </summary>
        public string? StreamingToken { get; set; }
    }

    public class AvatarResponse
    {
        public string VideoUrl { get; set; } = string.Empty;
        public string Transcript { get; set; } = string.Empty;
        public List<ChatSourceLink> Sources { get; set; } = new();
        public string ConversationId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public bool IsFallback { get; set; }
    }

    // Streaming API Models
    public class HeyGenStreamingTokenResponse
    {
        public object? Error { get; set; }
        public HeyGenStreamingTokenData? Data { get; set; }
    }

    public class HeyGenStreamingTokenData
    {
        public string Token { get; set; } = string.Empty;
    }

    public class HeyGenStreamingNewRequest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "v2";
        
        [JsonPropertyName("avatar_id")]
        public string AvatarId { get; set; } = string.Empty;
        
        [JsonPropertyName("avatar_name")]
        public string? AvatarName { get; set; }
        
        [JsonPropertyName("voice")]
        public HeyGenVoiceConfig? Voice { get; set; }
        
        [JsonPropertyName("quality")]
        public string? Quality { get; set; }
        
        [JsonPropertyName("video_encoding")]
        public string? VideoEncoding { get; set; }
    }

    public class HeyGenStreamingNewResponse
    {
        public int Code { get; set; }
        public string Message { get; set; } = string.Empty;
        public HeyGenStreamingSessionData? Data { get; set; }
    }

    public class HeyGenStreamingSessionData
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;
        
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
        
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;
        
        [JsonPropertyName("realtime_endpoint")]
        public string? RealtimeEndpoint { get; set; }
        
        /// <summary>
        /// The streaming API token used to create this session.
        /// This token should be reused for all streaming.task calls for this session.
        /// </summary>
        public string StreamingToken { get; set; } = string.Empty;
    }

    public class HeyGenVoiceConfig
    {
        [JsonPropertyName("voice_id")]
        public string VoiceId { get; set; } = string.Empty;
        
        [JsonPropertyName("rate")]
        public double Rate { get; set; } = 1.0;
    }

    public class HeyGenStreamingTaskRequest
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;
        
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
        
        [JsonPropertyName("task_type")]
        public string TaskType { get; set; } = "talk"; // "talk" or "repeat"
    }
}

