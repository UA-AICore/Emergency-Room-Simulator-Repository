# app/main.py — clean RAG backend using Chroma + remote LLM (Jeremy's server)
# Fully cleaned and updated to remove forced ABCDE answers

# ---------- Standard library ----------
import os
import uuid
from typing import Optional, List, Dict, Any

# ---------- Third-party ----------
from fastapi import FastAPI
from pydantic import BaseModel
from dotenv import load_dotenv

import requests
from requests.adapters import HTTPAdapter, Retry

from chromadb import PersistentClient
import chromadb.utils.embedding_functions as ef
from pypdf import PdfReader

# ---------- Load environment ----------
# Non-secret config (OLLAMA_URL, etc.) — can live in .env
load_dotenv()
# Untracked secrets: rag_backend/.env.secrets (in .gitignore). Create it with ANTHROPIC_API_KEY=...
_rag_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_env_secrets = os.path.join(_rag_root, ".env.secrets")
if os.path.isfile(_env_secrets):
    load_dotenv(_env_secrets)
# Override: RAG_SECRETS_ENV can point to another path if you prefer
_secrets_env = os.getenv("RAG_SECRETS_ENV", "").strip()
if _secrets_env and os.path.isfile(_secrets_env):
    load_dotenv(_secrets_env)

# ---------- Config flags ----------
USE_REMOTE_LLM = os.getenv("USE_REMOTE_LLM", "0") == "1"

# Remote LLM configuration (Jeremy's OpenAI-compatible server)
OPENAI_BASE_URL = os.getenv("OPENAI_BASE_URL", "").rstrip("/")
OPENAI_API_KEY = os.getenv("OPENAI_API_KEY", "")
OPENAI_MODEL = os.getenv("OPENAI_MODEL", "meta-llama/Llama-3.2-1B-instruct")

# Local Ollama (only when USE_REMOTE_LLM=0 and model is not claude)
OLLAMA_URL = os.getenv("OLLAMA_URL", "http://127.0.0.1:11434")
OLLAMA_MODEL = os.getenv("OLLAMA_MODEL", "gemma2")
OLLAMA_TIMEOUT = int(os.getenv("OLLAMA_TIMEOUT", "180"))  # seconds; gemma2 can be slow

# Claude (Anthropic) — key from .env.secrets (untracked) or env
# Model ID for Claude Opus 4.6: claude-opus-4-6 (see https://docs.anthropic.com/en/docs/about-claude/models)
ANTHROPIC_API_KEY = os.getenv("ANTHROPIC_API_KEY", "").strip()
ANTHROPIC_MODEL = os.getenv("ANTHROPIC_MODEL", "claude-opus-4-6").strip() or "claude-opus-4-6"
ANTHROPIC_TIMEOUT = int(os.getenv("ANTHROPIC_TIMEOUT", "120"))
ANTHROPIC_VERSION = "2023-06-01"

# ---------- ChromaDB setup ----------
DB_PATH = "vector_store/chroma"
COLLECTION_NAME = "medical_docs"

client = PersistentClient(path=DB_PATH)
embed_fn = ef.SentenceTransformerEmbeddingFunction(
    model_name="sentence-transformers/all-MiniLM-L6-v2"
)

try:
    collection = client.get_collection(COLLECTION_NAME)
except Exception:
    collection = client.create_collection(
        COLLECTION_NAME,
        embedding_function=embed_fn
    )

# ---------- LLM helper functions ----------

