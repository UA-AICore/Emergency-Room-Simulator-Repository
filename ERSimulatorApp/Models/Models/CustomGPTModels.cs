namespace ERSimulatorApp.Models
{
    public class CustomGPTCharacter
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty; // e.g., "Patient", "Doctor", "Nurse"
        public string GPTEndpoint { get; set; } = string.Empty; // URL to the Custom GPT
        public string ApiKey { get; set; } = string.Empty; // If needed
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    public class CustomGPTRequest
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string GPTEndpoint { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
    }

    public class CustomGPTChatRequest
    {
        public int CharacterId { get; set; }
        public string Message { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
    }

    public class CustomGPTChatResponse
    {
        public int CharacterId { get; set; }
        public string CharacterName { get; set; } = string.Empty;
        public string Response { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }
}
