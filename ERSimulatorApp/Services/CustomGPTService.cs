using ERSimulatorApp.Models;
using System.Text;
using System.Text.Json;

namespace ERSimulatorApp.Services
{
    public interface ICustomGPTService
    {
        Task<List<CustomGPTCharacter>> GetAllCharactersAsync();
        Task<CustomGPTCharacter?> GetCharacterByIdAsync(int id);
        Task<CustomGPTCharacter> CreateCharacterAsync(CustomGPTRequest request);
        Task<CustomGPTCharacter?> UpdateCharacterAsync(int id, CustomGPTRequest request);
        Task<bool> DeleteCharacterAsync(int id);
        Task<string> ChatWithCharacterAsync(int characterId, string message);
    }

    public class CustomGPTService : ICustomGPTService
    {
        private readonly string _charactersFilePath;
        private readonly object _lockObject = new object();
        private List<CustomGPTCharacter> _characters;
        private int _nextId = 1;
        private readonly ILogger<CustomGPTService>? _logger;

        public CustomGPTService(ILogger<CustomGPTService>? logger = null)
        {
            // Use /app/data directory for persistent data (required for container deployments)
            var dataDir = "/app/data";
            if (!Directory.Exists(dataDir))
            {
                // Fallback to current directory for local development
                dataDir = Directory.GetCurrentDirectory();
            }
            _charactersFilePath = Path.Combine(dataDir, "custom_gpt_characters.json");
            _logger = logger;
            _characters = LoadCharacters();
        }

        public async Task<List<CustomGPTCharacter>> GetAllCharactersAsync()
        {
            return await Task.Run(() =>
            {
                lock (_lockObject)
                {
                    return _characters.Where(c => c.IsActive).ToList();
                }
            });
        }

        public async Task<CustomGPTCharacter?> GetCharacterByIdAsync(int id)
        {
            return await Task.Run(() =>
            {
                lock (_lockObject)
                {
                    return _characters.FirstOrDefault(c => c.Id == id && c.IsActive);
                }
            });
        }

        public async Task<CustomGPTCharacter> CreateCharacterAsync(CustomGPTRequest request)
        {
            return await Task.Run(() =>
            {
                var character = new CustomGPTCharacter
                {
                    Id = _nextId++,
                    Name = request.Name,
                    Description = request.Description,
                    Role = request.Role,
                    GPTEndpoint = request.GPTEndpoint,
                    ApiKey = request.ApiKey,
                    CreatedAt = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow,
                    IsActive = true
                };

                lock (_lockObject)
                {
                    _characters.Add(character);
                    SaveCharacters();
                }

                return character;
            });
        }

        public async Task<CustomGPTCharacter?> UpdateCharacterAsync(int id, CustomGPTRequest request)
        {
            return await Task.Run(() =>
            {
                lock (_lockObject)
                {
                    var character = _characters.FirstOrDefault(c => c.Id == id);
                    if (character == null) return null;

                    character.Name = request.Name;
                    character.Description = request.Description;
                    character.Role = request.Role;
                    character.GPTEndpoint = request.GPTEndpoint;
                    character.ApiKey = request.ApiKey;
                    character.LastUpdated = DateTime.UtcNow;

                    SaveCharacters();

                    return character;
                }
            });
        }

        public async Task<bool> DeleteCharacterAsync(int id)
        {
            return await Task.Run(() =>
            {
                lock (_lockObject)
                {
                    var character = _characters.FirstOrDefault(c => c.Id == id);
                    if (character == null) return false;

                    character.IsActive = false;
                    character.LastUpdated = DateTime.UtcNow;

                    SaveCharacters();

                    return true;
                }
            });
        }

        public async Task<string> ChatWithCharacterAsync(int characterId, string message)
        {
            var character = await GetCharacterByIdAsync(characterId);
            if (character == null)
            {
                throw new ArgumentException("Character not found");
            }

            // For now, we'll use the existing Ollama service
            // Later, this can be extended to call actual Custom GPT endpoints
            // This is a placeholder for the integration point
            
            // TODO: Implement actual Custom GPT API calls
            // For now, return a placeholder response
            return await Task.FromResult($"This is a placeholder response from {character.Name}. Custom GPT integration coming soon!");
        }

        private List<CustomGPTCharacter> LoadCharacters()
        {
            if (!File.Exists(_charactersFilePath))
            {
                return CreateDefaultCharacters();
            }

            try
            {
                var json = File.ReadAllText(_charactersFilePath);
                var characters = JsonSerializer.Deserialize<List<CustomGPTCharacter>>(json);
                if (characters != null)
                {
                    _nextId = characters.Any() ? characters.Max(c => c.Id) + 1 : 1;
                    return characters;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error loading custom GPT characters");
            }

            return CreateDefaultCharacters();
        }

        private void SaveCharacters()
        {
            try
            {
                var json = JsonSerializer.Serialize(_characters, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_charactersFilePath, json);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error saving custom GPT characters");
            }
        }

        private List<CustomGPTCharacter> CreateDefaultCharacters()
        {
            var defaultCharacters = new List<CustomGPTCharacter>
            {
                new CustomGPTCharacter
                {
                    Id = 1,
                    Name = "Dr. Sarah Chen",
                    Description = "Experienced emergency medicine physician with a calm, professional demeanor",
                    Role = "Doctor",
                    GPTEndpoint = "https://api.openai.com/v1/chat/completions", // Placeholder
                    ApiKey = "", // Will be configured later
                    CreatedAt = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow,
                    IsActive = true
                },
                new CustomGPTCharacter
                {
                    Id = 2,
                    Name = "Patient: Michael Rodriguez",
                    Description = "Anxious patient with chest pain, needs reassurance and clear explanations",
                    Role = "Patient",
                    GPTEndpoint = "https://api.openai.com/v1/chat/completions", // Placeholder
                    ApiKey = "", // Will be configured later
                    CreatedAt = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow,
                    IsActive = true
                },
                new CustomGPTCharacter
                {
                    Id = 3,
                    Name = "Nurse: Emma Johnson",
                    Description = "Compassionate RN who advocates for patients and explains procedures clearly",
                    Role = "Nurse",
                    GPTEndpoint = "https://api.openai.com/v1/chat/completions", // Placeholder
                    ApiKey = "", // Will be configured later
                    CreatedAt = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow,
                    IsActive = true
                }
            };

            _nextId = 4;
            return defaultCharacters;
        }
    }
}