def call_remote_chat(
    messages: List[Dict[str, str]],
    temperature: float = 0.2,
    max_tokens: int = 512,
    timeout: int = 60,
) -> str:
    """
    Call Jeremy's OpenAI-compatible LLM server.
    """
    if not OPENAI_BASE_URL or not OPENAI_API_KEY:
        raise RuntimeError("Remote LLM enabled but OPENAI_BASE_URL or OPENAI_API_KEY missing.")

    base = OPENAI_BASE_URL
    if not base.endswith("/v1"):
        base = base.rstrip("/") + "/v1"

    url = f"{base}/chat/completions"
    headers = {
        "Authorization": f"Bearer {OPENAI_API_KEY}",
        "Content-Type": "application/json",
    }
    payload: Dict[str, Any] = {
        "model": OPENAI_MODEL,
        "messages": messages,
        "temperature": temperature,
        "max_tokens": max_tokens,
    }

    s = requests.Session()
    s.mount("https://", HTTPAdapter(max_retries=Retry(total=3, backoff_factor=0.5)))
    s.mount("http://", HTTPAdapter(max_retries=Retry(total=3, backoff_factor=0.5)))

    r = s.post(url, headers=headers, json=payload, timeout=timeout)
    if r.status_code >= 400:
        raise RuntimeError(f"LLM error {r.status_code}: {r.text}")

    data = r.json()
    return data["choices"][0]["message"]["content"]


def call_local_ollama(
    messages: List[Dict[str, str]],
    temperature: float = 0.1,
    max_tokens: int = 400,
    timeout: Optional[int] = None,
) -> str:
    """Call local Ollama API."""
    if timeout is None:
        timeout = OLLAMA_TIMEOUT
    # --- ORIGINAL (commented out so we can revert if needed) ---
    # def call_local_ollama(messages: List[Dict[str, str]]):
    #     raise RuntimeError("Local Ollama disabled (USE_REMOTE_LLM should be 1).")
    # --- END ORIGINAL ---
    url = f"{OLLAMA_URL.rstrip('/')}/api/chat"
    payload: Dict[str, Any] = {
        "model": OLLAMA_MODEL,
        "messages": messages,
        "stream": False,
        "options": {"temperature": temperature, "num_predict": max_tokens},
    }
    r = requests.post(url, json=payload, timeout=timeout or OLLAMA_TIMEOUT)
    if r.status_code >= 400:
        raise RuntimeError(f"Ollama error {r.status_code}: {r.text}")
    data = r.json()
    return data.get("message", {}).get("content", "")


def call_claude(
    system: str,
    user_content: str,
    max_tokens: int = 1024,
    timeout: Optional[int] = None,
) -> str:
    """Call Anthropic Messages API (e.g. Claude 4.6 Opus). Uses system + single user message."""
    if not ANTHROPIC_API_KEY:
        raise RuntimeError("Claude requested but ANTHROPIC_API_KEY is not set.")
    timeout = timeout or ANTHROPIC_TIMEOUT
    url = "https://api.anthropic.com/v1/messages"
    headers = {
        "x-api-key": ANTHROPIC_API_KEY,
        "anthropic-version": ANTHROPIC_VERSION,
        "Content-Type": "application/json",
    }
    payload: Dict[str, Any] = {
        "model": ANTHROPIC_MODEL,
        "max_tokens": max_tokens,
        "system": system,
        "messages": [{"role": "user", "content": user_content}],
    }
    s = requests.Session()
    s.mount("https://", HTTPAdapter(max_retries=Retry(total=2, backoff_factor=0.5)))
    r = s.post(url, headers=headers, json=payload, timeout=timeout)
    if r.status_code >= 400:
        raise RuntimeError(f"Claude API error {r.status_code}: {r.text}")
    data = r.json()
    # Messages API returns content as array of blocks (e.g. [{"type": "text", "text": "..."}])
    content = data.get("content") or []
    if not content:
        return ""
    block = content[0] if isinstance(content[0], dict) else {}
    return block.get("text", "")


# ---------- PDF helpers ----------

def read_pdf_text(path: str) -> str:
    reader = PdfReader(path)
    pages = []
    for p in reader.pages:
        try:
            pages.append(p.extract_text() or "")
        except Exception:
            continue
    return "\n".join(pages)


