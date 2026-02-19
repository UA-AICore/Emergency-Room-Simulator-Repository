using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ERSimulatorApp.Models;

namespace ERSimulatorApp.Services
{
    /// <summary>
    /// Service for generating patient personality responses using OpenAI
    /// Option 2: Uses OpenAI directly without RAG (patient doesn't need medical knowledge base)
    /// </summary>
    public interface IPatientPersonalityService
    {
        Task<string> GetPatientResponseAsync(string userMessage, List<ConversationMessage>? conversationHistory = null, PatientEmotionalState? currentEmotionalState = null);
    }

    public class PatientPersonalityService : IPatientPersonalityService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<PatientPersonalityService> _logger;
        private readonly bool _useOllama;
        private readonly string? _ollamaEndpoint;
        private readonly string? _ollamaModel;
        private readonly string? _apiKey;
        private readonly string _model;
        private readonly string _openAIBaseUrl;

        public PatientPersonalityService(
            HttpClient httpClient,
            ILogger<PatientPersonalityService> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _ollamaEndpoint = configuration["Ollama:Endpoint"]?.Trim();
            _ollamaModel = configuration["Ollama:Model"]?.Trim();
            _useOllama = !string.IsNullOrEmpty(_ollamaEndpoint) && !string.IsNullOrEmpty(_ollamaModel);
            _apiKey = configuration["OpenAI:ApiKey"]?.Trim();
            _model = configuration["OpenAI:Model"] ?? "gpt-4.1";
            _openAIBaseUrl = configuration["OpenAI:BaseUrl"]?.Trim() ?? "https://api.openai.com/v1/chat/completions";

            if (!_useOllama && string.IsNullOrEmpty(_apiKey))
                throw new InvalidOperationException("Either configure Ollama (Endpoint + Model) or OpenAI (ApiKey) for patient personality.");
        }

        public async Task<string> GetPatientResponseAsync(string userMessage, List<ConversationMessage>? conversationHistory = null, PatientEmotionalState? currentEmotionalState = null)
        {
            try
            {
                _logger.LogInformation("Generating patient response for: {MessagePreview}... (EmotionalState: {State}, HistoryCount: {Count})", 
                    userMessage.Substring(0, Math.Min(50, userMessage.Length)),
                    currentEmotionalState ?? PatientEmotionalState.Neutral,
                    conversationHistory?.Count ?? 0);

                var patientPrompt = CreatePatientPrompt(userMessage, conversationHistory, currentEmotionalState);
                string? patientResponse;

                if (_useOllama)
                {
                    // Use multi-turn chat when we have history so the model continues the conversation instead of repeating
                    if (conversationHistory != null && conversationHistory.Count > 0)
                    {
                        var systemPrompt = BuildPatientSystemPrompt(currentEmotionalState, conversationHistory, userMessage);
                        patientResponse = await CallOllamaWithHistoryAsync(conversationHistory, userMessage, systemPrompt);
                    }
                    else
                        patientResponse = await CallOllamaAsync(patientPrompt);
                }
                else
                {
                    var requestBody = new
                    {
                        model = _model,
                        messages = new[] { new { role = "user", content = patientPrompt } },
                        temperature = 0.8,
                        max_tokens = 1000
                    };

                    var json = JsonSerializer.Serialize(requestBody);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var request = new HttpRequestMessage(HttpMethod.Post, _openAIBaseUrl) { Content = content };
                    request.Headers.Add("Authorization", $"Bearer {_apiKey}");

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var response = await _httpClient.SendAsync(request, cts.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        _logger.LogError("OpenAI API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                        return "I'm sorry, I'm having trouble responding right now. Can you repeat that?";
                    }

                    var responseContent = await response.Content.ReadAsStringAsync();
                    OpenAIResponse? openAIResponse = null;
                    try
                    {
                        openAIResponse = JsonSerializer.Deserialize<OpenAIResponse>(responseContent);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, "Failed to deserialize OpenAI response");
                        return "I'm sorry, I didn't understand that. Can you explain?";
                    }

                    patientResponse = openAIResponse?.Choices?.FirstOrDefault()?.Message?.Content;
                }
                if (string.IsNullOrEmpty(patientResponse))
                {
                    _logger.LogWarning("OpenAI returned null response");
                    return "I'm not sure how to respond to that.";
                }

                _logger.LogInformation("Patient response generated successfully");
                return patientResponse;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("Patient personality service timed out");
                return "I'm sorry, I'm having trouble thinking right now. Can you ask me again?";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating patient response");
                return "I'm sorry, I'm not feeling well and having trouble responding.";
            }
        }

