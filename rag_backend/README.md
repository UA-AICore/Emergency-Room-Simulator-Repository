# RAG backend (Chroma + Ollama / Claude)

**Secrets (Anthropic / Claude):** There is **no** `rag_backend/.env.secrets` in git—it only exists after you create it locally.

1. From `rag_backend/`, copy the tracked template:
   ```bash
   cp .env.secrets.example .env.secrets
   ```
2. Edit `.env.secrets` and set `ANTHROPIC_API_KEY=...`.

Alternatively: `export ANTHROPIC_API_KEY=...` before starting uvicorn, or set `Environment=` on **`codira-rag`** (systemd). The .NET **`ERSimulatorApp/appsettings.json`** does **not** hold the Anthropic key.

Non-secret defaults (Ollama URL, port, etc.) can go in **`rag_backend/.env`**; copy from **`.env.example`** if you like (`.env` is also gitignored).

See **`rag_backend/.env.example`** for full env variable reference.