def chunk_text(text: str, chunk_size: int = 900, overlap: int = 150) -> List[str]:
    words = text.split()
    out = []
    i = 0
    while i < len(words):
        out.append(" ".join(words[i:i + chunk_size]))
        i += (chunk_size - overlap)
    return out


# ---------- FastAPI setup ----------
app = FastAPI()


class IngestReq(BaseModel):
    folder: Optional[str] = "data/pdfs"


class AskReq(BaseModel):
    question: str
    top_k: int = 4


# OpenAI chat-completions request (for .NET ER Simulator)
class ChatMessage(BaseModel):
    role: str
    content: str


def extract_question_for_rag(full_prompt: str) -> str:
    """
    Use only the student's current question for RAG retrieval when the .NET app
    sends the full conversation. Otherwise retrieval matches conversation text
    (e.g. 'trauma', 'ER', 'simulation') and returns wrong PDF chunks.
    """
    marker = "Student's current question:"
    if marker in full_prompt:
        rest = full_prompt.split(marker, 1)[1].strip()
        # First line after the marker (up to next double newline or instruction block)
        first_block = rest.split("\n\n")[0].strip()
        if first_block:
            return first_block
    return full_prompt.strip()


def _language_hint(question: str) -> str:
    """If the question looks like Spanish, return an instruction so the model responds in Spanish."""
    q = (question or "").strip().lower()
    if not q:
        return ""
    # Spanish indicators: inverted ¿, ñ, accented vowels, or common Spanish words
    if "¿" in question or "ñ" in q or any(c in q for c in "áéíóúü"):
        return "The student's question is in Spanish. You MUST respond entirely in Spanish.\n\n"
    spanish_starters = ("qué ", "como ", "cómo ", "cuál ", "cuáles ", "por qué ", "cuando ", "cuándo ", "donde ", "dónde ", "cuanto ", "cuánto ", "quien ", "quién ", "debo ", "puedo ", "debería ", "hay ", "tiene ", "tienen ", "es ", "son ", "en caso ", "qué debo ", "cómo puedo ")
    if any(q.startswith(s) for s in spanish_starters):
        return "The student's question is in Spanish. You MUST respond entirely in Spanish.\n\n"
    return ""


class OpenAIChatRequest(BaseModel):
    model: Optional[str] = None
    messages: List[ChatMessage]
    temperature: Optional[float] = 0.7
    max_tokens: Optional[int] = 2000


def _use_claude(model: Optional[str]) -> bool:
    """True if the requested model is Claude (user chose Claude mode)."""
    if not model:
        return False
    return "claude" in model.lower()


def _is_not_found_in_context(answer: str) -> bool:
    """True if the RAG answer indicates the topic was not in the indexed materials."""
    if not answer or len(answer.strip()) < 10:
        return False
    a = answer.strip().lower()
    if "not found in context" in a:
        return True
    if "don't cover" in a or "doesn't cover" in a or "do not cover" in a:
        return True
    if "materials i have" in a and ("don't cover" in a or "focus on" in a and "don't" in a):
        return True
    if "no relevant context" in a or "no relevant information" in a:
        return True
    return False


