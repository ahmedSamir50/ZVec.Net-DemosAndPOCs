# Product search (SigLIP + ZVec + pgvector)

**Requires ZVec.NET 1.0.0-beta.4** (NuGet; `+zvec.0.6.0`).

Google Search + Lens over the **Param Aggarwal fashion-product-images-small** catalog (~44k Myntra SKUs). Same SigLIP embedding, three stores:

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
- Local **SigLIP ONNX** (default `siglip-base-patch16-224`, 768-d; optional `siglip-so400m-patch14-384`, 1152-d)
- EF Core + Npgsql + Pgvector
- **No LM Studio**

## Dataset

| Source | Link |
|--------|------|
| Kaggle | [paramaggarwal/fashion-product-images-small](https://www.kaggle.com/datasets/paramaggarwal/fashion-product-images-small) |
| Hugging Face | [ashraq/fashion-product-images-small](https://huggingface.co/datasets/ashraq/fashion-product-images-small) |

First run downloads `styles.csv` from HF into `data/cache/fashion-small/`; images are fetched on demand during ingest (`images/{id}.jpg`). Attribution: Param Aggarwal, Myntra catalog scrape.

**Honest gap:** the small pack has no long descriptions — search text is `productDisplayName` + metadata (colour, season, usage, article type, …). Implicit / visual wow queries rely on SigLIP **image** vectors.

## Run (Aspire — recommended)

```bash
cd examples/03-product-search
dotnet run --project src/ProductSearch.AppHost
```

Opens **pgvector/pgvector:pg16** Postgres, API, and Blazor UI. UI reads `ProductSearchUi__ApiBaseUrl` from Aspire.

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

## Scores

ZVec Cosine metric exposes **distance** (lower = better). UI similarity % = `max(0, round(100 × (1 − distance)))`.

## Reset paths

| Action | Effect |
|--------|--------|
| **Reset indexes** | Wipes both ZVec folders + stamp; SQL rows remain until **Reset catalog** |
| **Reset catalog** | Deletes SQL/pgvector rows only |
| **Optimize** | Merges flat buffer → HNSW on both collections |

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
```
