"""
Vanilla baseline — the dumbest possible "memory service".

What it does, intentionally:
  /turns   → embed the full turn text as one document, store in pgvector
  /recall  → embed the query, cosine top-k, concatenate the raw turn text
             as the context block. No tier-budgeting, no off-topic gate.

What it does NOT do:
  - LLM extraction (no candidates produced from messages)
  - supersession or de-duplication
  - aspect/stance modeling
  - multi-hop edges
  - query rewriting
  - tier-budgeted assembly
  - off-topic similarity floor
  - hybrid (no BM25, no RRF)

Purpose: establish how much of the score on `fixtures/locomo-real` is
already explained by "shove the messages into a vector store and retrieve",
so the smart-memory adapters can be measured against a real baseline.
"""

from __future__ import annotations

import os
import time
import uuid
from typing import Any

import psycopg
from pgvector.psycopg import register_vector
from openai import OpenAI

from fastapi import Depends, FastAPI, Header, HTTPException
from pydantic import BaseModel


# ---- config ----
PG_URI         = os.getenv("PG_URI", "postgresql://baseline:baseline@postgres:5432/baseline")
EMBED_MODEL    = os.getenv("MEMORY_EMBED_MODEL", "text-embedding-3-small")
EMBED_DIM      = int(os.getenv("MEMORY_EMBED_DIM", "1536"))
TOP_K          = int(os.getenv("MEMORY_TOP_K", "10"))
AUTH_TOKEN     = os.getenv("MEMORY_AUTH_TOKEN", "").strip()
OPENAI_API_KEY = os.getenv("OPENAI_API_KEY", "")


openai_client = OpenAI(api_key=OPENAI_API_KEY)


# ---- DB bootstrap (with retry — Postgres takes a moment after compose up) ----
def _connect_with_retry() -> psycopg.Connection:
    deadline = time.time() + 30
    last: Exception | None = None
    while time.time() < deadline:
        try:
            conn = psycopg.connect(PG_URI, autocommit=True)
            # CREATE EXTENSION before register_vector — the latter queries pg_type
            # for the `vector` row, which only exists after the extension is installed.
            with conn.cursor() as cur:
                cur.execute("CREATE EXTENSION IF NOT EXISTS vector")
            register_vector(conn)
            return conn
        except Exception as e:
            last = e
            time.sleep(1)
    raise RuntimeError(f"could not connect to Postgres in 30s: {last}")


conn = _connect_with_retry()

with conn.cursor() as cur:
    cur.execute(
        f"""
        CREATE TABLE IF NOT EXISTS messages (
            id          uuid PRIMARY KEY,
            user_id     text NOT NULL,
            session_id  text NOT NULL,
            content     text NOT NULL,
            embedding   vector({EMBED_DIM}) NOT NULL,
            created_at  timestamptz NOT NULL DEFAULT now(),
            metadata    jsonb NOT NULL DEFAULT '{{}}'::jsonb
        )
        """
    )
    cur.execute(
        "CREATE INDEX IF NOT EXISTS messages_embedding_hnsw "
        "ON messages USING hnsw (embedding vector_cosine_ops)"
    )
    cur.execute("CREATE INDEX IF NOT EXISTS messages_user ON messages (user_id)")


# ---- auth ----
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
def _embed(text: str) -> list[float]:
    resp = openai_client.embeddings.create(
        input=text,
        model=EMBED_MODEL,
        dimensions=EMBED_DIM,  # Matryoshka — works for text-embedding-3-* models
    )
    return resp.data[0].embedding


def _format_turn(messages: list[IngestMessage]) -> str:
    return "\n".join(
        f"{m.name or m.role}: {m.content}" for m in messages
    )


# ---- app ----
app = FastAPI(title="vanilla baseline (no extraction)")


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
    text = _format_turn(req.messages)
    if not text.strip():
        raise HTTPException(status_code=400, detail="messages must contain non-empty content")

    msg_id = str(uuid.uuid4())
    try:
        vec = _embed(text)
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"embedding failed: {e}")

    with conn.cursor() as cur:
        cur.execute(
            "INSERT INTO messages (id, user_id, session_id, content, embedding) "
            "VALUES (%s, %s, %s, %s, %s)",
            (msg_id, user, req.session_id, text, vec),
        )
    return {"id": msg_id}


@app.post("/recall", dependencies=[Depends(require_auth)])
def recall(req: RecallRequest) -> dict[str, Any]:
    if not req.query.strip() or not req.user_id:
        return {"context": "", "citations": []}

    try:
        qvec = _embed(req.query)
    except Exception:
        return {"context": "", "citations": []}

    with conn.cursor() as cur:
        cur.execute(
            "SELECT id, content, 1 - (embedding <=> %s::vector) AS sim "
            "FROM messages WHERE user_id = %s "
            "ORDER BY embedding <=> %s::vector LIMIT %s",
            (qvec, req.user_id, qvec, TOP_K),
        )
        rows = cur.fetchall()

    if not rows:
        return {"context": "", "citations": []}

    lines = ["## Known facts about this user"]
    citations: list[dict[str, Any]] = []
    for row_id, content, sim in rows:
        lines.append(f"- {content}")
        citations.append({
            "turn_id": str(row_id),
            "score": float(sim),
            "snippet": content[:160],
        })
    return {"context": "\n".join(lines) + "\n", "citations": citations}


@app.post("/search", dependencies=[Depends(require_auth)])
def search(req: SearchRequest) -> dict[str, Any]:
    if not req.query.strip() or not req.user_id:
        return {"results": []}

    try:
        qvec = _embed(req.query)
    except Exception:
        return {"results": []}

    limit = req.limit if req.limit and req.limit > 0 else 10
    with conn.cursor() as cur:
        cur.execute(
            "SELECT id, content, session_id, created_at, "
            "1 - (embedding <=> %s::vector) AS sim "
            "FROM messages WHERE user_id = %s "
            "ORDER BY embedding <=> %s::vector LIMIT %s",
            (qvec, req.user_id, qvec, limit),
        )
        rows = cur.fetchall()

    return {"results": [
        {
            "content": content,
            "score": float(sim),
            "session_id": session_id,
            "timestamp": created_at.isoformat() if created_at else "",
            "metadata": {},
        }
        for (_id, content, session_id, created_at, sim) in rows
    ]}


@app.get("/users/{user_id}/memories", dependencies=[Depends(require_auth)])
def memories(user_id: str) -> dict[str, Any]:
    with conn.cursor() as cur:
        cur.execute(
            "SELECT id, session_id, content, created_at "
            "FROM messages WHERE user_id = %s ORDER BY created_at",
            (user_id,),
        )
        rows = cur.fetchall()
    return {"memories": [
        {
            "id": str(row_id),
            "type": "fact",
            "key": (content or "")[:40],
            "value": content,
            "confidence": 1.0,
            "source_session": session_id,
            "source_turn": str(row_id),
            "created_at": created_at.isoformat() if created_at else "",
            "updated_at": created_at.isoformat() if created_at else "",
            "supersedes": None,
            "active": True,
        }
        for (row_id, session_id, content, created_at) in rows
    ]}


@app.delete("/sessions/{session_id}", status_code=204, dependencies=[Depends(require_auth)])
def delete_session(session_id: str):
    with conn.cursor() as cur:
        cur.execute("DELETE FROM messages WHERE session_id = %s", (session_id,))


@app.delete("/users/{user_id}", status_code=204, dependencies=[Depends(require_auth)])
def delete_user(user_id: str):
    with conn.cursor() as cur:
        cur.execute("DELETE FROM messages WHERE user_id = %s", (user_id,))
