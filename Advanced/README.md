# PDDM — Projects Docs Deep Mind

Smart navigator over Apache Spark Jira docs using **ZVec.NET**, **LM Studio**, **ASP.NET Core**, **Blazor**, and **.NET Aspire**.

## Prerequisites

- .NET 10 SDK
- [LM Studio](https://lmstudio.ai/) with embedding + chat models loaded (`localhost:1234`)
- Windows x64 / Linux x64 / osx-arm64 (ZVec.NET native RID)

## Solution layout

```
Advanced/
  src/PDDM.Shared|Core|Api|UI|AppHost|ServiceDefaults
  tests/PDDM.Core.Tests|PDDM.Api.Tests
  docker/Dockerfile.api|Dockerfile.ui
  .codegraph/   # local CodeGraph index (gitignored DB)
  DEMO.md       # golden questions + expected navigator answers
```

API uses classic MVC controllers (`ChatController`, `IngestionController`, `StatsController`, `SettingsController`) — not minimal APIs.

## CodeGraph

Indexed for agent context retrieval. From this folder (`Advanced/`):

```bash
cd Advanced
codegraph init   # first time
codegraph sync   # after edits
```

When calling `codegraph_explore` from Cursor, pass `projectPath` as the workspace `Advanced` folder (repo-relative), not a machine-absolute drive path.

## Run (Aspire)

```bash
cd Advanced
dotnet run --project src/PDDM.AppHost
```

- API: http://localhost:5100
- UI: http://localhost:5200
- Aspire dashboard: Resources should list `pddm-api` / `pddm-ui`; Traces appear after Chat/Ingest once ServiceDefaults OTLP is wired.

## Run (separate processes)

```bash
dotnet run --project src/PDDM.Api --urls http://localhost:5100
dotnet run --project src/PDDM.UI --urls http://localhost:5200
```

## Tests

```bash
dotnet test PDDM.slnx
```

## Docker

LM Studio stays on the host. From `Advanced/`:

```bash
docker build -f docker/Dockerfile.api -t pddm-api .
docker build -f docker/Dockerfile.ui -t pddm-ui .
docker run --rm -p 5100:8080 -e Pddm__LmStudio__BaseUrl=http://host.docker.internal:1234/v1 pddm-api
docker run --rm -p 5200:8080 -e PddmUi__ApiBaseUrl=http://host.docker.internal:5100 pddm-ui
```

## Demo script / golden questions

See **[DEMO.md](DEMO.md)** for Q1–Q3 prompts, expected intents, and optimal navigator responses (markdown Jira links, hierarchy sections, anti-dump rules).

Quick prompts (also as Chat empty-state chips):

1. `I got assigned SPARK-57337 — help me understand it`
2. `I need to add ANSI mode validation so invalid string-to-number casts throw instead of returning null`
3. `Why did they decide to enable ANSI mode by default in Spark 4.0?`

Flow: open UI → **Ingest** (wipes `./data/spark-docs`, seeds SPARK-57337 + SPARK-44444 + ANSI hits) → confirm Stats Tier3/Decision > 0 → **Chat** with the questions above.

## LM Studio ChatModel

- **Recommended:** [`lmstudio-community/Qwen2.5-7B-Instruct-GGUF`](https://huggingface.co/lmstudio-community/Qwen2.5-7B-Instruct-GGUF) at **Q4_K_M** (~4.68 GB). On **4 GB VRAM**, load with **GPU + CPU/RAM offload**, keep context ~4k–8k, and consider unloading the embedding model when not ingesting.
- Set the Chat Model ID in **Settings** (or `Pddm:LmStudio:ChatModel`) to the exact id LM Studio shows after load.
- **Shipped default** (fallback): `google/gemma-4-e2b`. Thinking-style 3B experiments (e.g. VibeThinker) often ramble unless prompts stay strict.

## Notes

- Chat flow: **heuristic intent fast-path** (Jira key / phrase lists) → optional **LLM JSON classify** when the ask is ambiguous → retrieval by scenario → **intent-aware** system/user prompts → streamed answer.
- Embedding dimension is locked at **768**; changing it requires destroying `./data/spark-docs` and re-ingesting (Ingest now recreates the collection each run).
- HybridIndex rebuilds from `chunk-ids.json` + ZVec `Fetch` on API startup.
- UI never references ZVec.NET (API-only).
- Do not commit machine-absolute paths (`D:\…`); use repo-relative paths only.
