"""
Ollama-compatible inference server for the fine-tuned MedGemma adapter.

Run:
    source rag_backend/.venv/bin/activate
    uvicorn rag_backend.finetuning.serve:app --host 127.0.0.1 --port 11435

Endpoints (same shapes as real Ollama):
    POST /api/chat       body: {model, messages:[{role,content}], stream?, options?}
    POST /api/generate   body: {model, prompt, stream?, options?}
    GET  /api/tags       returns the single model tag we serve
    GET  /health         simple liveness check
"""
from __future__ import annotations

import os

# ---- Env vars must be set BEFORE `import torch` or they won't take effect. ----
# HF model cache + which GPU we use.
os.environ.setdefault("HF_HOME", "/media/volume/AI_Core_CoDIRA_StorageVolume/huggingface_cache")
os.environ.setdefault("CUDA_VISIBLE_DEVICES", "0")
# Gemma3 in transformers triggers torch.compile + cudagraph trees during
# generate(). That path has a known bug (`assert torch._C._is_key_in_tls(...)`)
# when generate() runs from a worker thread spawned by FastAPI's default
# executor. Disabling cudagraphs avoids the crash with a negligible perf hit
# on a 4B model; regular kernels still run on CUDA.
os.environ.setdefault("TORCHINDUCTOR_CUDAGRAPHS", "0")

import asyncio
import concurrent.futures
import json
import logging
import threading
import time
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional

import torch
from fastapi import FastAPI
from fastapi.responses import StreamingResponse
from pydantic import BaseModel
from transformers import AutoModelForCausalLM, AutoTokenizer
from peft import PeftModel

# Belt + suspenders: also disable via the Python API in case the env var is
# read too late by whichever inductor module loads first.
try:
    import torch._inductor.config as _inductor_config
    _inductor_config.triton.cudagraphs = False
except Exception:  # pragma: no cover - best-effort
    pass

BASE_MODEL_ID = os.getenv("FT_BASE_MODEL", "google/medgemma-4b-it")
ADAPTER_PATH = os.getenv(
    "FT_ADAPTER_PATH",
    "/home/exouser/Desktop/Emergency-Room-Simulator-Repository/rag_backend/finetuning/medgemma-finetuned/checkpoint-100",
)
MODEL_TAG = os.getenv("FT_MODEL_TAG", "medgemma-ft")
DEVICE = "cuda:0" if torch.cuda.is_available() else "cpu"

# Optional auto-injected system prompt. Applied only to requests that don't
# already carry their own system message (so callers like the RAG backend,
# which send their own system prompt, are not double-prompted).
_DEFAULT_SYSTEM_PROMPT = (
    "You are a warm, empathetic GP (general practitioner). "
    "Use plain, simple language. Ask only 1-2 focused questions at a time. "
    "Show empathy and good bedside manner. "
    "Never overwhelm the patient with too many questions at once."
)
FT_SYSTEM_PROMPT = os.getenv("FT_SYSTEM_PROMPT", _DEFAULT_SYSTEM_PROMPT).strip()

log = logging.getLogger("medgemma-ft-serve")
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")

_infer_lock = threading.Lock()
tokenizer = None
model = None

# PyTorch's cudagraph-tree manager keeps state in thread-local storage, so all
# generate() calls should run on the same worker thread that initialised CUDA.
# We route every request through this single-worker pool and warm it up at
# startup; calling run_in_executor(None, ...) would spawn fresh threads and
# make debugging the TLS bug above much harder if cudagraphs ever get
# re-enabled.
_infer_executor = concurrent.futures.ThreadPoolExecutor(
    max_workers=1, thread_name_prefix="medgemma-ft-infer"
)


def _warmup() -> None:
    with torch.inference_mode():
        warm_inputs = tokenizer("hello", return_tensors="pt").to(DEVICE)
        model.generate(
            **warm_inputs,
            max_new_tokens=4,
            do_sample=False,
            pad_token_id=tokenizer.pad_token_id or tokenizer.eos_token_id,
        )