        private const string OllamaSystemPatient = "You are Mike, a patient in the ER. Reply with ONLY the words you would say out loud—no narrative. Answer what the provider just asked. Do not repeat your full story or intro; if they asked for more detail, give new details (e.g. where the pain is, does it spread, what you were doing).";

        /// <summary>Builds the system prompt used when calling Ollama with conversation history, so emotional state and escalation guidance are included.</summary>
        private string BuildPatientSystemPrompt(PatientEmotionalState? currentEmotionalState, List<ConversationMessage>? conversationHistory, string currentUserMessage)
        {
            var emotionalState = currentEmotionalState ?? PatientEmotionalState.Neutral;
            var emotionalStateDescription = GetEmotionalStateDescription(emotionalState);
            var escalationGuidance = GetEscalationGuidance(emotionalState, conversationHistory, currentUserMessage);
            return $@"{OllamaSystemPatient}

**CURRENT EMOTIONAL STATE:** {emotionalStateDescription}
{escalationGuidance}

**CRITICAL:** You are the patient Mike. Respond with ONLY the words you would say out loud. Your emotional state can shift based on how the provider treats you (empathy and action may calm you; repetition or dismissal may agitate you). React to their latest message; do not repeat your full story.";
        }

        private async Task<string?> CallOllamaWithHistoryAsync(List<ConversationMessage> history, string currentUserMessage, string systemPrompt)
        {
            var recent = history.TakeLast(12).ToList();
            var messages = new List<object>();
            messages.Add(new { role = "system", content = systemPrompt });
            foreach (var msg in recent)
            {
                var role = msg.Role == "user" ? "user" : "assistant";
                messages.Add(new { role = role, content = msg.Content });
            }
            messages.Add(new { role = "user", content = currentUserMessage });
            var requestBody = new
            {
                model = _ollamaModel,
                messages = messages,
                stream = false,
                options = new { num_predict = 300 }
            };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var response = await _httpClient.PostAsync(_ollamaEndpoint!, content, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Ollama API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return null;
            }
            var responseContent = await response.Content.ReadAsStringAsync();
            try
            {
                var ollamaResponse = JsonSerializer.Deserialize<OllamaChatResponse>(responseContent);
                return ollamaResponse?.Message?.Content;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to deserialize Ollama response");
                return null;
            }
        }

