# RAG backend (Chroma + Ollama / Claude)

See repo root **SETUP-LOCAL.md** for how to run.

**Secrets:** Put API keys in `rag_backend/.env.secrets`. That file is in `.gitignore` and is never committed. Example:
```bash
echo 'ANTHROPIC_API_KEY=your-key' > .env.secrets
```
