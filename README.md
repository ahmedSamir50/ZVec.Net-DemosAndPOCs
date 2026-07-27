# ZVec.NET Demos & POCs

Repository for demos and proof-of-concepts built on [ZVec.NET](https://github.com/ahmedSamir50/AdamSystems.ZVec.NET).

**Requires ZVec.NET 1.0.0-beta.3.1** (NuGet).

Collections use SDK **`OpenOrCreate`** / DI default `OpenMode = OpenOrCreate` (restart-safe). See package README [Create vs Open](https://github.com/ahmedSamir50/AdamSystems.ZVec.NET#create-vs-open-restart-safe-collections) — do not use obsolete `Create`.

## Why ZVec.NET

**ZVec.NET gives .NET apps production vector search inside the process — typed POCOs, DI, SafeHandles — with no Docker vector cluster, no Postgres extension, and no cloud vector bill.**

What the .NET community gains:

- **Zero extra infrastructure** — no Qdrant / Weaviate / LanceDB container, no `pgvector`, no Pinecone / Azure AI Search account for app-scale RAG, edge, or demos
- **In-process latency & data residency** — no HTTP/gRPC hop; embeddings never leave the app process
- **Native .NET DX** — `AddZVec()`, `[ZVecVector]`, `IZvecCollection<T>`, `ReadOnlyMemory<float>` pin path, SafeHandle lifecycle
- **Full engine surface** — HNSW + metadata filters, FTS/hybrid, rerankers, multiple index types
- **Ship where .NET ships** — net8 / net9 / net10; Windows, Linux, macOS (+ MAUI RIDs in the SDK)

Honest ceiling: single-node scale (millions of vectors per machine). Planet-scale multi-tenant still belongs to managed cloud vector DBs.

| Without ZVec.NET | With ZVec.NET |
|---|---|
| Run Qdrant / Weaviate / LanceDB service | Folder on disk + process memory |
| Or Postgres + pgvector | One NuGet + native RID |
| Or Pinecone / Azure AI Search | No account, no per-query network tax |
| Client SDK + connection strings + ops | `AddZVec()` + typed collection |
| Sensitive embeddings leave the box | Data stays in-process |

Proof in this repo: [Advanced/](Advanced/) (Jira RAG) and [examples/01-clip-onnx](examples/01-clip-onnx/) (CLIP gallery). Talk track: [docs/ZVec.NET_Team_Session.html](docs/ZVec.NET_Team_Session.html).

## Projects

| Path | Description |
|------|-------------|
| [Advanced/](Advanced/) | **PDDM** — Projects Docs Deep Mind (Jira RAG navigator with Aspire + Docker) |
| [examples/01-clip-onnx](examples/01-clip-onnx/) | **CLIP ONNX gallery** — Flickr8k vision embeddings in ZVec; text or image query |

Session deck: [docs/ZVec.NET_Team_Session.html](docs/ZVec.NET_Team_Session.html)

Remote: https://github.com/ahmedSamir50/ZVec.Net-DemosAndPOCs.git
