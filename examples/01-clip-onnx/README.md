# CLIP ONNX gallery (ZVec.NET)

Index Flickr8k photos with **CLIP ViT-B/32** vision embeddings, store `{id, path, embedding}` in **ZVec.NET**, then search by **text** or **uploaded image** in the same 512-d space.

## Stack

| Piece | Choice |
|-------|--------|
| Host | ASP.NET Core Minimal API (`net10.0`) + static UI |
| Vectors | ZVec.NET `1.0.0-beta.2` typed ODM (`ImageAsset`) |
| Encoders | ONNX Runtime — `vision_model.onnx` + `text_model.onnx` |
| Preprocess | SkiaSharp **fit-contain + pad** to **224×224** (no center-crop discard) |
| Dataset | Flickr8k only, manifest-driven patch ingest (default batch **100**) |

## Setup

```bash
cd examples/01-clip-onnx
dotnet restore
cd src/ClipOnnx.App
dotnet run
```

### Models (auto-download on startup)

On start, the app checks **`ClipOnnx:ModelsDir`** (default `./models` under the content root).

- If all four files are present → load ONNX and mark ready.
- If any are missing and **`AutoDownloadModels`** is `true` (default) → download from Hugging Face into that folder with progress on `GET /api/status` and the UI.
- Files: `vision_model.onnx`, `text_model.onnx`, `vocab.json`, `merges.txt`  
  Source: [inference4j/clip-vit-base-patch32](https://huggingface.co/inference4j/clip-vit-base-patch32)

Override path / URLs in `appsettings.json`:

```json
"ClipOnnx": {
  "ModelsDir": "./models",
  "AutoDownloadModels": true,
  "ModelDownloadUrlTemplate": "https://huggingface.co/inference4j/clip-vit-base-patch32/resolve/main/{file}"
}
```

Optional offline helper (same files): `./scripts/download-models.ps1`

Open the printed URL. Ingest/search stay disabled until **models state = Ready**.

### Ingest + search

1. **Ingest next patch** — first run downloads Flickr8k text + images zips (large), then embeds the next `batchSize` (default 100) manifest entries that are not already in ZVec.
2. **Text search** — natural language → text ONNX → ZVec top-K.
3. **Image search** — upload → same SkiaSharp preprocess + vision ONNX → ZVec top-K.

API:

- `GET /api/status` — includes `models.state`, `models.modelsDir`, per-file progress, `encoderReady`
- `POST /api/ingest` `{ "batchSize": 100, "maxBatches": 1 }`
- `POST /api/search/text` `{ "query": "…", "topK": 10 }`
- `POST /api/search/image` multipart `file` (+ optional `topK`)

## Data layout (gitignored)

```
data/
  flickr8k/images/     # originals for UI thumbnails
  flickr8k/*.txt       # manifests
  zvec-clip-gallery/   # on-disk ZVec collection
  state/flickr8k.json  # manifest offset for resume
models/                # ONNX + vocab (auto-downloaded; not committed)
```

## Schema

```csharp
[ZVecCollection("clip_gallery")]
public sealed class ImageAsset
{
    public string Id { get; set; }
    public string Path { get; set; }
    [ZVecVector(512, Metric = ZVecMetricType.Cosine, M = 16, EfConstruction = 200)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
```

No captions stored. Scores are cosine on L2-normalized 512-d vectors.

## Notes

- Preprocess is **fit-contain + pad**, not CLIP’s original center-crop (keeps all content; slight distribution shift vs training).
- First model download is hundreds of MB; first Flickr download is ~1GB — use `maxBatches: 1` while testing.
- CPU ONNX by default.
