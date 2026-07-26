# CLIP ONNX gallery (ZVec.NET)

Local **CLIP dual-encoder** (ONNX Runtime, CPU) + local **ZVec.NET** Cosine index of **vision** embeddings.
Search is multimodal: **text→image** and **image→image** from pixels. Flickr captions may appear under result cards as **secondary enrichment only** — they are not indexed for primary ranking.

**Same in-process story as PDDM:** vision embeddings live in a local ZVec collection (`data/zvec-clip-gallery/…`) — multimodal search without a Qdrant/pgvector/cloud vector microservice.

## Multi-model picker (B/32 · B/16 · L/14)

| Id | Model | Dim | Notes |
|----|-------|-----|-------|
| `clip-vit-b32` | OpenAI CLIP ViT-B/32 | 512 | Fastest / weakest — smoke tests |
| `clip-vit-b16` | OpenAI CLIP ViT-B/16 | 512 | **Default** — balanced demo |
| `clip-vit-l14` | OpenAI CLIP ViT-L/14 | **768** | Best accuracy — **pre-ingest before the talk** (slow CPU encode) |

ONNX files live under `models/{modelId}/`. ZVec collections live under `data/zvec-clip-gallery/{modelId}/`.

### Critical: never mix models in one index

Embeddings from different CLIP variants are incompatible (even B/32 vs B/16 at 512-d). The gallery stamp stores `ModelId`, `EmbeddingDim`, `EncodePipelineVersion`, and `Offset`. After **Apply model**, if the stamp does not match:

1. Search is blocked with a clear message  
2. Ingest-append is refused  
3. Use **Reset index → Ingest** (Flickr images are kept)

## Live demo script (recommended)

1. Open the UI → pick **CLIP ViT-L/14** (or B/16) → **Apply model** (downloads if needed)  
2. If mismatch banner appears → **Reset index**  
3. **Ingest** enough images **before** the talk (especially L/14)  
4. Status **Demo ready** → use query chips → then **Image → image** for the multimodal punchline  
5. Captions under cards are human Flickr text — say so explicitly if asked  

Banned trap queries in search: bare `network`, `19`.

## Scores

ZVec Cosine metric exposes **cosine distance** on hit `Score` (lower = more similar), not raw CLIP cosθ.

| ZVec distance | CLIP cosθ (`1 − distance`) | UI similarity % |
|---------------|----------------------------|-----------------|
| 0.00 | 1.00 | 100% |
| 0.30 | 0.70 | 70% |
| 0.50 | 0.50 | 50% |
| 1.00 | 0.00 | 0% |
| 2.00 | -1.00 | 0% |

Conversion: `cosine = 1 - zvecScore`, then `similarityPercent = max(0, round(100 * cosine))`.  
Results are sorted **highest cosine first**. Identical vectors → distance ≈ 0 → cos ≈ 1.

Defaults: `MinCosine=0.30`, gap `0.05`, `MinConfidentHits=3` (empty beats junk).

## Setup

```bash
cd examples/01-clip-onnx
dotnet restore
cd src/ClipOnnx.App
dotnet run
```

`appsettings.json`:

```json
"ClipOnnx": {
  "ActiveModelId": "clip-vit-b16",
  "MinCosine": 0.30,
  "MaxCosineGapFromTop": 0.05,
  "MinConfidentHits": 3,
  "TextPromptTemplates": [ "a photo of {query}" ]
}
```

## API

- `GET /api/models` — catalog + expectations  
- `POST /api/models/select` `{ "modelId": "clip-vit-l14" }` — download/load + mismatch flag  
- `GET /api/status` — models, ingest, gallery stamp, `demoReady`, warnings  
- `POST /api/ingest` / `POST /api/ingest/reset`  
- `POST /api/search/text` / `POST /api/search/image`  
- `GET /api/debug/encode-check` — mutual CLIP cosine (active model)  
- `GET /api/debug/probe` / `GET /api/debug/sanity`  

## Hardware note

Laptop-class CPU (e.g. i7-8850H + 32GB) can run B/16 and L/14 **FP32 on CPU**. **4GB VRAM is not assumed** for L/14 dual-encoder CUDA. Pre-embed L/14 before live demos.

## Data layout

```
data/
  flickr8k/images/
  flickr8k/Flickr8k.token.txt   # captions for UI only
  zvec-clip-gallery/{modelId}/  # per-model ZVec
  state/flickr8k.json           # offset + model stamp
models/{modelId}/               # per-model ONNX + vocab
```
