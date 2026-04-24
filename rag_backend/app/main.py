# app/main.py — clean RAG backend using Chroma + remote LLM (Jeremy's server)
# Fully cleaned and updated to remove forced ABCDE answers

# ---------- Standard library ----------
import json
import logging
import os
import uuid
from typing import Optional, List, Dict, Any, Tuple

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
# Untracked secrets: rag_backend/.env.secrets (gitignored; copy from .env.secrets.example). ANTHROPIC_API_KEY=...
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
# Defaults match fine-tuned MedGemma on serve.py:11435 (override with OLLAMA_URL / OLLAMA_MODEL / .env).
OLLAMA_URL = os.getenv("OLLAMA_URL", "http://127.0.0.1:11435")
OLLAMA_MODEL = os.getenv("OLLAMA_MODEL", "medgemma-ft")
OLLAMA_TIMEOUT = int(os.getenv("OLLAMA_TIMEOUT", "300"))
# RAG+conversation prompts to a local fine-tune server (e.g. 4B on GPU) can
# OOM or exceed the tokenizer limit; we cap the context string before the LLM call.
RAG_LOCAL_MAX_CONTEXT_CHARS = int(os.getenv("RAG_LOCAL_MAX_CONTEXT_CHARS", "20000"))

# Claude (Anthropic) — key from .env.secrets (untracked) or env
# Model ID for Claude Opus 4.6: claude-opus-4-6 (see https://docs.anthropic.com/en/docs/about-claude/models)
ANTHROPIC_API_KEY = os.getenv("ANTHROPIC_API_KEY", "").strip()
ANTHROPIC_MODEL = os.getenv("ANTHROPIC_MODEL", "claude-opus-4-6").strip() or "claude-opus-4-6"
ANTHROPIC_TIMEOUT = int(os.getenv("ANTHROPIC_TIMEOUT", "120"))
ANTHROPIC_VERSION = "2023-06-01"

# ---------- Knowledge Base API (optional; replaces Chroma retrieval when RAG_CONTEXT_SOURCE=knowledge_api) ----------
RAG_CONTEXT_SOURCE = os.getenv("RAG_CONTEXT_SOURCE", "chromadb").strip().lower()
KNOWLEDGE_API_SEARCH_URL = os.getenv("KNOWLEDGE_API_SEARCH_URL", "").strip()
KNOWLEDGE_API_KEY = os.getenv("KNOWLEDGE_API_KEY", "").strip()
KNOWLEDGE_API_AUTH_STYLE = os.getenv("KNOWLEDGE_API_AUTH_STYLE", "bearer").strip().lower()
KNOWLEDGE_API_AUTH_HEADER = os.getenv("KNOWLEDGE_API_AUTH_HEADER", "Authorization").strip() or "Authorization"
KNOWLEDGE_API_AUTH_PREFIX = os.getenv("KNOWLEDGE_API_AUTH_PREFIX", "Bearer ").strip()
KNOWLEDGE_API_METHOD = os.getenv("KNOWLEDGE_API_METHOD", "POST").strip().upper()
KNOWLEDGE_API_QUERY_KEY = os.getenv("KNOWLEDGE_API_QUERY_KEY", "query").strip() or "query"
KNOWLEDGE_API_TOP_K_KEY = os.getenv("KNOWLEDGE_API_TOP_K_KEY", "top_k").strip() or "top_k"
KNOWLEDGE_API_EXTRA_BODY = os.getenv("KNOWLEDGE_API_EXTRA_BODY", "").strip()
KNOWLEDGE_API_TIMEOUT = int(os.getenv("KNOWLEDGE_API_TIMEOUT", "60"))
KNOWLEDGE_API_CONTENT_TYPE = os.getenv("KNOWLEDGE_API_CONTENT_TYPE", "application/json").strip()
KNOWLEDGE_API_GET_QUERY_PARAM = os.getenv("KNOWLEDGE_API_GET_QUERY_PARAM", "q").strip() or "q"
KNOWLEDGE_API_GET_LIMIT_PARAM = os.getenv("KNOWLEDGE_API_GET_LIMIT_PARAM", "limit").strip() or "limit"
KNOWLEDGE_API_QUERY_AUTH_PARAM = os.getenv("KNOWLEDGE_API_QUERY_AUTH_PARAM", "api_key").strip() or "api_key"
KNOWLEDGE_API_HEADERS_JSON = os.getenv("KNOWLEDGE_API_HEADERS_JSON", "").strip()

