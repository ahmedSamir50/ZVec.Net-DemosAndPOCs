# MovieLens recommendations (MAUI Blazor Hybrid)

**Requires ZVec.NET 1.0.0-beta.3.1** (NuGet) · **.NET 10** · Windows + Android

Netflix-style **“Because you watched…”** on device: MovieLens titles are embedded with local **all-MiniLM-L6-v2** (ONNX), stored in an in-process **ZVec.NET** HNSW collection, and queried with a **user behaviour vector** (mean of liked movie embeddings). MudBlazor UI · no controllers · no vector cloud.

Talk track: edge proof for [slide 14](../../docs/ZVec.NET_Team_Session.html) in the team session deck. CRUD/`Optimize` context: [slide 11](../../docs/ZVec.NET_Team_Session.html#slide-11).

## Create vs Open

Uses SDK **`OpenOrCreate`** under `FileSystem.AppDataDirectory/zvec-movies`. Stamp file records model id / 384-d / pipeline version. **Reset index** wipes the folder + stamp.

**mmap is off** for this MAUI demo (`EnableMmap = false`) — more stable Optimize/reopen on Windows Hybrid.

## Encode pipeline (important)

Sentence-transformers MiniLM must use **`sentence_embedding` or mean-pooled `last_hidden_state`** — never BERT **`pooler_output`**. Wrong pooling collapses the space (Inception → kids documentary, Matrix → random comedy at 96%).

Pipeline id: `minilm-meanpool-l2-v3-seq256`. After pulling this change: **Reset index → Ingest** (stamp mismatch). Encoder runs a sanity check at load (Inception nearer Interstellar than a kids documentary).

## Optimize

Upserts stage in a flat buffer; **`Optimize()`** merges into HNSW for production-quality ANN (no re-embed). This demo:

1. Runs Optimize automatically at the **end of a successful Ingest**.
2. Exposes an **Optimize index** button for already-ingested collections.
3. **Reopens** the collection after Optimize so Recommend uses a fresh querier over merged segments.

Same pattern in CLIP (`examples/01-clip-onnx`) and PDDM (`Advanced/`).

## Ranking

ANN alone on title+genre text is weak. Recommend:

1. Over-fetches ZVec neighbors.
2. **Injects franchise mates** from the catalog (Matrix → Reloaded/Revolutions; Die Hard / Jumanji sequels).
3. Reranks with **genre Jaccard** + **franchise stem bonus**.
4. Applies **MinCosine** / gap gates on raw cosine; UI shows **similarity %** (relative cosine, not a calibrated “match”).

## Troubleshooting

**UI freezes on Ingest** — work runs on the ThreadPool; first ingest still takes several minutes with a moving progress bar.

**`InternalError (Query)` / Gandiva `fill_result`** — Reset → Ingest; Optimize reopens after merge.

**Stamp mismatch / joke neighbors** — Reset → Ingest after an encoder pipeline bump.

**Index ready but empty ANN** — Reset → Ingest (corrupt or empty collection).

## Setup

```powershell
cd examples/02-movie-recs
./scripts/download-minilm.ps1
./scripts/download-movielens.ps1

dotnet build src/MovieRecs.Maui/MovieRecs.Maui.csproj -f net10.0-windows10.0.19041.0
```

Run (Windows):

```powershell
dotnet build src/MovieRecs.Maui/MovieRecs.Maui.csproj -t:Run -f net10.0-windows10.0.19041.0
```

### Assets

| Path under `Resources/Raw` | Source |
|----------------------------|--------|
| `movielens/movies.csv`, `ratings.csv` | [MovieLens ml-latest-small](https://grouplens.org/datasets/movielens/) |
| `models/all-MiniLM-L6-v2.onnx`, `vocab.txt` | [sentence-transformers/all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) |

## Live demo script

1. **Reset → Ingest** once after a pipeline bump (progress moves; several minutes on CPU).
2. Seed **The Matrix** alone → expect Reloaded / Revolutions in Top-K.
3. **Inception** → Sci-Fi/Thriller family, not Children’s/Documentary.
4. Note **CPU · Mem** strip — in-process, no vector sidecar.

## Architecture

- **MAUI Blazor Hybrid** + **MudBlazor** — services injected into Blazor (no controllers).
- **MiniLM ONNX** — WordPiece + ST mean-pool + L2 → 384-d.
- **RecommendService** — mean watchlist vector → `QueryAsync` → franchise inject + genre/franchise rerank + gates.
- Targets: `net10.0-windows10.0.19041.0`, `net10.0-android`.

## Scores

ZVec Cosine metric returns **distance**. UI: `similarity% = round(100 * (1 - distance))` on raw cosine before display.
