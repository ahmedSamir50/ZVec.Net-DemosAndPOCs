# MovieLens recommendations (MAUI Blazor Hybrid)

**Requires ZVec.NET 1.0.0-beta.3.1** (NuGet) · **.NET 10** · Windows + Android

Netflix-style **“Because you watched…”** on device: MovieLens titles are embedded with local **all-MiniLM-L6-v2** (ONNX), stored in an in-process **ZVec.NET** HNSW collection, and queried with a **user behaviour vector** (mean of liked movie embeddings). MudBlazor UI · no controllers · no vector cloud.

Talk track: edge proof for [slide 14](../../docs/ZVec.NET_Team_Session.html) in the team session deck. CRUD/`Optimize` context: [slide 11](../../docs/ZVec.NET_Team_Session.html#slide-11).

## Create vs Open

Uses SDK **`OpenOrCreate`** under `FileSystem.AppDataDirectory/zvec-movies`. Stamp file records model id / 384-d / pipeline version. **Reset index** wipes the folder + stamp.

## Optimize

Upserts stage in a flat buffer; **`Optimize()`** merges into HNSW for production-quality ANN (no re-embed). This demo:

1. Runs Optimize automatically at the **end of a successful Ingest**.
2. Exposes an **Optimize index** button for already-ingested collections.
3. **Reopens** the collection after Optimize so Recommend uses a fresh querier over merged segments.

Same pattern in CLIP (`examples/01-clip-onnx`) and PDDM (`Advanced/`).

## Troubleshooting

**`InternalError (Query)` / `fill_result` / `Gandiva: fetch table failed`**

Usually a stale querier after Optimize, or a corrupt AppData index. The app retries once with a reopen. If it still fails: **Reset index → Ingest**. Optimize now reopens the collection automatically after the merge.

## Setup

```powershell
cd examples/02-movie-recs
# Once: fetch MiniLM ONNX (~90MB) if missing
./scripts/download-minilm.ps1
./scripts/download-movielens.ps1   # if movies.csv / ratings.csv missing

dotnet build src/MovieRecs.Maui/MovieRecs.Maui.csproj -f net10.0-windows10.0.19041.0
dotnet build src/MovieRecs.Maui/MovieRecs.Maui.csproj -f net10.0-android
```

Run (Windows):

```powershell
dotnet build src/MovieRecs.Maui/MovieRecs.Maui.csproj -t:Run -f net10.0-windows10.0.19041.0
```

Or open `MovieRecs.slnx` in Visual Studio and deploy to Windows / Android emulator/device.

### Assets

| Path under `Resources/Raw` | Source |
|----------------------------|--------|
| `movielens/movies.csv`, `ratings.csv` | [MovieLens ml-latest-small](https://grouplens.org/datasets/movielens/) |
| `models/all-MiniLM-L6-v2.onnx`, `vocab.txt` | [sentence-transformers/all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) |

The ONNX file is large (~90MB) — prefer the download script; do not rely on it being committed.

## Live demo script

1. Launch on **Windows** (or a warm **Android** device with index already built).
2. Tap **Ingest** once (first run embeds ~9.7k titles — several minutes on CPU; progress bar shows; Optimize runs at the end).
3. Subsequent launches: stamp match → instant **Demo ready**. Optional: **Optimize index** if you want to show the merge step alone.
4. Seed chips (Toy Story / Matrix / Spirited Away) or pick a **MovieLens user** → watchlist summary updates and rec tiles clear → **Recommend**.
5. **More like this** on a result tile replaces the watchlist with that title and re-queries.
6. Note the **CPU · Mem** strip — same recommender on device, no vector sidecar.

Airplane mode works after assets are bundled and the index exists under app data.

## Architecture

- **MAUI Blazor Hybrid** + **MudBlazor** — Blazor `@inject`s services (no Kestrel / Minimal APIs / controllers).
- **MiniLM ONNX** — Bert WordPiece + mean-pool + L2 → 384-d.
- **RecommendService** — mean of watchlist embeddings → `QueryAsync` → drop seen; optional genre filter in-process.
- **PerfMonitorService** — ~1s poll of process CPU % (TotalProcessorTime deltas) + working set MB.
- Targets: `net10.0-windows10.0.19041.0`, `net10.0-android` (iOS deferred).

## Scores

ZVec Cosine metric returns **distance**. UI shows `similarity% = round(100 * (1 - distance))`.
