# Product search (SigLIP + ZVec + pgvector)

**Requires ZVec.NET 1.0.0-beta.6** (NuGet; `+zvec.0.7.0`).

Google Search + Lens over a **10,000-SKU curated fashion catalog** (Myntra-style product images + descriptions). Same SigLIP embedding, three stores:

| Store | Role |
|-------|------|
| **PostgreSQL + pgvector** | System of record — full product row + `text_embedding` / `image_embedding` HNSW |
| **ZVec text collection** | HNSW + invert filters + FTS on concatenated metadata |
| **ZVec image collection** | Lean image HNSW only |

Deterministic **UUID v5** (`myntra:{catalog_id}`) ties all three together. Ingest is patch-based (100 / 500 / 1000) with a **saga**: ZVec upsert → SQL transaction → compensate ZVec if SQL fails.

## Why this demo

Other examples in this repo show **one HNSW field**. This one is the production ZVec.NET feature tour: dual collections, invert + FTS + hybrid, RRF/Weighted fusion, `Optimize()` + reopen, `DeleteAsync` rollback, and an on-screen **ZVec | PostgreSQL | Both** bake-off on the same query vector.

## Stack

- .NET 10, Aspire AppHost, MudBlazor 9.7
- Local **SigLIP ONNX** (default `siglip-base-patch16-224`, 768-d; optional `siglip2-so400m-patch14-384`, 1152-d)
- EF Core + Npgsql + Pgvector
- **No LM Studio**

## Dataset

The demo ships an in-repo pack: [`data/fashion-10k.zip`](data/fashion-10k.zip) (~77 MB, max-compressed). It contains **10,000** products sampled from the Param Aggarwal fashion catalog (image + display name + category + product description).

