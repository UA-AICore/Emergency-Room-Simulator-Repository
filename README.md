# Emergency Room Simulator Web App

GitHub repo:  
**https://github.com/UA-AICore/Emergency-Room-Simulator-Repository**

This project is an **AI-powered training assistant for an emergency room setting**.

It lets a learner:

- Chat with a virtual ER assistant that uses a medical knowledge base (RAG).
- See and talk to a **HeyGen avatar** that answers their questions with voice and video.
- Generate **multiple-choice quizzes** about ER topics.

The app is built with **.NET 8 (Razor Pages)** and connects to several external AI services.

---

## 1. Big Picture: How the System Works

At a high level, the system has four main “pipelines”:

1. **Chat + RAG (Retrieval-Augmented Generation)**  
   - The user types a question in the web UI.
   - The backend sends this question to a **RAG service** that searches a medical knowledge base.
   - The RAG service returns relevant document chunks plus an answer.
   - A **“personality layer”** (using OpenAI) rewrites or shapes this answer to sound like a specific ER persona.
   - The final answer is returned to the browser and also logged.

2. **Avatar Streaming (real-time talking avatar)**  
   - The user either:
     - Sends audio (speech) → Whisper transcribes it to text, or  
     - Sends text directly.
   - The text goes through the **same RAG pipeline** to generate a response.
   - The response is then sent to **HeyGen’s streaming avatar API**, which creates real-time video/voice.
   - The app manages the avatar session (start, send audio/text, stop).

3. **Quiz Generation**  
   - The user chooses or types a **topic** (e.g., “chest pain workup in the ER”).
   - The backend asks the RAG service to create **multiple-choice questions**.
   - The questions are returned as JSON with:
     - Question text  
     - Answer choices  
     - Correct answer  
   - The UI can then display these questions as a quiz.

---

## 2. Repository Layout (What Each Folder Does)

Top-level folders:

- **`ERSimulatorApp/`**  
  Main .NET 8 web application:
  - Razor Pages (UI)
  - REST APIs (for chat, avatar, quizzes, custom GPTs, health checks)
  - Dependency injection setup (services wired up)
  - Dockerfile and deployment scripts  

- **`rag-local/`**  
  Optional **Python + FastAPI** service that simulates a RAG endpoint using **Ollama** (local LLM).  
  Use this if you want to run everything locally instead of calling a remote RAG service.

- **`heygen-proxy/`**  
  Lightweight proxy service for **HeyGen async video** APIs.  
  Helps avoid exposing API keys directly from the web app and centralizes video creation/status logic.

- **`SAMPLE_QUESTIONS.md`**  
  Example prompts/questions you can use to test the chat and RAG flow.

- **Helper scripts in `ERSimulatorApp/`:**
  - `quick-deploy.sh` – build and deploy the Docker image to a remote server.
  - `set-env-vars.sh` – export required environment variables quickly.
  - `update-api-keys.sh` – helper to refresh API keys.
  - `check-rag-status.sh` – simple script to check if the RAG service is responding.

---

## 3. Key Backend Components and How They Talk to Each Other

Below is a simple explanation of the main C# components mentioned in this project.

### 3.1 Chat + RAG

**Main classes:**

- **`ChatController`**  
  - API that receives chat messages from the UI (`POST /api/chat/send`).
  - Passes the user’s question to the LLM service.
  - Returns the final answer plus source info (if available).

- **`ILLMService`** (interface)  
  - Defines the contract for LLM-related services.
  - Allows swapping different LLM/RAG implementations if needed.

- **`RAGWithPersonalityService`**  
  - Implementation of `ILLMService` that:
    1. Calls **`RAGService`** to query the knowledge base.
    2. Calls **`CharacterGatewayService`** to apply a persona (tone, style, role).
  - Combines the RAG answer and persona, then returns a final response.

- **`RAGService`**  
  - Talks to a **remote RAG endpoint** (OpenAI-compatible).
  - Sends the user’s question, gets back relevant documents and an answer.
  - Handles parameters like model name, `topK`, timeouts, etc.

- **`CharacterGatewayService`**  
  - Talks to OpenAI (or compatible API) to apply a **personality layer**.
  - Example personas: calm ER attending, triage nurse, etc.
  - Adjusts wording, tone, and sometimes structure of the answer.

**Flow for a chat request:**

1. User sends question → `ChatController`.
2. `ChatController` calls `RAGWithPersonalityService`.
3. `RAGWithPersonalityService`:
   - Calls `RAGService` for a knowledge-grounded answer.
   - Calls `CharacterGatewayService` for persona styling.
4. Final answer is logged in `ChatLogService` and returned to the UI.

---

### 3.2 Avatar Streaming (HeyGen + Whisper)

**Main classes:**

- **`AvatarStreamingController`**  
  - Handles endpoints like:
    - `POST /api/avatar/v2/streaming/session/create`
    - `POST /api/avatar/v2/streaming/audio`
    - `POST /api/avatar/v2/streaming/task`
    - `POST /api/avatar/v2/streaming/session/stop`
  - Manages **session lifecycle**:
    - Create session
    - Send user input (audio or text)
    - Receive streaming response from HeyGen
    - Stop session

