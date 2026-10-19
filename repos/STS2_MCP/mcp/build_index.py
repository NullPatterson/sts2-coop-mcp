# build_index.py  (run on your PC, in the same folder as agent_loop.py)
import json, os, pickle, re
from pathlib import Path
import faiss
from sentence_transformers import SentenceTransformer

DOCS_DIR = Path("./sts2-knowledge")   # local copy

def record_to_text(key, record, source_file):
    name = record.get("name", key)
    fields = ", ".join(f"{k} {v}" for k, v in record.items() if k != "name" and v is not None)
    return f"{name} ({key}, from {source_file}): {fields}"

def chunk_markdown(text, rel_path, max_chars=1500):
    """One chunk per ##/### section, prefixed with file path and headings.
    Text before the first ## (the intro under the # title) is kept too."""
    m = re.match(r"\s*# (.+)", text)
    title = m.group(1).strip() if m else Path(rel_path).stem
    parts = re.split(r"(?m)^(?=#{2,3} )", text)
    chunks = []
    for part in parts:
        part = part.strip()
        if not part:
            continue
        first = part.splitlines()[0]
        heading = first.lstrip("# ").strip() if first.startswith("##") else "Overview"
        # drop a bare "# Title" line from the intro so it isn't duplicated in the body
        body = re.sub(r"^# .+\n?", "", part).strip() if not first.startswith("##") else part
        if not body:
            continue
        prefix = f"[{rel_path} | {title} / {heading}] "
        while len(body) > max_chars:
            cut = body.rfind("\n\n", 0, max_chars)
            cut = cut if cut > 200 else max_chars
            chunks.append(prefix + body[:cut].strip())
            body = body[cut:].strip()
        if body:
            chunks.append(prefix + body)
    return chunks

all_chunks = []

for path in sorted(DOCS_DIR.rglob("*.json")):
    data = json.load(open(path, encoding="utf-8"))
    n = 0
    for key, record in data.items():
        if isinstance(record, dict):
            all_chunks.append(record_to_text(key, record, path.name))
            n += 1
    print(f"{path.relative_to(DOCS_DIR)}: {n} records")

for path in sorted(DOCS_DIR.rglob("*.md")):
    rel = path.relative_to(DOCS_DIR).as_posix()
    chunks = chunk_markdown(path.read_text(encoding="utf-8"), rel)
    all_chunks.extend(chunks)
    print(f"{rel}: {len(chunks)} chunks")

print(f"Total: {len(all_chunks)} chunks. Embedding...")
embedder = SentenceTransformer("Qwen/Qwen3-Embedding-0.6B", device="cpu")
emb = embedder.encode(all_chunks, normalize_embeddings=True, show_progress_bar=True, batch_size=16)
print("Embeddings shape:", emb.shape)

index = faiss.IndexFlatIP(emb.shape[1])
index.add(emb)
faiss.write_index(index, "docs.index")
pickle.dump(all_chunks, open("chunks.pkl", "wb"))
print("Saved docs.index and chunks.pkl")