def _rag_answer(question: str, top_k: int = 4, model: Optional[str] = None) -> tuple[str, list[str]]:
    """Run RAG: query Chroma, build context, call LLM. Returns (answer, context_preview).
    model: optional request model; if it contains 'claude', use Claude API; else Ollama or remote."""
    top_k = max(1, min(10, top_k))
    results = collection.query(query_texts=[question], n_results=top_k)

    docs = (results.get("documents") or [[]])[0]
    metas = (results.get("metadatas") or [[]])[0]

    if not docs:
        return "No relevant context found in indexed PDFs.", []

    previews = []
    for d, m in zip(docs, metas):
        src = m.get("source", "unknown")
        chunk_id = m.get("chunk")
        label = f"({src}, chunk {chunk_id})"
        previews.append(f"- {label} {d[:700]}")

    context_text = "\n".join(previews)
    system_prompt = (
        "You are an ER doctor teaching a student. Use ONLY the context below to answer. You are speaking TO the student—give them the answer directly, as if you are explaining it yourself.\n"
        "CRITICAL: You MUST respond in the EXACT same language as the student's question. If the question is in Spanish, your entire answer must be in Spanish. If in English, answer in English. Do not default to English when the question is in another language.\n"
        "FORBIDDEN: Do not refer to the context as text or a document. Never say: 'this text focuses on', 'the document says', 'it talks about', 'the context mentions'. Never describe what the source says; instead, teach that information as your own.\n"
        "GOOD: 'With blunt abdominal trauma, patients often have tenderness, guarding, and rigidity—so we examine them carefully.'\n"
        "BAD: 'This text focuses on trauma and talks about abdominal tenderness.'\n"
        "If the answer is not in the context, say: \"Not found in context.\" Answer in 2–4 short sentences. Use \"you\" and a warm tone. No bullet points unless they ask for a list. Do NOT repeat the ABCDE list unless they ask for ABCDE.\n"
    )
    lang_hint = _language_hint(question)
    user_content = f"{lang_hint}Question: {question}\n\nContext:\n{context_text}"

    try:
        if _use_claude(model):
            answer = call_claude(system=system_prompt, user_content=user_content, max_tokens=1024)
        elif USE_REMOTE_LLM:
            messages = [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_content},
            ]
            answer = call_remote_chat(messages, temperature=0.1, max_tokens=400)
        else:
            messages = [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_content},
            ]
            answer = call_local_ollama(messages)
    except Exception as e:
        import logging
        logging.warning("RAG LLM call failed: %s", e)
        # Still return previews so the UI can show "references" even when the LLM step failed
        return (
            "I couldn't look that up in my references right now. Please try again in a moment.",
            previews,
        )

    # Claude mode: if the answer says the topic isn't in the RAG materials, fall back to general Claude for a concise response
    if _use_claude(model) and _is_not_found_in_context(answer):
        try:
            fallback_system = (
                "You are an ER physician teaching a student. The student asked a medical question that is not in your trauma-focused reference materials. "
                "Give a concise, helpful answer from general medical knowledge in 2–4 short sentences. Use a warm, teaching tone. "
                "CRITICAL: You MUST respond in the EXACT same language as the student's question. If they asked in Spanish, answer entirely in Spanish. If in English, in English. Do not default to English.\n"
                "Do not say the topic was not in your materials—just answer the question directly."
            )
            fallback_user = f"{_language_hint(question)}Question: {question}"
            answer = call_claude(system=fallback_system, user_content=fallback_user, max_tokens=512)
            # Return empty previews so the UI doesn't show RAG references for this general answer
            return answer, []
        except Exception as e:
            import logging
            logging.warning("Claude fallback (general knowledge) failed: %s", e)
            # Keep the original "not found" answer
            return answer, previews

    return answer, previews


# ---------- Routes ----------

@app.get("/health")
def health():
    mode = "remote" if USE_REMOTE_LLM else "ollama"
    model = OPENAI_MODEL if USE_REMOTE_LLM else OLLAMA_MODEL
    try:
        count = collection.count()
    except Exception:
        count = 0
    return {
        "status": "ok",
        "llm_mode": mode,
        "model": model,
        "docs_indexed": count,
    }


@app.post("/ingest")
def ingest(req: IngestReq):
    folder = (req.folder or "").strip().strip("\"'")

    if not os.path.isdir(folder):
        return {"error": f"Folder not found: {folder}"}

    added = 0
    for name in sorted(os.listdir(folder)):
        if not name.lower().endswith(".pdf"):
            continue

        path = os.path.join(folder, name)
        try:
            text = read_pdf_text(path)
            if not text.strip():
                continue

            chunks = chunk_text(text)
            ids = [str(uuid.uuid4()) for _ in chunks]
            metas = [{"source": name, "chunk": i} for i in range(len(chunks))]

            collection.add(ids=ids, documents=chunks, metadatas=metas)
            added += len(chunks)

        except Exception:
            continue

    return {"added_chunks": added, "total_chunks": collection.count()}


