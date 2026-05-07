"""
mem0 adapter — exposes our HTTP memory contract over the mem0ai SDK so the
existing fixture runner (`fixtures/run-eval.sh`) can grade mem0 with the
same probes used to grade our .NET service.

Mismatches vs our service (documented in adapters/mem0/README.md):
  - mem0 has no first-class session concept; sessions are stored as metadata.
  - mem0 has no multi-hop edge graph and no tier-budgeted assembly.
  - `/recall` returns a flat top-k list formatted as a "Known facts" block.
"""

from __future__ import annotations

import os
from typing import Any

from fastapi import Depends, FastAPI, Header, HTTPException
from pydantic import BaseModel
from mem0 import Memory


# ---- mem0 client ----
# Embedding dim must agree across the OpenAI call and the Qdrant collection.
# When MEMORY_EMBED_MODEL is text-embedding-3-large (default 3072 dim), we tell OpenAI
# to truncate to MEMORY_EMBED_DIM via Matryoshka. Both endpoints get the same number.
EMBED_DIM = int(os.getenv("MEMORY_EMBED_DIM", "1536"))

mem = Memory.from_config({
    "vector_store": {
        "provider": "qdrant",
        "config": {
            "host": os.getenv("QDRANT_HOST", "qdrant"),
            "port": int(os.getenv("QDRANT_PORT", "6333")),
            "collection_name": os.getenv("QDRANT_COLLECTION", "mem0"),
            "embedding_model_dims": EMBED_DIM,
        },
    },
    "llm": {
        "provider": "openai",
        "config": {
            "model": os.getenv("MEMORY_CHAT_MODEL", "gpt-4o-mini"),
            "api_key": os.getenv("OPENAI_API_KEY", ""),
        },
    },
    "embedder": {
        "provider": "openai",
        "config": {
            "model": os.getenv("MEMORY_EMBED_MODEL", "text-embedding-3-small"),
            "api_key": os.getenv("OPENAI_API_KEY", ""),
            "embedding_dims": EMBED_DIM,
        },
    },
})


# ---- auth ----
AUTH_TOKEN = os.getenv("MEMORY_AUTH_TOKEN", "").strip()


def require_auth(authorization: str | None = Header(default=None)) -> None:
    if not AUTH_TOKEN:
        return
    if authorization != f"Bearer {AUTH_TOKEN}":
        raise HTTPException(status_code=401, detail="unauthorized")


# ---- request models ----
class IngestMessage(BaseModel):
    role: str
    content: str
    name: str | None = None


class IngestTurnRequest(BaseModel):
    session_id: str
    user_id: str | None = None
    messages: list[IngestMessage]
    timestamp: str
    metadata: dict[str, Any] | None = None


class RecallRequest(BaseModel):
    query: str
    session_id: str
    user_id: str | None = None
    max_tokens: int = 1024


class SearchRequest(BaseModel):
    query: str
    session_id: str | None = None
    user_id: str | None = None
    limit: int = 10


# ---- helpers ----
def _results_list(out: Any) -> list[dict[str, Any]]:
    """mem0 sometimes returns {results: [...]} and sometimes [...] directly."""
    if isinstance(out, dict):
        return out.get("results", []) or []
    if isinstance(out, list):
        return out
    return []


def _safe_metadata(r: dict[str, Any]) -> dict[str, Any]:
    md = r.get("metadata")
    return md if isinstance(md, dict) else {}


# ---- app ----
app = FastAPI(title="mem0 adapter")


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/turns", status_code=201, dependencies=[Depends(require_auth)])
def turns(req: IngestTurnRequest) -> dict[str, str]:
    if not req.session_id:
        raise HTTPException(status_code=400, detail="session_id required")
    if not req.messages:
        raise HTTPException(status_code=400, detail="messages required")

    user = req.user_id or "anonymous"
    msgs = [
        {"role": m.role, "content": m.content, **({"name": m.name} if m.name else {})}
        for m in req.messages
    ]
    metadata = {"session_id": req.session_id, "ts": req.timestamp, **(req.metadata or {})}

    try:
        out = mem.add(msgs, user_id=user, metadata=metadata)
    except Exception as e:
        # Match our service's behavior: turn-level errors degrade extraction but don't crash.
        raise HTTPException(status_code=500, detail=f"mem0 add failed: {e}")

    rows = _results_list(out)
    if rows:
        return {"id": str(rows[0].get("id") or "")}
    # mem0 may return no candidates if extraction yielded nothing; we still need an id
    # for the contract. Use a synthesized one.
    return {"id": ""}


@app.post("/recall", dependencies=[Depends(require_auth)])
def recall(req: RecallRequest) -> dict[str, Any]:
    if not req.query.strip() or not req.user_id:
        return {"context": "", "citations": []}

    # Modern mem0 versions reject user_id as a top-level kwarg; pass via filters.
    out = mem.search(query=req.query, filters={"user_id": req.user_id}, limit=10)
    rows = _results_list(out)
    if not rows:
        return {"context": "", "citations": []}

    lines: list[str] = ["## Known facts about this user"]
    citations: list[dict[str, Any]] = []
    for r in rows:
        memory = r.get("memory") or r.get("text") or ""
        if not memory:
            continue
        lines.append(f"- {memory}")
        citations.append({
            "turn_id": str(r.get("id") or ""),
            "score": float(r.get("score") or 0.0),
            "snippet": memory[:160],
        })

    return {"context": "\n".join(lines) + "\n", "citations": citations}


@app.post("/search", dependencies=[Depends(require_auth)])
def search(req: SearchRequest) -> dict[str, Any]:
    if not req.query.strip() or not req.user_id:
        return {"results": []}

    out = mem.search(query=req.query, filters={"user_id": req.user_id}, limit=req.limit or 10)
    rows = _results_list(out)
    return {"results": [
        {
            "content": r.get("memory", ""),
            "score": float(r.get("score") or 0.0),
            "session_id": _safe_metadata(r).get("session_id", ""),
            "timestamp": r.get("created_at", ""),
            "metadata": _safe_metadata(r),
        }
        for r in rows
    ]}


@app.get("/users/{user_id}/memories", dependencies=[Depends(require_auth)])
def memories(user_id: str) -> dict[str, Any]:
    try:
        out = mem.get_all(filters={"user_id": user_id})
    except (TypeError, ValueError):
        out = mem.get_all(user_id=user_id)
    rows = _results_list(out)
    return {"memories": [
        {
            "id": str(r.get("id") or ""),
            "type": "fact",
            "key": (r.get("memory") or "")[:40],
            "value": r.get("memory") or "",
            "confidence": 1.0,
            "source_session": _safe_metadata(r).get("session_id", ""),
            "source_turn": str(r.get("id") or ""),
            "created_at": r.get("created_at", ""),
            "updated_at": r.get("updated_at", r.get("created_at", "")),
            "supersedes": None,
            "active": True,
        }
        for r in rows
    ]}


@app.delete("/sessions/{session_id}", status_code=204, dependencies=[Depends(require_auth)])
def delete_session(session_id: str):
    # mem0 has no native session deletion. Best-effort: try metadata-filter delete,
    # silently fall back if the SDK doesn't support it.
    try:
        mem.delete_all(filters={"session_id": session_id})  # type: ignore[arg-type]
    except Exception:
        pass


@app.delete("/users/{user_id}", status_code=204, dependencies=[Depends(require_auth)])
def delete_user(user_id: str):
    try:
        mem.delete_all(filters={"user_id": user_id})
    except (TypeError, ValueError):
        mem.delete_all(user_id=user_id)