| Original source | Link |
|-----------------|------|
| Kaggle | [paramaggarwal/fashion-product-images-small](https://www.kaggle.com/datasets/paramaggarwal/fashion-product-images-small) |
| Hugging Face | [ashraq/fashion-product-images-small](https://huggingface.co/datasets/ashraq/fashion-product-images-small) |

**First ingest** lazily extracts from the pack (no network):

1. `data.csv` → `data/cache/fashion-small/data.csv` (once)
2. Each `images/{id}.jpg` → cache **only when that SKU is in the current patch**

Re-curate the pack from a full Kaggle archive with:

```bash
dotnet run --project tools/CurateFashion10k -- "path/to/archive.zip" "data/fashion-10k.zip"
```

Attribution: Param Aggarwal, Myntra catalog scrape.

## Run

Start **ProductSearch.AppHost** — any normal launcher is fine:

- **Visual Studio** — F5 or Ctrl+F5 on AppHost
- **VS Code / Cursor** — debug or run AppHost
- **CMD / terminal** — `dotnet run --project src/ProductSearch.AppHost` (or `aspire run` if the Aspire CLI is installed)

```bash
cd examples/03-product-search
dotnet run --project src/ProductSearch.AppHost
```

Opens **pgvector/pgvector:pg16** Postgres, API, and Blazor UI. UI reads `ProductSearchUi__ApiBaseUrl` from Aspire.

**Docker Desktop** must be running for Postgres (resume from Resource Saver if the tray icon is a moon).

After a successful start, if Status shows a model/dim stamp mismatch (e.g. index built with SigLIP2 1152-d while Base 768-d is active), **Reset indexes** then ingest.

### Persistent Postgres

Aspire provisions Postgres with a **named Docker volume** so catalog rows survive AppHost restarts:

| Setting | Value |
|---------|--------|
| Host | `localhost` |
| Port | `5432` |
| Database | `productsearch` |
| Username | `postgres` |
| Password | `postgres` |
| Docker volume | `productsearch-pgdata` |

The **Status** page shows these credentials parsed from the active connection string.

**One-time cleanup:** if an older Aspire Postgres container was created with a random password, remove the old container (and anonymous volume if needed) before the first run after this change:

```bash
docker rm -f <old-postgres-container>
docker volume rm productsearch-pgdata   # only if you want a fresh DB
```

Then restart AppHost.

## Run (API + UI standalone)

1. Start Postgres with pgvector and create database `productsearch`.
2. Update `src/ProductSearch.Api/appsettings.json` connection string if needed.
3. ```bash
   dotnet run --project src/ProductSearch.Api
   dotnet run --project src/ProductSearch.UI
   ```
4. Set `ProductSearchUi__ApiBaseUrl` in UI `appsettings.json` (default `http://localhost:5110`).

## Live demo script

1. **Status** → confirm SigLIP model loaded; note empty counts until ingest.
2. **Ingest** → patch **100** (repeat 500/1000 before a talk for so400m wow).
3. Wait for **Demo ready** — SQL count = ZVec text = ZVec image.
4. **Search** → try wow chips:
   - **Exact name** / **Paraphrase** — metadata text hits
   - **Colour filter** / **Season + gender** — invert-filtered ANN
   - **Beach vacation** / **Rainy commute** — implicit visual (words absent from title)
5. Toggle **Engine: Both** → compare latency, overlap @N, rank disagreements (dense-only fair contest).
6. **Lens** (camera icon) → image query; SigLIP shared space searches image index.
7. Enable **Hybrid FTS** + **RRF/Weighted** under ZVec mode only.
8. **More like this** (API) uses stored image vector — no re-encode.

## Scores & Semantics

ZVec Cosine metric exposes **distance** (lower = better). Relation: `cosθ ≈ 1 − distance` for unit vectors.
- **SigLIP vs CLIP Loss Geometry:** CLIP uses softmax with low temperature, producing positive cosine similarities in `[0.25..0.45]`. SigLIP uses pairwise sigmoid loss without in-batch softmax competition, producing unscaled positive cosines in `[0.05..0.18]` (negatives in `[-0.05..0.02]`).
- **Confidence Thresholds:** Calibrated to `MinCosine = 0.03f` and `MaxCosineGapFromTop = 0.35f` to prevent false rejections of legitimate fashion matches.
- **User Similarity %:** Calibrated via piecewise mapping (`SigLipScoreSemantics.SimilarityPercent`) so that cross-modal matches (`0.03..0.20`) display in `[50%..95%]`, and image-to-image matches (`0.30..1.00`) display in `[50%..100%]`.

## Architecture & Multimodal Retrieval Mechanics

1. **Cross-Modal Dense Vector Routing:**
   - Contrastive dual-encoders (CLIP and SigLIP) project text and images into a shared space trained on `(Image, Text)` pairs.
   - Text queries inhabit the visual feature space. Searching text embeddings (`zvec-text`) with text queries causes **anisotropic cone collapse** where unrelated items yield high similarity.
   - Therefore, dense ANN text queries search **`zvec-image`** (product photos).
2. **Lexical FTS Keyword Search:**
   - Keyword queries search **`zvec-text`** on `ConcatenatedText` (title, category, colour, description) using `ZVecFtsDefaultOperator.Or` with BM25 ranking.
3. **Hybrid RRF Fusion:**
   - RRF (`1 / (60 + rank)`) fuses cross-modal visual hits (`zvec-image`) with lexical metadata hits (`zvec-text`). Items that both look like the query AND match keywords rank highest.
   - Confirmed FTS keyword matches are never discarded by vector cosine threshold filters.

## Integration Tests (xUnit v3)

Isolated ZVec retrieval quality tests (cross-modal dense search, FTS, and hybrid fusion) run without Postgres or UI dependencies:

```bash
dotnet run --project tests/ProductSearch.Tests/ProductSearch.Tests.csproj
```

Automatically skips gracefully if SigLIP ONNX models or dataset pack are not present on disk.

## Reset paths

| Action | Effect |
|--------|--------|
| **Reset indexes** | Wipes both ZVec folders + stamp; recreates FTS/invert indexes; SQL rows remain |
| **Reset catalog** | Deletes SQL/pgvector rows **and** clears ZVec + ingest stamp |
| **Optimize** | Merges flat buffer → HNSW on both collections |

If SQL is empty but ZVec still has docs (e.g. after an old ephemeral Postgres), **Start patch** auto-rewinds the ingest stamp and rebuilds both stores from offset 0.

Model/dim mismatch → search blocked; **Reset indexes → Ingest** (same pattern as CLIP gallery).

## Project layout

```
examples/03-product-search/
  data/wow-queries.json
  data/cache/fashion-small/   # gitignored
  models/                     # gitignored SigLIP ONNX
  data/zvec-text/{modelId}/   # gitignored
  data/zvec-image/{modelId}/  # gitignored
  src/
    ProductSearch.AppHost
    ProductSearch.Api
    ProductSearch.Core
    ProductSearch.Shared
    ProductSearch.UI
    ProductSearch.ServiceDefaults
  tests/
    ProductSearch.Tests       # xUnit v3 integration suite
```