def _load_model() -> None:
    global tokenizer, model
    log.info("Loading tokenizer from %s", BASE_MODEL_ID)
    tokenizer = AutoTokenizer.from_pretrained(BASE_MODEL_ID)

    log.info("Loading base model (bf16, eager attention) on %s", DEVICE)
    base = AutoModelForCausalLM.from_pretrained(
        BASE_MODEL_ID,
        torch_dtype=torch.bfloat16,
        device_map=DEVICE,
        attn_implementation="eager",
    )

    log.info("Attaching LoRA adapter at %s", ADAPTER_PATH)
    merged = PeftModel.from_pretrained(base, ADAPTER_PATH)
    merged.eval()
    model = merged

    # Avoid Gemma3's default static-cache compile path (torch.compile +
    # cudagraphs). We want plain eager decoding so we never hit the
    # cudagraph_trees TLS assertion.
    try:
        if getattr(model, "generation_config", None) is not None:
            model.generation_config.cache_implementation = None
    except Exception as e:  # pragma: no cover - best-effort
        log.warning("Could not override generation_config.cache_implementation: %s", e)

    log.info("Warming up on inference worker thread (1 generate call)...")
    _infer_executor.submit(_warmup).result()

    if FT_SYSTEM_PROMPT:
        preview = FT_SYSTEM_PROMPT[:120] + ("..." if len(FT_SYSTEM_PROMPT) > 120 else "")
        log.info("FT_SYSTEM_PROMPT active (auto-injected when caller sends no system msg): %r", preview)
    else:
        log.info("FT_SYSTEM_PROMPT not set; caller's messages are used verbatim.")
    log.info("Ready.")


def _messages_to_prompt(messages: List[Dict[str, str]]) -> str:
    """Apply Gemma chat template to an OpenAI-style message list.

    If the caller did not provide any `system` message AND FT_SYSTEM_PROMPT is
    configured, we auto-inject it as the system preamble. Callers that already
    send their own system prompt (e.g. the RAG backend's ER-attending voice)
    are left untouched so we don't stack two competing personas.
    """
    caller_has_system = any(
        (m.get("role") == "system" and (m.get("content") or "").strip())
        for m in messages
    )
    chat: List[Dict[str, str]] = []
    system_preamble = "" if caller_has_system else (FT_SYSTEM_PROMPT + "\n\n" if FT_SYSTEM_PROMPT else "")
    for m in messages:
        role = m.get("role", "user")
        content = m.get("content", "")
        if role == "system":
            system_preamble += (content + "\n\n")
        elif role in ("user", "human"):
            chat.append({"role": "user", "content": (system_preamble + content).strip()})
            system_preamble = ""
        elif role in ("assistant", "model"):
            chat.append({"role": "assistant", "content": content})
    if not chat:
        chat = [{"role": "user", "content": system_preamble.strip() or ""}]

    return tokenizer.apply_chat_template(
        chat,
        add_generation_prompt=True,
        tokenize=False,
    )


def _generate(prompt_text: str, options: Optional[Dict[str, Any]]) -> str:
    options = options or {}
    max_new_tokens = int(options.get("num_predict") or options.get("max_tokens") or 400)
    temperature = float(options.get("temperature", 0.7))
    top_p = float(options.get("top_p", 0.9))
    do_sample = temperature > 0

    inputs = tokenizer(prompt_text, return_tensors="pt").to(DEVICE)
    with _infer_lock, torch.inference_mode():
        outputs = model.generate(
            **inputs,
            max_new_tokens=max_new_tokens,
            do_sample=do_sample,
            temperature=temperature if do_sample else 1.0,
            top_p=top_p if do_sample else 1.0,
            pad_token_id=tokenizer.pad_token_id or tokenizer.eos_token_id,
        )
    generated = outputs[0, inputs["input_ids"].shape[-1]:]
    return tokenizer.decode(generated, skip_special_tokens=True).strip()


