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

# ---------- Load environment (.env) ----------
load_dotenv()

# ---------- Config flags ----------
USE_REMOTE_LLM = os.getenv("USE_REMOTE_LLM", "0") == "1"

# Remote LLM configuration (Jeremy's OpenAI-compatible server)
OPENAI_BASE_URL = os.getenv("OPENAI_BASE_URL", "").rstrip("/")
OPENAI_API_KEY = os.getenv("OPENAI_API_KEY", "")
OPENAI_MODEL = os.getenv("OPENAI_MODEL", "meta-llama/Llama-3.2-1B-instruct")

# Local Ollama (only when USE_REMOTE_LLM=0)
OLLAMA_URL = os.getenv("OLLAMA_URL", "http://127.0.0.1:11434")
OLLAMA_MODEL = os.getenv("OLLAMA_MODEL", "llama3.2:1b")

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


def call_local_ollama(messages: List[Dict[str, str]]):
    raise RuntimeError("Local Ollama disabled (USE_REMOTE_LLM should be 1).")


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
    """
    Trauma-safe RAG answer engine (NO forced ABCDE format).
    """

    top_k = max(1, min(10, req.top_k))
    results = collection.query(query_texts=[req.question], n_results=top_k)

    docs = (results.get("documents") or [[]])[0]
    metas = (results.get("metadatas") or [[]])[0]

    if not docs:
        return {
            "answer": "No relevant context found in indexed PDFs.",
            "context_preview": [],
        }

    # Build context preview
    previews = []
    for d, m in zip(docs, metas):
        src = m.get("source", "unknown")
        chunk_id = m.get("chunk")
        label = f"({src}, chunk {chunk_id})"
        previews.append(f"- {label} {d[:700]}")

    context_text = "\n".join(previews)

    # --- NEW SAFER PROMPT (NO ABCDE FORCING) ---
    messages = [
        {
            "role": "system",
            "content": (
                "You are a trauma/ATLS medical assistant.\n"
                "You MUST use ONLY the provided context to answer.\n"
                "If the answer is not clearly in the context, reply: \"Not found in context.\"\n"
                "Do NOT hallucinate or invent medical facts.\n"
                "If the question asks for a list (injuries, steps, interventions), respond in bullet points.\n"
                "Do NOT repeat the ABCDE list unless the user explicitly asks for ABCDE.\n"
                "Keep answers concise and clinically accurate.\n"
            ),
        },
        {
            "role": "user",
            "content": f"Question: {req.question}\n\nContext:\n{context_text}",
        },
    ]

    # Call LLM
    try:
        if USE_REMOTE_LLM:
            answer = call_remote_chat(messages, temperature=0.1, max_tokens=400)
        else:
            answer = call_local_ollama(messages)
    except Exception as e:
        return {
            "answer": f"Error calling LLM: {e}",
            "context_preview": previews,
        }

    return {
        "answer": answer,
        "context_preview": previews,
    }