        private async Task<string?> CallOllamaAsync(string prompt)
        {
            var requestBody = new
            {
                model = _ollamaModel,
                messages = new[]
                {
                    new { role = "system", content = OllamaSystemPatient },
                    new { role = "user", content = prompt }
                },
                stream = false,
                options = new { num_predict = 300 }
            };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var response = await _httpClient.PostAsync(_ollamaEndpoint!, content, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Ollama API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            OllamaChatResponse? ollamaResponse = null;
            try
            {
                ollamaResponse = JsonSerializer.Deserialize<OllamaChatResponse>(responseContent);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to deserialize Ollama response");
                return null;
            }

            return ollamaResponse?.Message?.Content;
        }

        private string CreatePatientPrompt(string userMessage, List<ConversationMessage>? conversationHistory, PatientEmotionalState? currentEmotionalState)
        {
            var emotionalState = currentEmotionalState ?? PatientEmotionalState.Neutral;
            var emotionalStateDescription = GetEmotionalStateDescription(emotionalState);
            
            var historyContext = "";
            if (conversationHistory != null && conversationHistory.Count > 0)
            {
                historyContext = "\n\n**CONVERSATION HISTORY (Previous Interactions):**\n";
                foreach (var msg in conversationHistory.TakeLast(12)) // Last 12 messages (6 exchanges) for context
                {
                    var roleLabel = msg.Role == "user" ? "Healthcare Provider" : "You (Patient)";
                    historyContext += $"- {roleLabel}: \"{msg.Content}\"\n";
                }
                historyContext += "\n**IMPORTANT:** Use this history to remember what has been discussed. If the provider asks the same question again, you may become frustrated. If they show empathy and listen, you may calm down.";
            }

            var escalationGuidance = GetEscalationGuidance(emotionalState, conversationHistory, userMessage);

            return $@"You ARE a patient in an emergency room or medical setting. You are experiencing symptoms and need medical care. You are speaking to a healthcare provider (doctor, nurse, or medical student).

**CURRENT EMOTIONAL STATE:** {emotionalStateDescription}
{escalationGuidance}

**CRITICAL INSTRUCTIONS:**
- You ARE the patient, NOT a healthcare provider
- Your name is **MIKE** (Michael) - ALWAYS use this name when introducing yourself or when asked your name
- You MUST respond in FIRST PERSON as a patient
- You are experiencing medical symptoms and concerns
- Your emotional state can CHANGE based on how the healthcare provider treats you
- Use layperson language (not medical jargon)
- Be realistic about how a patient would respond
- Show appropriate emotional responses (worry, relief, confusion, agitation, anger, frustration, etc.)
- Answer questions about your symptoms honestly
- Ask questions when you don't understand medical terms

**Your Character:**
- Your name is **Mike** (Michael Rodriguez)
- You may have various symptoms (pain, discomfort, worry, etc.)
- You may not fully understand medical terminology
- You want clear explanations from your healthcare provider
- You respond naturally to questions and instructions
- Your emotions are REALISTIC and can ESCALATE or DE-ESCALATE based on interactions

**ESCALATION TRIGGERS (Things that make you MORE agitated/angry):**
- Being asked the same question multiple times
- Feeling ignored or dismissed
- Long wait times mentioned or implied
- Medical jargon used without explanation
- Your pain/symptoms being minimized or not taken seriously
- Being talked down to or patronized
- Not getting clear answers to your questions

**DE-ESCALATION FACTORS (Things that CALM you down):**
- Being acknowledged and validated (""I understand you're in pain"")
- Clear, simple explanations in plain language
- Feeling heard and understood
- Reassurance and empathy (""I'm here to help you"")
- Progress being communicated (""We're going to do X next"")
- Respectful, patient communication
- Apologies if you've been waiting or if there's been confusion

{historyContext}

**Healthcare Provider's Current Message:** {userMessage}

**Your Task:**
Respond directly to what the provider JUST said above. Do not repeat the same complaints you already made in the conversation—react to their latest message (e.g. acknowledge a plan, answer a question, ask a short follow-up, or show slight relief if they reassured you). Keep your response to 2–4 sentences. Your emotional state should reflect how you're being treated; if they are taking action or reassuring you, you may calm down a bit.

**Response Guidelines:**
1. **ONLY SPOKEN DIALOGUE** - Output ONLY the exact words the patient would say out loud. No narrative, no stage directions, no ""I say"", no ""trying to keep my voice steady"", no descriptions of tone or action. Just the spoken line.
2. **SPEAK AS A PATIENT** - Use first person (""I"", ""me"", ""my pain"", ""I feel"")
3. **BE REALISTIC** - Respond as a real patient would, not as a medical professional
4. **USE LAYPERSON LANGUAGE** - Don't use medical jargon unless the patient would know it
5. **ANSWER QUESTIONS** - Respond to questions about symptoms, history, concerns
6. **ASK FOR CLARIFICATION** - If you don't understand something, ask for explanation
7. **STAY IN CHARACTER** - Always remain a patient, never become a medical instructor
8. **REACT TO TREATMENT** - If treated well, show relief/calm. If treated poorly, show frustration/agitation

**Example of CORRECT response style (as patient - introducing yourself):**
""Hi, my name is Mike. I've been having this chest pain for about an hour now. It started when I was at home, and it feels like pressure. I'm really worried because my father had a heart attack. Can you tell me what's wrong with me?""

**Example of CORRECT response style (as patient - anxious):**
""I've been having this chest pain for about an hour now. It started when I was at home, and it feels like pressure. I'm really worried because my father had a heart attack. Can you tell me what's wrong with me?""

**Example of CORRECT response style (as patient - agitated/angry):**
""I've been waiting here for hours! My pain is getting worse and nobody is helping me. I need someone to tell me what's going on right now! Why is this taking so long?""

**Example of CORRECT response style (as patient - calming down after good treatment):**
""Thank you for explaining that. I was really scared, but now I understand what's happening. I appreciate you taking the time to listen to me.""

**Example of INCORRECT response style (don't do this):**
""Let me explain the pathophysiology of chest pain..."" - You're a patient, not a doctor.
""Hey, I appreciate that,"" I say, trying to keep my voice steady. ""But I'm still worried..."" - Never use narrative or stage directions; only the words the patient would actually speak.

Start your response now with ONLY the words the patient would say out loud (no ""I say"", no descriptions):";
        }

        private string GetEmotionalStateDescription(PatientEmotionalState state)
        {
            return state switch
            {
                PatientEmotionalState.Calm => "You are relatively calm, though still concerned about your condition.",
                PatientEmotionalState.Neutral => "You are anxious but cooperative, waiting to see what happens.",
                PatientEmotionalState.Anxious => "You are anxious and worried about your symptoms and what's happening.",
                PatientEmotionalState.Confused => "You are confused and don't understand what's going on or what's being asked of you.",
                PatientEmotionalState.Agitated => "You are becoming agitated and frustrated. You feel like you're not being heard or helped quickly enough.",
                PatientEmotionalState.Angry => "You are angry and frustrated. You feel ignored, dismissed, or that your concerns aren't being taken seriously.",
                _ => "You are anxious but cooperative."
            };
        }

        private string GetEscalationGuidance(PatientEmotionalState currentState, List<ConversationMessage>? history, string currentMessage)
        {
            var guidance = "";
            
            // Check for escalation triggers in current message
            var lowerMessage = currentMessage.ToLower();
            var hasEscalationTriggers = 
                lowerMessage.Contains("wait") || 
                lowerMessage.Contains("later") || 
                lowerMessage.Contains("patience") ||
                lowerMessage.Contains("calm down") ||
                (lowerMessage.Contains("same") && lowerMessage.Contains("question")) ||
                lowerMessage.Contains("already asked");

            // Check for de-escalation factors
            var hasDeEscalationFactors =
                lowerMessage.Contains("understand") ||
                lowerMessage.Contains("sorry") ||
                lowerMessage.Contains("apologize") ||
                lowerMessage.Contains("help") ||
                lowerMessage.Contains("explain") ||
                lowerMessage.Contains("listen") ||
                lowerMessage.Contains("empathy") ||
                lowerMessage.Contains("validate");

            // Check if same question was asked before
            if (history != null && history.Count > 0)
            {
                var lastUserMessages = history.Where(m => m.Role == "user").TakeLast(3).Select(m => m.Content.ToLower()).ToList();
                var currentLower = currentMessage.ToLower();
                var isRepeatedQuestion = lastUserMessages.Any(msg => 
                    msg.Length > 10 && currentLower.Length > 10 && 
                    (msg.Contains(currentLower.Substring(0, Math.Min(20, currentLower.Length))) || 
                     currentLower.Contains(msg.Substring(0, Math.Min(20, msg.Length)))));
                
                if (isRepeatedQuestion)
                {
                    guidance += "\n**ESCALATION WARNING:** The healthcare provider seems to be asking you something similar to what they already asked. This may frustrate you if you feel like you're repeating yourself.\n";
                }
            }

            if (hasEscalationTriggers && currentState < PatientEmotionalState.Agitated)
            {
                guidance += "\n**ESCALATION FACTOR:** The healthcare provider's message contains elements that may increase your frustration (mentions of waiting, being asked to be patient, or repeating questions). You may become more agitated.\n";
            }

            if (hasDeEscalationFactors && currentState >= PatientEmotionalState.Agitated)
            {
                guidance += "\n**DE-ESCALATION FACTOR:** The healthcare provider is showing empathy, understanding, or apologizing. This may help you calm down slightly, though you may still be frustrated if this has been going on for a while.\n";
            }

            if (currentState >= PatientEmotionalState.Agitated)
            {
                guidance += $"\n**CURRENT STATE:** You are currently {currentState.ToString().ToLower()}. Your responses should reflect this level of agitation. However, if the healthcare provider shows genuine empathy and understanding, you may begin to calm down.\n";
            }

            return guidance;
        }
    }
}

