using ERSimulatorApp.Models;
using ERSimulatorApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace ERSimulatorApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CustomGPTController : ControllerBase
    {
        private readonly ICustomGPTService _customGPTService;
        private readonly ChatLogService _logService;
        private readonly ILogger<CustomGPTController> _logger;

        public CustomGPTController(
            ICustomGPTService customGPTService, 
            ChatLogService logService, 
            ILogger<CustomGPTController> logger)
        {
            _customGPTService = customGPTService;
            _logService = logService;
            _logger = logger;
        }

        // GET: api/customgpt
        [HttpGet]
        public async Task<IActionResult> GetAllCharacters()
        {
            try
            {
                var characters = await _customGPTService.GetAllCharactersAsync();
                return Ok(characters);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving custom GPT characters");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        // GET: api/customgpt/{id}
        [HttpGet("{id}")]
        public async Task<IActionResult> GetCharacter(int id)
        {
            try
            {
                var character = await _customGPTService.GetCharacterByIdAsync(id);
                if (character == null)
                {
                    return NotFound(new { error = "Character not found" });
                }

                return Ok(character);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving character {CharacterId}", id);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        // POST: api/customgpt
        [HttpPost]
        public async Task<IActionResult> CreateCharacter([FromBody] CustomGPTRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return BadRequest(new { error = "Character name is required" });
                }

                if (string.IsNullOrWhiteSpace(request.GPTEndpoint))
                {
                    return BadRequest(new { error = "GPT endpoint is required" });
                }

                var character = await _customGPTService.CreateCharacterAsync(request);
                
                _logger.LogInformation("Created custom GPT character: {CharacterName} (ID: {CharacterId})", character.Name, character.Id);
                return CreatedAtAction(nameof(GetCharacter), new { id = character.Id }, character);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating custom GPT character");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        // PUT: api/customgpt/{id}
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateCharacter(int id, [FromBody] CustomGPTRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return BadRequest(new { error = "Character name is required" });
                }

                if (string.IsNullOrWhiteSpace(request.GPTEndpoint))
                {
                    return BadRequest(new { error = "GPT endpoint is required" });
                }

                var character = await _customGPTService.UpdateCharacterAsync(id, request);
                if (character == null)
                {
                    return NotFound(new { error = "Character not found" });
                }

                _logger.LogInformation("Updated custom GPT character: {CharacterName} (ID: {CharacterId})", character.Name, character.Id);
                return Ok(character);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating custom GPT character {CharacterId}", id);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        // DELETE: api/customgpt/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteCharacter(int id)
        {
            try
            {
                var success = await _customGPTService.DeleteCharacterAsync(id);
                if (!success)
                {
                    return NotFound(new { error = "Character not found" });
                }

                _logger.LogInformation("Deleted custom GPT character ID: {CharacterId}", id);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting custom GPT character {CharacterId}", id);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        // POST: api/customgpt/{id}/chat
        [HttpPost("{id}/chat")]
        public async Task<IActionResult> ChatWithCharacter(int id, [FromBody] CustomGPTChatRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Message))
                {
                    return BadRequest(new { error = "Message cannot be empty" });
                }

                var character = await _customGPTService.GetCharacterByIdAsync(id);
                if (character == null)
                {
                    return NotFound(new { error = "Character not found" });
                }

                var startTime = DateTime.UtcNow;
                _logger.LogInformation("Custom GPT chat: {CharacterName} (ID: {CharacterId})", character.Name, character.Id);

                // Get response from Custom GPT
                var aiResponse = await _customGPTService.ChatWithCharacterAsync(id, request.Message);
                
                var endTime = DateTime.UtcNow;
                var responseTime = endTime - startTime;

                // Log the conversation
                var logEntry = new ChatLogEntry
                {
                    Timestamp = startTime,
                    SessionId = request.SessionId,
                    UserMessage = $"[{character.Name}] {request.Message}",
                    AIResponse = aiResponse,
                    ResponseTime = responseTime
                };

                _logService.LogChat(logEntry);

                var response = new CustomGPTChatResponse
                {
                    CharacterId = character.Id,
                    CharacterName = character.Name,
                    Response = aiResponse,
                    SessionId = request.SessionId,
                    Timestamp = endTime
                };

                _logger.LogInformation("Custom GPT response generated in {ResponseTime}ms", responseTime.TotalMilliseconds);
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in custom GPT chat for character {CharacterId}", id);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }
    }
}