if RAG_CONTEXT_SOURCE == "knowledge_api" and not KNOWLEDGE_API_SEARCH_URL:
    logging.warning(
        "RAG_CONTEXT_SOURCE=knowledge_api but KNOWLEDGE_API_SEARCH_URL is empty — RAG will return errors until set."
    )

# Console-friendly retrieval debugging (visible at default uvicorn log level)
_rag_log = logging.getLogger("rag")


def _retrieval_source_label() -> str:
    """Value returned in API JSON and used in logs."""
    return "knowledge_api" if RAG_CONTEXT_SOURCE == "knowledge_api" else "chromadb"


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

_rag_log.info(
    "RAG startup: retrieval=%s | chromadb: PDF chunks + embeddings | knowledge_api: HTTP Knowledge Base | "
    "active_mode=%s | kb_url_configured=%s",
    _retrieval_source_label(),
    RAG_CONTEXT_SOURCE,
    bool(KNOWLEDGE_API_SEARCH_URL),
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
    temperature: float = 0.6,
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
        "temperature": temperature,
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
    # When false, skip Chroma + Knowledge API retrieval and answer from the LLM’s general knowledge only
    use_rag: bool = True


def _use_claude(model: Optional[str]) -> bool:
    """True if the requested model is Claude (user chose Claude mode)."""
    if not model:
        return False
    return "claude" in model.lower()


def _truncate_rag_context_for_local_llm(context_text: str) -> str:
    """Shorter RAG context for medgemma-ft and similar; avoids OOM on long Chroma+prompt bundles."""
    if len(context_text) <= RAG_LOCAL_MAX_CONTEXT_CHARS:
        return context_text
    _rag_log.warning(
        "Truncating RAG context for local LLM: %d chars -> %d (RAG_LOCAL_MAX_CONTEXT_CHARS).",
        len(context_text),
        RAG_LOCAL_MAX_CONTEXT_CHARS,
    )
    return (context_text[: RAG_LOCAL_MAX_CONTEXT_CHARS - 1].rstrip() + "…")


def _is_not_found_in_context(answer: str) -> bool:
    """True if the RAG answer indicates the topic was not in the indexed materials.
    Detects English and Spanish (and similar) 'not in context' phrasing so Claude fallback runs."""
    if not answer or len(answer.strip()) < 10:
        return False
    a = answer.strip().lower()
    # English
    if "not found in context" in a:
        return True
    if "don't cover" in a or "doesn't cover" in a or "do not cover" in a:
        return True
    if "materials i have" in a and ("don't cover" in a or "focus on" in a and "don't" in a):
        return True
    if "no relevant context" in a or "no relevant information" in a:
        return True
    # Spanish (so fallback gives general-knowledge answer in same language)
    if "no se encuentra en el contexto" in a or "no está en el contexto" in a:
        return True
    if "no cubre" in a and ("tema" in a or "contexto" in a or "información" in a):
        return True
    if "la información disponible no cubre" in a:
        return True
    return False


def _knowledge_api_headers() -> Dict[str, str]:
    headers: Dict[str, str] = {"Content-Type": KNOWLEDGE_API_CONTENT_TYPE}
    if KNOWLEDGE_API_HEADERS_JSON:
        try:
            extra = json.loads(KNOWLEDGE_API_HEADERS_JSON)
            if isinstance(extra, dict):
                for k, v in extra.items():
                    headers[str(k)] = str(v)
        except json.JSONDecodeError:
            logging.warning("KNOWLEDGE_API_HEADERS_JSON is not valid JSON; ignoring.")
    if KNOWLEDGE_API_KEY and KNOWLEDGE_API_AUTH_STYLE == "bearer":
        headers[KNOWLEDGE_API_AUTH_HEADER] = f"{KNOWLEDGE_API_AUTH_PREFIX}{KNOWLEDGE_API_KEY}".strip()
    elif KNOWLEDGE_API_KEY and KNOWLEDGE_API_AUTH_STYLE == "api_key":
        hname = KNOWLEDGE_API_AUTH_HEADER if KNOWLEDGE_API_AUTH_HEADER != "Authorization" else "X-Api-Key"
        headers[hname] = KNOWLEDGE_API_KEY
    return headers


def _knowledge_api_request(question: str, top_k: int) -> Optional[Any]:
    """Call external KB search; return parsed JSON or None on failure."""
    if not KNOWLEDGE_API_SEARCH_URL:
        return None
    s = requests.Session()
    s.mount("https://", HTTPAdapter(max_retries=Retry(total=2, backoff_factor=0.3)))
    s.mount("http://", HTTPAdapter(max_retries=Retry(total=2, backoff_factor=0.3)))
    try:
        if KNOWLEDGE_API_METHOD == "GET":
            params: Dict[str, str] = {
                KNOWLEDGE_API_GET_QUERY_PARAM: question,
                KNOWLEDGE_API_GET_LIMIT_PARAM: str(top_k),
            }
            if KNOWLEDGE_API_KEY and KNOWLEDGE_API_AUTH_STYLE == "query":
                params[KNOWLEDGE_API_QUERY_AUTH_PARAM] = KNOWLEDGE_API_KEY
            hdrs = {k: v for k, v in _knowledge_api_headers().items() if k.lower() != "content-type"}
            r = s.get(
                KNOWLEDGE_API_SEARCH_URL,
                params=params,
                headers=hdrs,
                timeout=KNOWLEDGE_API_TIMEOUT,
            )
        else:
            body: Dict[str, Any] = {
                KNOWLEDGE_API_QUERY_KEY: question,
                KNOWLEDGE_API_TOP_K_KEY: top_k,
            }
            if KNOWLEDGE_API_EXTRA_BODY:
                try:
                    body.update(json.loads(KNOWLEDGE_API_EXTRA_BODY))
                except json.JSONDecodeError:
                    logging.warning("KNOWLEDGE_API_EXTRA_BODY is not valid JSON; ignoring.")
            r = s.post(
                KNOWLEDGE_API_SEARCH_URL,
                headers=_knowledge_api_headers(),
                json=body,
                timeout=KNOWLEDGE_API_TIMEOUT,
            )
        if r.status_code >= 400:
            logging.warning("Knowledge API HTTP %s: %s", r.status_code, (r.text or "")[:800])
            return None
        try:
            return r.json()
        except Exception:
            t = (r.text or "").strip()
            return {"_single_text": t} if t else None
    except Exception as e:
        logging.warning("Knowledge API request failed: %s", e)
        return None


def _kb_extract_text_from_item(item: Any) -> Tuple[str, Dict[str, Any]]:
    meta: Dict[str, Any] = {}
    if item is None:
        return "", meta
    if isinstance(item, str):
        return item.strip(), meta
    if not isinstance(item, dict):
        return str(item).strip(), meta
    text = ""
    for k in ("content", "text", "body", "snippet", "chunk_text", "document", "answer", "passage"):
        v = item.get(k)
        if isinstance(v, str) and v.strip():
            text = v.strip()
            break
    if not text:
        title = item.get("title") or item.get("name") or ""
        content = item.get("content") or item.get("text") or ""
        if isinstance(title, str) and isinstance(content, str) and (str(title).strip() or str(content).strip()):
            text = f"{str(title).strip()}\n{str(content).strip()}".strip()
        else:
            text = json.dumps(item, ensure_ascii=False)[:4000]
    src = item.get("source") or item.get("document_id") or item.get("id") or item.get("url") or item.get("title")
    meta["source"] = str(src)[:500] if src is not None else "knowledge_api"
    ck = item.get("chunk")
    if ck is None:
        ck = item.get("index") or item.get("page")
    meta["chunk"] = ck
    return text, meta


def _knowledge_api_parse_response(data: Any, max_items: int) -> Tuple[List[str], List[Dict[str, Any]]]:
    if data is None:
        return [], []
    if isinstance(data, dict) and "_single_text" in data:
        t = str(data["_single_text"]).strip()
        if not t:
            return [], []
        return [t], [{"source": "knowledge_api", "chunk": 0}]
    items: List[Any] = []
    if isinstance(data, list):
        items = data
    elif isinstance(data, dict):
        for key in ("results", "documents", "chunks", "hits", "items", "records", "matches", "context"):
            val = data.get(key)
            if isinstance(val, list):
                items = val
                break
        if not items and isinstance(data.get("data"), dict):
            inner = data["data"]
            for key in ("results", "documents", "chunks", "hits", "items", "records"):
                val = inner.get(key)
                if isinstance(val, list):
                    items = val
                    break
        if not items and isinstance(data.get("answer"), str) and data["answer"].strip():
            items = [{"content": data["answer"]}]
    docs: List[str] = []
    metas: List[Dict[str, Any]] = []
    cap = max(1, min(20, max_items))
    for i, it in enumerate(items[:cap]):
        text, m = _kb_extract_text_from_item(it)
        if not text:
            continue
        if m.get("chunk") is None:
            m["chunk"] = i
        docs.append(text)
        metas.append(m)
    return docs, metas


def _retrieve_documents(question: str, top_k: int) -> Tuple[List[str], List[Dict[str, Any]]]:
    """Vector store (Chroma) or external Knowledge API, depending on RAG_CONTEXT_SOURCE."""
    qprev = (question[:120] + "…") if len(question) > 120 else question
    if RAG_CONTEXT_SOURCE == "knowledge_api":
        url_disp = KNOWLEDGE_API_SEARCH_URL or "(not set)"
        if len(url_disp) > 96:
            url_disp = url_disp[:96] + "…"
        _rag_log.info(
            "RAG retrieval: source=KNOWLEDGE_API | top_k=%s | url=%s | question_preview=%r",
            top_k,
            url_disp,
            qprev,
        )
        raw = _knowledge_api_request(question, top_k)
        docs, metas = _knowledge_api_parse_response(raw, top_k)
        _rag_log.info(
            "RAG retrieval: KNOWLEDGE_API returned %s chunk(s) (raw_response=%s)",
            len(docs),
            "ok" if raw is not None else "none/error",
        )
        return docs, metas
    _rag_log.info(
        "RAG retrieval: source=CHROMADB_PDFS | top_k=%s | question_preview=%r",
        top_k,
        qprev,
    )
    results = collection.query(query_texts=[question], n_results=top_k)
    docs = (results.get("documents") or [[]])[0]
    metas = (results.get("metadatas") or [[]])[0]
    _rag_log.info("RAG retrieval: CHROMADB_PDFS returned %s chunk(s)", len(docs))
    return docs, metas


def _rag_answer(
    question: str,
    top_k: int = 4,
    model: Optional[str] = None,
    prompt_for_llm: Optional[str] = None,
) -> tuple[str, list[str]]:
    """Run RAG: retrieve context (Chroma or Knowledge API), build context, call LLM. Returns (answer, context_preview).
    model: optional request model; if it contains 'claude', use Claude API; else Ollama or remote.
    prompt_for_llm: when set (e.g. full conversation from .NET), the LLM sees this so it can resolve
    references like 'the same answer in English'; retrieval still uses question."""
    top_k = max(1, min(10, top_k))
    if RAG_CONTEXT_SOURCE == "knowledge_api" and not KNOWLEDGE_API_SEARCH_URL:
        return (
            "Knowledge API mode is on but KNOWLEDGE_API_SEARCH_URL is not set. Add it to rag_backend/.env — see docs/KNOWLEDGE-API-RAG.md.",
            [],
        )

    docs, metas = _retrieve_documents(question, top_k)

    if not docs:
        empty = (
            "No relevant context returned from the knowledge API."
            if RAG_CONTEXT_SOURCE == "knowledge_api"
            else "No relevant context found in indexed PDFs."
        )
        return empty, []

    previews = []
    for d, m in zip(docs, metas):
        src = m.get("source", "unknown")
        chunk_id = m.get("chunk")
        label = f"({src}, chunk {chunk_id})"
        previews.append(f"- {label} {d[:700]}")

    context_text = "\n".join(previews)
    if not _use_claude(model) and not USE_REMOTE_LLM:
        context_text = _truncate_rag_context_for_local_llm(context_text)
    has_conversation = prompt_for_llm is not None and len((prompt_for_llm or "").strip()) > 0
    system_prompt = (
        "You are an ER attending talking to a resident or student—a real person they can talk to, not a generic AI. Use ONLY the context below to answer. Speak directly to them, as if you're in the room.\n"
        "VOICE: Sound like a colleague and mentor: natural, personable, occasionally use contractions (we're, you'll, that's). Vary your sentence length and rhythm. Avoid stiff or listy phrasing; avoid sounding like a textbook or a report. Be someone worth talking to.\n"
        "CRITICAL: You MUST respond in the EXACT same language as the student's question. If the question is in Spanish, your entire answer must be in Spanish. If in English, answer in English. Do not default to English when the question is in another language.\n"
    )
    if has_conversation:
        system_prompt += (
            "The student may refer to a previous answer (e.g. 'the same answer in English', 'that in Spanish'). Use the conversation above to identify which answer they mean and provide it in the requested language or form. The context below may still help; use it when relevant.\n"
        )
    system_prompt += (
        "FORBIDDEN: Do not refer to the context as text or a document. Never say: 'this text focuses on', 'the document says', 'it talks about', 'the context mentions'. Never describe what the source says; instead, teach that information as your own.\n"
        "GOOD: 'With blunt abdominal trauma, you're looking for tenderness, guarding, rigidity—so we examine them carefully.'\n"
        "BAD: 'This text focuses on trauma and talks about abdominal tenderness.'\n"
        "If the answer is not in the context, say: \"Not found in context.\" Keep your answer to one or two brief paragraphs—err on the concise side. No bullet points unless they ask for a list. Do NOT repeat the ABCDE list unless they ask for ABCDE.\n"
    )
    lang_hint = _language_hint(prompt_for_llm if has_conversation else question)
    if has_conversation:
        user_content = f"{lang_hint}{prompt_for_llm.strip()}\n\nContext from reference materials:\n{context_text}"
    else:
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
    except Exception:
        # Typical causes: medgemma-ft on OLLAMA_URL down, timeout, or GPU OOM on huge prompts.
        logging.exception(
            "RAG LLM call failed; check fine-tune server at OLLAMA_URL (reachable from the RAG process) "
            "and consider lowering RAG top_k or RAG_LOCAL_MAX_CONTEXT_CHARS. OLLAMA_URL=%r OLLAMA_MODEL=%r",
            OLLAMA_URL,
            OLLAMA_MODEL,
        )
        # Still return previews so the UI can show "references" even when the LLM step failed
        return (
            "I couldn't look that up in my references right now. Please try again in a moment.",
            previews,
        )

    # Claude mode: if the answer says the topic isn't in the RAG materials, fall back to general Claude for a concise response
    if _use_claude(model) and _is_not_found_in_context(answer):
        try:
            fallback_system = (
                "You are an ER attending talking to a resident or student—a real person they can talk to, not a generic AI. The student may have asked something outside your trauma notes, or they may want a previous answer repeated or translated (e.g. 'the same answer in English'). "
                "If they refer to a previous answer, use the conversation to identify which one and give it in the requested language. Otherwise answer from general medical knowledge in one or two brief paragraphs—err on the concise side. "
                "VOICE: Sound like a colleague and mentor: personable, use contractions when it fits, vary your rhythm. Don't sound like a textbook or a chatbot. Be someone worth talking to.\n"
                "CRITICAL: You MUST respond in the EXACT same language as the student's current question. If they asked in Spanish, answer entirely in Spanish. If in English, in English. Do not default to English.\n"
                "Do not say the topic was not in your materials—just answer the question directly."
            )
            fallback_user = (
                f"{_language_hint(prompt_for_llm if has_conversation else question)}"
                + ("Question: " if not has_conversation else "")
                + (prompt_for_llm.strip() if has_conversation else question)
            )
            answer = call_claude(system=fallback_system, user_content=fallback_user, max_tokens=1024)
            # Return empty previews so the UI doesn't show RAG references for this general answer
            return answer, []
        except Exception as e:
            logging.warning("Claude fallback (general knowledge) failed: %s", e)
            # Keep the original "not found" answer
            return answer, previews

    return answer, previews


def _general_only_answer(
    question: str,
    model: Optional[str] = None,
    prompt_for_llm: Optional[str] = None,
) -> tuple[str, list[str]]:
    """LLM only — no Chroma or Knowledge API retrieval. Same model routing (Claude / remote / Ollama) as RAG path."""
    has_conversation = prompt_for_llm is not None and len((prompt_for_llm or "").strip()) > 0
    system_prompt = (
        "You are an ER attending talking to a resident or student. Answer from well-established "
        "emergency and general clinical knowledge. Be concise, accurate, and direct.\n"
        "CRITICAL: You MUST respond in the EXACT same language as the student's current question.\n"
        "Keep the answer to one or two short paragraphs. Sound like a person, not a document.\n"
    )
    lang_hint = _language_hint(prompt_for_llm if has_conversation else question)
    if has_conversation:
        user_content = f"{lang_hint}{prompt_for_llm.strip()}"
    else:
        user_content = f"{lang_hint}Question: {question}"
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
        logging.warning("General (no RAG) LLM call failed: %s", e)
        return (
            "I couldn't get an answer from the model just now. Please try again in a moment.",
            [],
        )
    return answer, []


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
        "rag_context_source": RAG_CONTEXT_SOURCE,
        "retrieval_source": _retrieval_source_label(),
        "knowledge_api_url_configured": bool(KNOWLEDGE_API_SEARCH_URL),
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
    return {
        "answer": answer,
        "context_preview": previews,
        "retrieval_source": _retrieval_source_label(),
    }

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
            "retrieval_source": _retrieval_source_label(),
        }

    # Use only the current question for retrieval so we get PDF chunks about the topic, not the conversation
    question_for_rag = extract_question_for_rag(full_prompt)
    has_conversation = "Student's current question:" in full_prompt or "Recent conversation:" in full_prompt
    prompt_for_llm = full_prompt if has_conversation else None
    requested_model = (req.model or "").strip() or None

    if not req.use_rag:
        answer, previews = _general_only_answer(
            question_for_rag, model=requested_model, prompt_for_llm=prompt_for_llm
        )
        retrieval = "off"
    else:
        answer, previews = _rag_answer(
            question_for_rag, top_k=5, model=requested_model, prompt_for_llm=prompt_for_llm
        )
        retrieval = _retrieval_source_label()

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
        "retrieval_source": retrieval,
    }

