#!/usr/bin/env python3
"""
Emit bash `export VAR=...` lines from ERSimulatorApp appsettings so uvicorn inherits
the same Knowledge Base config as the .NET app (single place to edit).

Usage (from repo root):
  eval "$(python3 rag_backend/sync_kb_env_from_appsettings.py)"

Merges like ASP.NET Core: **appsettings.json** first, then **appsettings.Development.json**
overlays **RAG.KnowledgeBase** (Development wins on duplicate keys). Edit KB settings in
appsettings.json; use Development only for local overrides.

Only exports when Knowledge API mode is active (ContextSource=knowledge_api OR SearchUrl is set).
Does NOT export RAG_CONTEXT_SOURCE=chromadb (so rag_backend/.env can still set knowledge_api).

Existing environment variables are NOT overwritten.
"""
from __future__ import annotations

import json
import os
import shlex
import sys
from typing import Any, Dict


def _load_json(path: str) -> Optional[Dict[str, Any]]:
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, json.JSONDecodeError):
        return None


def _merged_knowledge_base(repo_root: str) -> Dict[str, Any]:
    """RAG.KnowledgeBase from appsettings.json, overlaid by appsettings.Development.json."""
    app_dir = os.path.join(repo_root, "ERSimulatorApp")
    kb: Dict[str, Any] = {}
    for name in ("appsettings.json", "appsettings.Development.json"):
        p = os.path.join(app_dir, name)
        if not os.path.isfile(p):
            continue
        cfg = _load_json(p)
        if not cfg:
            continue
        rag = cfg.get("RAG") or {}
        section = rag.get("KnowledgeBase")
        if isinstance(section, dict) and section:
            kb.update(section)
    return kb


def _emit(name: str, value: Any) -> None:
    if value is None:
        return
    if isinstance(value, bool):
        s = "1" if value else "0"
    elif isinstance(value, (int, float)):
        s = str(value)
    else:
        s = str(value).strip()
    if not s:
        return
    print(f"if [ -z \"${{{name}:-}}\" ]; then export {name}={shlex.quote(s)}; fi")


def main() -> None:
    repo_root = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    kb = _merged_knowledge_base(repo_root)
    if not kb:
        return

    search_url = (kb.get("SearchUrl") or "").strip()
    ctx = (kb.get("ContextSource") or "").strip().lower()
    if ctx not in ("knowledge_api", "chromadb", ""):
        ctx = ""

    # Active KB HTTP retrieval: explicit mode or any non-empty SearchUrl (unless user forced chromadb)
    use_kb = ctx == "knowledge_api" or (bool(search_url) and ctx != "chromadb")
    if not use_kb:
        return

    _emit("RAG_CONTEXT_SOURCE", "knowledge_api")
    _emit("SKIP_PDF_INGEST", "1")
    _emit("KNOWLEDGE_API_SEARCH_URL", search_url or kb.get("SearchUrl"))
    _emit("KNOWLEDGE_API_KEY", kb.get("ApiKey"))
    _emit("KNOWLEDGE_API_METHOD", kb.get("Method"))
    _emit("KNOWLEDGE_API_QUERY_KEY", kb.get("QueryKey"))
    _emit("KNOWLEDGE_API_TOP_K_KEY", kb.get("TopKKey"))
    _emit("KNOWLEDGE_API_AUTH_STYLE", kb.get("AuthStyle"))
    _emit("KNOWLEDGE_API_AUTH_HEADER", kb.get("AuthHeader"))
    _emit("KNOWLEDGE_API_GET_QUERY_PARAM", kb.get("GetQueryParam"))
    _emit("KNOWLEDGE_API_GET_LIMIT_PARAM", kb.get("GetLimitParam"))
    _emit("KNOWLEDGE_API_QUERY_AUTH_PARAM", kb.get("QueryAuthParam"))
    _emit("KNOWLEDGE_API_TIMEOUT", kb.get("TimeoutSeconds"))

    extra = kb.get("ExtraBody")
    if extra is not None and not isinstance(extra, str):
        extra = json.dumps(extra, separators=(",", ":"))
    _emit("KNOWLEDGE_API_EXTRA_BODY", extra)

    headers = kb.get("Headers")
    if isinstance(headers, dict) and headers:
        _emit("KNOWLEDGE_API_HEADERS_JSON", json.dumps(headers, separators=(",", ":")))


if __name__ == "__main__":
    main()