@app.post("/api/ask")
def ask(req: AskReq):
    """Trauma-safe RAG answer engine (NO forced ABCDE format)."""
    answer, previews = _rag_answer(req.question, req.top_k)
    return {"answer": answer, "context_preview": previews}

    # --- ORIGINAL /api/ask INLINE IMPLEMENTATION (commented out so we can revert if needed) ---
    # top_k = max(1, min(10, req.top_k))
    # results = collection.query(query_texts=[req.question], n_results=top_k)
    #
    # docs = (results.get("documents") or [[]])[0]
    # metas = (results.get("metadatas") or [[]])[0]
    #
    # if not docs:
    #     return {
    #         "answer": "No relevant context found in indexed PDFs.",
    #         "context_preview": [],
    #     }
    #
    # previews = []
    # for d, m in zip(docs, metas):
    #     src = m.get("source", "unknown")
    #     chunk_id = m.get("chunk")
    #     label = f"({src}, chunk {chunk_id})"
    #     previews.append(f"- {label} {d[:700]}")
    #
    # context_text = "\n".join(previews)
    # messages = [
    #     {
    #         "role": "system",
    #         "content": (
    #             "You are a trauma/ATLS medical assistant.\n"
    #             "You MUST use ONLY the provided context to answer.\n"
    #             "If the answer is not clearly in the context, reply: \"Not found in context.\"\n"
    #             "Do NOT hallucinate or invent medical facts.\n"
    #             "If the question asks for a list (injuries, steps, interventions), respond in bullet points.\n"
    #             "Do NOT repeat the ABCDE list unless the user explicitly asks for ABCDE.\n"
    #             "Keep answers concise and clinically accurate.\n"
    #         ),
    #     },
    #     {
    #         "role": "user",
    #         "content": f"Question: {req.question}\n\nContext:\n{context_text}",
    #     },
    # ]
    #
    # try:
    #     if USE_REMOTE_LLM:
    #         answer = call_remote_chat(messages, temperature=0.1, max_tokens=400)
    #     else:
    #         answer = call_local_ollama(messages)
    # except Exception as e:
    #     return {
    #         "answer": f"Error calling LLM: {e}",
    #         "context_preview": previews,
    #     }
    #
    # return {
    #     "answer": answer,
    #     "context_preview": previews,
    # }
    # --- END ORIGINAL ---


@app.post("/v1/chat/completions")
def chat_completions(req: OpenAIChatRequest):
    """
    OpenAI-compatible endpoint for the .NET ER Simulator.
    Accepts { model, messages, temperature?, max_tokens? }, returns { choices, context_preview }.
    """
    user_parts = [m.content for m in req.messages if m.role == "user"]
    full_prompt = " ".join(user_parts).strip() if user_parts else ""

    if not full_prompt:
        return {
            "choices": [
                {
                    "index": 0,
                    "message": {"role": "assistant", "content": "No user message provided."},
                    "finish_reason": "stop",
                }
            ],
            "context_preview": [],
        }

    # Use only the current question for retrieval so we get PDF chunks about the topic, not the conversation
    question_for_rag = extract_question_for_rag(full_prompt)
    requested_model = (req.model or "").strip() or None
    answer, previews = _rag_answer(question_for_rag, top_k=5, model=requested_model)

    return {
        "id": "rag-chat-" + str(uuid.uuid4()),
        "object": "chat.completion",
        "choices": [
            {
                "index": 0,
                "message": {"role": "assistant", "content": answer},
                "finish_reason": "stop",
            }
        ],
        "usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0},
        "context_preview": previews,
    }