# ---------- FastAPI ----------

class ChatMessage(BaseModel):
    role: str
    content: str


class ChatRequest(BaseModel):
    model: Optional[str] = None
    messages: Optional[List[ChatMessage]] = None
    prompt: Optional[str] = None
    stream: Optional[bool] = False
    options: Optional[Dict[str, Any]] = None


app = FastAPI(title="medgemma-ft serve")


@app.on_event("startup")
def _startup() -> None:
    _load_model()


@app.get("/health")
def health() -> Dict[str, Any]:
    return {
        "status": "ok",
        "model": MODEL_TAG,
        "base_model": BASE_MODEL_ID,
        "adapter_path": ADAPTER_PATH,
        "device": DEVICE,
        "loaded": model is not None,
        "system_prompt_active": bool(FT_SYSTEM_PROMPT),
        "system_prompt_preview": (FT_SYSTEM_PROMPT[:160] + ("..." if len(FT_SYSTEM_PROMPT) > 160 else "")),
    }


@app.get("/api/tags")
def tags() -> Dict[str, Any]:
    return {
        "models": [
            {
                "name": MODEL_TAG,
                "model": MODEL_TAG,
                "modified_at": datetime.now(timezone.utc).isoformat(),
                "size": 0,
                "digest": "",
                "details": {
                    "family": "gemma3",
                    "parameter_size": "4B",
                    "quantization_level": "bf16",
                },
            }
        ]
    }


def _chat_response_body(text: str, started_ns: int) -> Dict[str, Any]:
    """Return a body that looks like BOTH /api/chat and /api/generate shapes,
    so any Ollama client is happy."""
    now = datetime.now(timezone.utc).isoformat()
    return {
        "model": MODEL_TAG,
        "created_at": now,
        "message": {"role": "assistant", "content": text},
        "response": text,
        "done": True,
        "total_duration": time.time_ns() - started_ns,
        "prompt_eval_count": 0,
        "eval_count": 0,
    }


def _run_inference_for_request(req: ChatRequest) -> str:
    if req.messages:
        prompt_text = _messages_to_prompt([m.model_dump() for m in req.messages])
    elif req.prompt is not None:
        prompt_text = _messages_to_prompt([{"role": "user", "content": req.prompt}])
    else:
        return ""
    return _generate(prompt_text, req.options)


async def _stream_response(req: ChatRequest) -> StreamingResponse:
    """Ollama streaming is newline-delimited JSON; we emit one chunk + a done chunk."""
    started = time.time_ns()

    async def gen():
        loop = asyncio.get_event_loop()
        text = await loop.run_in_executor(_infer_executor, _run_inference_for_request, req)
        chunk = {
            "model": MODEL_TAG,
            "created_at": datetime.now(timezone.utc).isoformat(),
            "message": {"role": "assistant", "content": text},
            "response": text,
            "done": False,
        }
        yield json.dumps(chunk) + "\n"
        final = _chat_response_body("", started)
        final["message"]["content"] = ""
        final["response"] = ""
        yield json.dumps(final) + "\n"

    return StreamingResponse(gen(), media_type="application/x-ndjson")


@app.post("/api/chat")
async def api_chat(req: ChatRequest):
    if req.stream:
        return await _stream_response(req)
    started = time.time_ns()
    text = await asyncio.get_event_loop().run_in_executor(
        _infer_executor, _run_inference_for_request, req
    )
    return _chat_response_body(text, started)


@app.post("/api/generate")
async def api_generate(req: ChatRequest):
    if req.stream:
        return await _stream_response(req)
    started = time.time_ns()
    text = await asyncio.get_event_loop().run_in_executor(
        _infer_executor, _run_inference_for_request, req
    )
    return _chat_response_body(text, started)