- **`HeyGenStreamingService`**  
  - Low-level service that talks directly to the HeyGen streaming API.
  - Handles:
    - Authentication (API key)
    - Streaming calls
    - Reusing tokens and managing timeouts.

- **Whisper ASR (speech-to-text)**  
  - When audio is sent to `.../streaming/audio`:
    - Whisper is used for automatic speech recognition.
    - The recognized text is then piped into the **same RAG pipeline** used for normal chat.

**Flow for streaming session:**

1. Frontend creates a session → `AvatarStreamingController` → `HeyGenStreamingService`.
2. User speaks:
   - Audio is sent to `AvatarStreamingController`.
   - Whisper → text → RAG pipeline → text answer.
3. Text answer is sent to HeyGen, which generates **live avatar video/voice**.
4. When done, frontend tells the API to stop the session.

---

### 3.3 Quiz Generation

**Main class:**

- **`QuizController`**
  - Endpoint: `POST /api/quiz/generate`.
  - Receives a **topic** or prompt (e.g., “acute stroke in ER”).
  - Uses the **RAG endpoint** to generate a set of **multiple-choice questions (MCQs)**.
  - Returns JSON:
    - Question
    - Options
    - Correct answer
    - (Optional) Explanation

This lets the UI or another client build a quiz interface around the generated questions.

---

### 3.4 Custom GPT Characters

**Main classes:**

- **`CustomGPTController`**
  - Endpoints:
    - `GET /api/customgpt` – list characters
    - `POST /api/customgpt` – create character
    - `PUT /api/customgpt/{id}` – update character
    - `DELETE /api/customgpt/{id}` – delete character
    - `POST /api/customgpt/{id}/chat` – chat with a specific character

- **`CustomGPTService`**
  - Handles storage and retrieval of character definitions.
  - Defines how these characters behave (system prompts, tone, etc.).
  - Integrates with the OpenAI/personality layer.

---

### 3.5 Health Checks and Logging

**Main classes:**

- **`HealthController`**
  - `GET /api/health` – basic check to confirm the app is running.
  - `GET /api/health/detailed` – checks:
    - RAG service
    - Ollama (if configured)
    - Possibly other upstream services.

- **`ChatLogService`**
  - Writes recent chat transcripts to a log file:
    - Default location: `/app/data/chat_logs.txt` (or the working directory).
  - API: `GET /api/chat/logs` to retrieve recent logs.
  - The UI also has a **Logs** Razor Page to view them.

---

## 4. Configuration & Environment Variables

Most settings are controlled by **environment variables**, which mirror the sections in `ERSimulatorApp/appsettings.json`.

### 4.1 OpenAI / Personality Layer

- `OpenAI__ApiKey` – API key for OpenAI (or compatible endpoint).
- `OpenAI__Model` – model name (e.g., `gpt-4o` or similar).
- `OpenAI__TimeoutSeconds` – timeout for OpenAI requests.

### 4.2 RAG Service

- `RAG__BaseUrl` – URL of the RAG API (remote or `rag-local`).
- `RAG__ApiKey` – API key for the RAG service.
- `RAG__Model` – model name for the RAG backend.
- `RAG__TopK` – how many documents to retrieve.
- `RAG__TimeoutSeconds` – timeout for RAG calls.
- `RAG__SourceDocumentsPath` – optional path for source documents served to the UI.  
  Defaults to `rag-local/sample_data` if not set.

### 4.3 HeyGen (Avatar + async video)

- `HeyGen__ApiKey` – HeyGen API key.
- `HeyGen__AvatarId` – which avatar to use.
- `HeyGen__LookId` – avatar “look” configuration.
- `HeyGen__ProxyBaseUrl` – base URL for the HeyGen video proxy.
- `HeyGen__TimeoutSeconds` – timeout for HeyGen calls.

### 4.4 Whisper (speech-to-text)

- `Whisper__TimeoutSeconds` – timeout for ASR calls.

### 4.5 Ollama (optional local LLM)

- `Ollama__Endpoint` – URL to the local Ollama server.
- `Ollama__Model` – which Ollama model to use.

### 4.6 Personality Flags

- `Personality__Enabled` – `true`/`false` to toggle the persona layer.
- `Personality__Type` – what type of persona to use (e.g., `"er_attending"`).

### 4.7 ASP.NET Hosting

- `ASPNETCORE_URLS` – e.g., `http://+:8080`.
- `ASPNETCORE_PATHBASE` – optional path prefix when hosting behind a reverse proxy.

There is also an `ENV_VARS_QUICK_REFERENCE.txt` file plus a `set-env-vars.sh` script in `ERSimulatorApp/` that help you quickly set all these variables.

---

## 5. Running the App Locally

### 5.1 Prerequisites

- .NET 8 SDK  
- Access to:
  - RAG service (and its API key)
  - OpenAI (for personality/custom GPTs)
  - HeyGen (for avatars and videos)
- Optional:
  - Python 3.10+ and **Ollama** if you want to run `rag-local`.
- Docker (if you want to build/run containers).

### 5.2 Start the Web App

```bash
cd "ER Simulator Web App/ERSimulatorApp"

# 1. Set required environment variables
#    (OpenAI, RAG, HeyGen, etc.)

dotnet restore
ASPNETCORE_URLS=http://localhost:8080 dotnet run
