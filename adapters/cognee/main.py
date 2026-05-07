"""
Cognee adapter — exposes our HTTP memory contract over the cognee SDK.

Cognee's mental model differs from ours:
  - cognee.add(text, dataset_name) just stages raw text
  - cognee.cognify(datasets=[...]) processes staged text into a KG
  - cognee.search(query_type, query_text, datasets=[...]) queries

We map per-user_id to a cognee dataset. /turns ingests text + cognifies
synchronously so a subsequent /recall sees the new data, matching our
service's "no eventual consistency" contract.
"""

from __future__ import annotations

import os

# ---- Cognee global config — MUST run before `import cognee` so the SDK reads it correctly ----
os.environ["LLM_API_KEY"]           = os.getenv("OPENAI_API_KEY", "")
os.environ["LLM_MODEL"]             = os.getenv("MEMORY_CHAT_MODEL", "gpt-4o-mini")
os.environ["LLM_PROVIDER"]          = "openai"
os.environ["EMBEDDING_API_KEY"]     = os.getenv("OPENAI_API_KEY", "")
os.environ["EMBEDDING_MODEL"]       = os.getenv("MEMORY_EMBED_MODEL", "text-embedding-3-small")
os.environ["EMBEDDING_PROVIDER"]    = "openai"
os.environ["EMBEDDING_DIMENSIONS"]  = os.getenv("MEMORY_EMBED_DIM", "1536")

import asyncio
import uuid
from typing import Any

from fastapi import Depends, FastAPI, Header, HTTPException
from pydantic import BaseModel

import cognee
from cognee.api.v1.search import SearchType


# ---- auth ----
AUTH_TOKEN = os.getenv("MEMORY_AUTH_TOKEN", "").strip()


def require_auth(authorization: str | None = Header(default=None)) -> None:
    if not AUTH_TOKEN:
        return
    if authorization != f"Bearer {AUTH_TOKEN}":
        raise HTTPException(status_code=401, detail="unauthorized")


# ---- request models (same as our service's contract) ----
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
def _dataset(user_id: str) -> str:
    """One cognee dataset per user. cognee allows alphanumerics + underscores."""
    return "user_" + "".join(c if c.isalnum() else "_" for c in user_id)


def _format_messages(messages: list[IngestMessage]) -> str:
    """Flatten a turn into a single text blob cognee can ingest."""
    return "\n".join(f"[{m.role}{(' ' + m.name) if m.name else ''}] {m.content}" for m in messages)


# ---- app ----
app = FastAPI(title="cognee adapter")


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/turns", status_code=201, dependencies=[Depends(require_auth)])
async def turns(req: IngestTurnRequest) -> dict[str, str]:
    if not req.session_id:
        raise HTTPException(status_code=400, detail="session_id required")
    if not req.messages:
        raise HTTPException(status_code=400, detail="messages required")

    user = req.user_id or "anonymous"
    dataset = _dataset(user)
    text = _format_messages(req.messages)
    if req.timestamp:
        text = f"[turn at {req.timestamp}, session={req.session_id}]\n{text}"

    try:
        await cognee.add(text, dataset_name=dataset)
        # Synchronously process so a subsequent /recall sees this turn.
        await cognee.cognify(datasets=[dataset])
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"cognee add/cognify failed: {e}")

    return {"id": str(uuid.uuid4())}  # cognee doesn't return a stable ingest id


@app.post("/recall", dependencies=[Depends(require_auth)])
async def recall(req: RecallRequest) -> dict[str, Any]:
    if not req.query.strip() or not req.user_id:
        return {"context": "", "citations": []}

    dataset = _dataset(req.user_id)

    try:
        results = await cognee.search(
            query_type=SearchType.GRAPH_COMPLETION,
            query_text=req.query,
            datasets=[dataset],
        )
    except Exception:
        # Cold dataset / no cognified data yet
        return {"context": "", "citations": []}

    # cognee returns shapes that vary by version. As of cognee 1.x:
    #   GRAPH_COMPLETION → list[dict] with key "search_result": list[str]
    # Older versions returned bare strings or lists of strings. Normalize all of them.
    chunks: list[str] = []
    if isinstance(results, str):
        chunks.append(results)
    elif isinstance(results, list):
        for r in results:
            if isinstance(r, str):
                chunks.append(r)
            elif isinstance(r, dict) and "search_result" in r:
                inner = r["search_result"]
                if isinstance(inner, list):
                    chunks.extend(str(x) for x in inner if x)
                elif inner:
                    chunks.append(str(inner))
            elif r:
                chunks.append(str(r))
    text = "\n".join(c.strip() for c in chunks if c and c.strip())

    if not text or text.lower().startswith("i don't know") or "no relevant information" in text.lower():
        return {"context": "", "citations": []}

    context = "## Known facts about this user\n" + text + "\n"
    return {"context": context, "citations": []}


@app.post("/search", dependencies=[Depends(require_auth)])
async def search(req: SearchRequest) -> dict[str, Any]:
    if not req.query.strip() or not req.user_id:
        return {"results": []}

    dataset = _dataset(req.user_id)
    try:
        results = await cognee.search(
            query_type=SearchType.INSIGHTS,
            query_text=req.query,
            datasets=[dataset],
        )
    except Exception:
        return {"results": []}

    items: list[dict[str, Any]] = []
    if isinstance(results, list):
        for r in results[: req.limit or 10]:
            content = r if isinstance(r, str) else getattr(r, "content", str(r))
            items.append({
                "content": content,
                "score": 0.0,
                "session_id": "",
                "timestamp": "",
                "metadata": {},
            })
    return {"results": items}


@app.get("/users/{user_id}/memories", dependencies=[Depends(require_auth)])
async def memories(user_id: str) -> dict[str, Any]:
    # cognee doesn't expose a "list all extracted facts" API. Best-effort: return empty.
    # The KG is queryable via /search; structured per-fact listing is out of scope here.
    return {"memories": []}


@app.delete("/sessions/{session_id}", status_code=204, dependencies=[Depends(require_auth)])
def delete_session(session_id: str):
    # cognee has no native session concept and no metadata-based deletion. No-op.
    return


@app.delete("/users/{user_id}", status_code=204, dependencies=[Depends(require_auth)])
async def delete_user(user_id: str):
    dataset = _dataset(user_id)
    try:
        await cognee.prune.prune_data(dataset_name=dataset)
        await cognee.prune.prune_system(metadata=True, graph=True, vector=True)
    except Exception:
        # If a finer-grained delete isn't available in this cognee version, skip.
        pass
