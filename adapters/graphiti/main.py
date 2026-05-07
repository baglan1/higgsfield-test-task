"""
Graphiti adapter — exposes our HTTP memory contract over getzep/graphiti.

Graphiti's mental model:
  - episodes are atomic units of input (a conversation turn = one episode)
  - the LLM extracts entities + relationships from each episode at ingest time
  - relationships ("edges") have valid_at / invalid_at timestamps for bi-temporal recall
  - search returns EntityEdge objects whose .fact field is a natural-language
    statement of the relationship

We map per-user_id to a graphiti `group_id` for scoping. Sessions and timestamps
are passed through to Graphiti so it can timestamp facts.
"""

from __future__ import annotations

import os
import uuid
from datetime import datetime
from typing import Any

# Set OPENAI_API_KEY before importing graphiti so its LLM client picks it up.
os.environ.setdefault("OPENAI_API_KEY", os.getenv("OPENAI_API_KEY", ""))

from fastapi import Depends, FastAPI, Header, HTTPException
from pydantic import BaseModel

from graphiti_core import Graphiti
from graphiti_core.nodes import EpisodeType
from graphiti_core.driver.falkordb_driver import FalkorDriver


# ---- Graphiti client ----
graphiti: Graphiti | None = None


async def get_graphiti() -> Graphiti:
    """Lazy-init: Graphiti's constructor connects to the DB, so we defer until first call."""
    global graphiti
    if graphiti is None:
        driver = FalkorDriver(
            host=os.getenv("FALKORDB_HOST", "falkordb"),
            port=int(os.getenv("FALKORDB_PORT", "6379")),
            username=os.getenv("FALKORDB_USERNAME") or None,
            password=os.getenv("FALKORDB_PASSWORD") or None,
        )
        g = Graphiti(graph_driver=driver)
        await g.build_indices_and_constraints()
        graphiti = g
    return graphiti


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
def _group_id(user_id: str) -> str:
    """FalkorDB's RediSearch treats hyphens (and other punctuation) as separators.
    Sanitize to alphanumerics + underscores so group_id-scoped queries don't break."""
    return "u_" + "".join(c if c.isalnum() else "_" for c in user_id)


def _format_episode(messages: list[IngestMessage]) -> str:
    return "\n".join(
        f"{m.name or m.role}: {m.content}" for m in messages
    )


def _parse_ts(s: str) -> datetime:
    # Accept ISO 8601 with or without trailing Z.
    if s.endswith("Z"):
        s = s[:-1] + "+00:00"
    try:
        return datetime.fromisoformat(s)
    except Exception:
        return datetime.utcnow()


# ---- app ----
app = FastAPI(title="graphiti adapter")


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/turns", status_code=201, dependencies=[Depends(require_auth)])
async def turns(req: IngestTurnRequest) -> dict[str, str]:
    if not req.session_id:
        raise HTTPException(status_code=400, detail="session_id required")
    if not req.messages:
        raise HTTPException(status_code=400, detail="messages required")

    user = _group_id(req.user_id or "anonymous")
    g = await get_graphiti()

    episode_body = _format_episode(req.messages)
    name = f"turn-{uuid.uuid4()}"
    try:
        result = await g.add_episode(
            name=name,
            episode_body=episode_body,
            source=EpisodeType.message,
            source_description=f"session {req.session_id}",
            reference_time=_parse_ts(req.timestamp),
            group_id=user,
        )
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"graphiti add_episode failed: {e}")

    # Graphiti's AddEpisodeResults has .episode (the EpisodicNode); use its uuid as our turn id.
    episode_id = ""
    if result is not None:
        episode = getattr(result, "episode", None)
        if episode is not None:
            episode_id = str(getattr(episode, "uuid", "") or "")
    return {"id": episode_id or str(uuid.uuid4())}


@app.post("/recall", dependencies=[Depends(require_auth)])
async def recall(req: RecallRequest) -> dict[str, Any]:
    if not req.query.strip() or not req.user_id:
        return {"context": "", "citations": []}

    g = await get_graphiti()
    gid = _group_id(req.user_id)
    try:
        edges = await g.search(query=req.query, group_ids=[gid], num_results=10)
    except Exception:
        return {"context": "", "citations": []}

    if not edges:
        return {"context": "", "citations": []}

    lines = ["## Known facts about this user"]
    citations: list[dict[str, Any]] = []
    for e in edges:
        # graphiti EntityEdge has .fact (str), .uuid, .valid_at (datetime|None),
        # .invalid_at (datetime|None). Surface only currently-valid edges.
        if getattr(e, "invalid_at", None) is not None:
            continue
        fact = getattr(e, "fact", None) or ""
        if not fact:
            continue
        valid_at = getattr(e, "valid_at", None)
        prefix = f"[{valid_at.date().isoformat()}] " if valid_at else ""
        lines.append(f"- {prefix}{fact}")
        citations.append({
            "turn_id": str(getattr(e, "episodes", [None])[0] or "") or str(getattr(e, "uuid", "")),
            "score": 0.0,
            "snippet": fact[:160],
        })

    if len(lines) == 1:  # nothing currently valid
        return {"context": "", "citations": []}
    return {"context": "\n".join(lines) + "\n", "citations": citations}


@app.post("/search", dependencies=[Depends(require_auth)])
async def search(req: SearchRequest) -> dict[str, Any]:
    if not req.query.strip() or not req.user_id:
        return {"results": []}

    g = await get_graphiti()
    gid = _group_id(req.user_id)
    try:
        edges = await g.search(query=req.query, group_ids=[gid], num_results=req.limit or 10)
    except Exception:
        return {"results": []}

    items: list[dict[str, Any]] = []
    for e in edges:
        fact = getattr(e, "fact", None) or ""
        if not fact:
            continue
        valid_at = getattr(e, "valid_at", None)
        items.append({
            "content": fact,
            "score": 0.0,
            "session_id": "",
            "timestamp": valid_at.isoformat() if valid_at else "",
            "metadata": {},
        })
    return {"results": items}


@app.get("/users/{user_id}/memories", dependencies=[Depends(require_auth)])
async def memories(user_id: str) -> dict[str, Any]:
    # Graphiti doesn't have a "list all edges for a group_id" first-class API; the
    # closest thing is a broad search. Return empty for this contract endpoint.
    return {"memories": []}


@app.delete("/sessions/{session_id}", status_code=204, dependencies=[Depends(require_auth)])
def delete_session(session_id: str):
    # Graphiti doesn't separately track sessions; no-op.
    return


@app.delete("/users/{user_id}", status_code=204, dependencies=[Depends(require_auth)])
async def delete_user(user_id: str):
    g = await get_graphiti()
    gid = _group_id(user_id)
    try:
        # Wipe all nodes + edges for this group_id by raw Cypher.
        await g.driver.execute_query(
            "MATCH (n {group_id: $gid}) DETACH DELETE n",
            gid=gid,
        )
    except Exception:
        pass
