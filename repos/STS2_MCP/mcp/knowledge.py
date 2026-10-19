# knowledge.py
"""Retrieval for the STS2 agent. Lives next to agent_loop.py, together with
docs.index and chunks.pkl (both from the same build_index.py run).

search_knowledge(items, topics):
  items  -> exact stats lookups by card/relic/potion name or ID
  topics -> semantic search over strategy/markdown chunks only
"""
import os

# The embedding model is already cached locally; skip the Hub check (and the
# "unauthenticated requests" warning). Remove this line (or run once with
# HF_HUB_OFFLINE=0 set in your shell) if you ever switch embedding models.
os.environ.setdefault("HF_HUB_OFFLINE", "1")

import pickle
import re
import threading
from pathlib import Path

import faiss
import numpy as np

_DIR = Path(__file__).resolve().parent
EMBED_MODEL = "Qwen/Qwen3-Embedding-0.6B"  # must match the model that built docs.index

_lock = threading.Lock()
_state = {}  # lazy-loaded


def _load():
    if _state:
        return
    from sentence_transformers import SentenceTransformer

    chunks = pickle.load(open(_DIR / "chunks.pkl", "rb"))
    index = faiss.read_index(str(_DIR / "docs.index"))
    if index.ntotal != len(chunks):
        raise RuntimeError(
            f"docs.index has {index.ntotal} vectors but chunks.pkl has {len(chunks)} chunks: "
            f"they come from different builds. Rebuild both together."
        )

    # Exact-name lookup for stats records only. Strategy/markdown chunks start
    # with "[" and are reached through topic search instead.
    name_map = {}
    for i, c in enumerate(chunks):
        if c.startswith("["):
            continue
        name_map.setdefault(c.split(" (", 1)[0].strip().lower(), []).append(i)
        m = re.match(r".*? \(([^,]+), from", c)  # also index by KEY, e.g. ABRASIVE_PLUS
        if m:
            name_map.setdefault(m.group(1).strip().lower(), []).append(i)

    # Separate index holding only strategy chunks, so a topic search can never
    # be crowded out by the ~1,300 stats records.
    doc_ids = [i for i, c in enumerate(chunks) if c.startswith("[")]
    doc_index = None
    if doc_ids:
        vecs = np.vstack([index.reconstruct(i) for i in doc_ids]).astype("float32")
        doc_index = faiss.IndexFlatIP(vecs.shape[1])
        doc_index.add(vecs)

    _state.update(
        embedder=SentenceTransformer(EMBED_MODEL, device="cpu"),
        index=index,
        chunks=chunks,
        name_map=name_map,
        doc_index=doc_index,
        doc_ids=doc_ids,
    )


def preload():
    """Load everything now (call once at startup)."""
    with _lock:
        _load()
        print(f"[knowledge] loaded {len(_state['chunks'])} chunks "
              f"({len(_state['doc_ids'])} strategy chunks)")


def search_knowledge(items=None, topics=None, k_doc=3) -> str:
    with _lock:
        _load()
        chunks, name_map = _state["chunks"], _state["name_map"]
        seen, out = set(), []

        def add(i):
            if i not in seen:
                seen.add(i)
                out.append(chunks[i])

        # Stats lookups: exact name/ID match first, nearest stats record as fallback.
        for q in (items or [])[:8]:
            key = q.strip().lower()
            hits = list(name_map.get(key, [])) or list(name_map.get(key.rstrip("+"), []))
            if not hits:
                emb = _state["embedder"].encode([q], normalize_embeddings=True)
                _, idx = _state["index"].search(emb, 2)
                hits = [int(i) for i in idx[0] if i >= 0]
            for i in hits:
                add(i)

        # Strategy lookups: strategy chunks only.
        if _state["doc_index"] is not None:
            for t in (topics or [])[:4]:
                emb = _state["embedder"].encode([t], normalize_embeddings=True)
                _, idx = _state["doc_index"].search(emb, k_doc)
                for j in idx[0]:
                    if j >= 0:
                        add(_state["doc_ids"][int(j)])

    return "\n\n".join(out) or "No matching entries found."


SEARCH_TOOL = {
    "type": "function",
    "function": {
        "name": "search_knowledge",
        "description": (
            "Look up Slay the Spire 2 knowledge. 'items': exact names of cards/relics/potions "
            "you're considering; returns community stats (tier, win rate, pick rate). "
            "'topics': short phrases like 'Ironclad exhaust build' or 'Silent poison synergies'; "
            "returns strategy notes on character archetypes, build priorities and general strategy. "
            "Use BOTH when picking cards or relics: stats alone ignore your deck's plan. "
            "This does not take a game action; you must still call a game tool afterwards."
        ),
        "parameters": {
            "type": "object",
            "properties": {
                "items": {"type": "array", "items": {"type": "string"}},
                "topics": {"type": "array", "items": {"type": "string"}},
            },
        },
    },
}