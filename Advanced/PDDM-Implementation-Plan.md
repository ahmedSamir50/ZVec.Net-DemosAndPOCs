# PDDM — Projects Docs Deep Mind
## Detailed Implementation Plan (Verified Against ZVec.NET v1.0.0-beta.4)

> **Project**: Projects Docs Deep Mind (PDDM)  
> **Stack**: .NET 10 (net10.0) — Separate API + Separate UI  
> **API**: ASP.NET Core Web API (net10.0) — hosts ZVec.NET, LM Studio integration, SSE streaming  
> **UI**: Blazor Server with MudBlazor (net10.0) — thin client, communicates with API via HTTP + SSE  
> **Data Source**: Apache Spark on Apache Jira (issues.apache.org/jira)  
> **Embedding**: Configurable via appsettings.json or UI (default: nomic-embed-text-v1.5, 768-dim, Cosine)  
> **Chat LLM**: Configurable via appsettings.json or UI (default: Qwen2.5-7B-Instruct via LM Studio)  
> **Storage**: ZVec.NET only (single source of truth) — no SQLite  
> **Streaming**: SSE (Server-Sent Events) — UI receives streamed LLM responses from API  
> **Ingestion**: Manual command triggered from UI → API  

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                    PDDM.UI (Blazor Server + MudBlazor)      │
│   Thin client — NO ZVec, NO LM Studio calls                │
│   Communicates with API via:                                │
│     • HTTP POST for ingestion, stats, settings              │
│     • SSE (EventSource) for streaming chat responses        │
└──────────────────────────┬──────────────────────────────────┘
                           │  HTTP + SSE
                           │  (localhost:5100 → localhost:5200)
┌──────────────────────────▼──────────────────────────────────┐
│                    PDDM.Api (ASP.NET Core Web API)           │
│   Hosts ZVec.NET, LM Studio HTTP client, all services      │
│   SSE endpoint: GET /api/chat/stream?question=...           │
│   REST endpoints: /api/ingestion, /api/stats, /api/settings │
│   ZVec.NET collection: spark_docs (JiraDocChunk POCO)      │
└─────────────────────────────────────────────────────────────┘
                           │
                           │  HTTP
                           │  (localhost:1234)
┌──────────────────────────▼──────────────────────────────────┐
│                    LM Studio (Local AI Server)               │
│   OpenAI-compatible API: /v1/embeddings, /v1/chat/completions│
│   Models configurable via appsettings.json or UI            │
└─────────────────────────────────────────────────────────────┘
                           │
                           │  HTTP (REST API)
                           │  (issues.apache.org/jira)
┌──────────────────────────▼──────────────────────────────────┐
│                    Apache Jira (Data Source)                 │
│   Project: SPARK — Epics, Stories, Bugs, Comments           │
│   No auth required, expand=comments, Epic Link via field    │
└─────────────────────────────────────────────────────────────┘
```

**Key Design Principles**:
- ZVec.NET lives ONLY in PDDM.Api — never in UI project (WASM compatibility is irrelevant)
- UI is a THIN CLIENT — it sends user messages and receives SSE responses
- All AI model settings (embedding model, chat model, dimensions, temperature) are configurable via `appsettings.json` AND via a Settings page in the UI that updates the API's runtime configuration
- SSE streaming: The API streams LLM `chat/completions` responses token-by-token to the UI via SSE, so the user sees the answer building up in real-time

---

## ZVec.NET API Constraints (MUST honor these — from verified repo README)

| Constraint | Impact on PDDM |
|---|---|
| **Expression filters** only support `==`, `!=`, `<`, `<=`, `>`, `>=`, `&&`, `||`, `!`, `null` checks | No `Contains()`, `StartsWith()` in typed queries. Use `ZVecFilterBuilder.ContainAny/Like` via `.Untyped` for array/string-like filtering |
| **`CreateAndOpen` throws if path already exists** | Prefer SDK `IZvecFactory.OpenOrCreate` / DI `OpenMode = OpenOrCreate` (beta.3+; obsolete `Create` bool) |
| **`QueryGroupBy` throws `NotSupportedException`** | Group results client-side after query |
| **Typed `Query` returns `IReadOnlyList<ZVecQueryResult<T>>`** (not `ZVecDoc`) | Access `hit.Record` for the typed entity, `hit.Score` for similarity score |
| **Typed `Fetch` returns `T?`** (nullable) | Always null-check; `Fetch(id, includeVector: false)` for metadata-only reads |
| **Async = cancellation-aware sync wrappers** (NOT thread-pool offloads) | For batch insert, use sync APIs on BackgroundService, not `Task.Run` per request |
| **DDL `add_column` only adds nullable numeric columns** | ALL string/array fields MUST be in create-time schema — no later DDL |
| **`includeVector: true` default** | Always pass `includeVector: false` when you don't need result embeddings (lower latency) |

**Note**: WASM compatibility is NOT a concern for PDDM because ZVec.NET is ONLY used in the API project. The UI project does not reference ZVec.NET at all. This constraint is listed for completeness but has zero impact on our architecture.

---

## Epic Breakdown

---

## Epic 1: Project Foundation & Infrastructure

**Goal**: Create the .NET 10 solution with SEPARATE API and UI projects, configure ZVec.NET DI in API only, LM Studio HTTP client in API only, SSE infrastructure, and all project structure.

### Story 1.1: Create Solution & Projects

**Files to create**:
```
PDDM/
├── PDDM.sln
├── src/
│   ├── PDDM.Core/               # Domain models, services (net10.0 class library)
│   ├── PDDM.Api/                # ASP.NET Core Web API — ZVec, LM Studio, SSE (net10.0)
│   ├── PDDM.UI/                 # Blazor Server + MudBlazor — thin client (net10.0)
│   └── PDDM.Shared/             # Shared DTOs between API and UI (net10.0 class library)
├── data/
│   └── spark-docs/              # ZVec.NET persistent storage path (API-side only)
├── appsettings.json             # In PDDM.Api (with model config) and PDDM.UI (with API URL)
└── Directory.Build.props        # Common build settings for net10.0
```

**Commands**:
```bash
dotnet new sln -n PDDM
dotnet new classlib -n PDDM.Core -o src/PDDM.Core -f net10.0
dotnet new webapi -n PDDM.Api -o src/PDDM.Api -f net10.0
dotnet new blazor -n PDDM.UI -o src/PDDM.UI -f net10.0
dotnet new classlib -n PDDM.Shared -o src/PDDM.Shared -f net10.0
dotnet sln add src/PDDM.Core src/PDDM.Api src/PDDM.UI src/PDDM.Shared
```

**Project references**:
- PDDM.Api → PDDM.Core, PDDM.Shared
- PDDM.UI → PDDM.Shared (UI does NOT reference PDDM.Core — it's a thin client)
- PDDM.Core → no internal references (pure domain library)
- PDDM.Shared → no internal references (DTOs only)

**NuGet packages for PDDM.Core** (API-side library with ZVec + LM Studio):
```bash
dotnet add src/PDDM.Core package ZVec.NET --version 1.0.0-beta.4
dotnet add src/PDDM.Core package Microsoft.Extensions.Http
```

**NuGet packages for PDDM.Api** (API project only):
```bash
dotnet add src/PDDM.Api package Microsoft.AspNetCore.OpenApi  # For SSE + minimal API
```

**NuGet packages for PDDM.UI** (UI project only):
```bash
dotnet add src/PDDM.UI package MudBlazor
```

**⚠️ IMPORTANT**: PDDM.UI does NOT reference ZVec.NET or PDDM.Core. The UI is a thin client that communicates exclusively with PDDM.Api via HTTP and SSE. All vector operations and LM Studio calls happen in PDDM.Api.

### Story 1.2: Configuration Model (Configurable Models)

**File**: `src/PDDM.Core/Configuration/PddmSettings.cs`

**Key change**: Chat and embedding models are configurable — defaults in appsettings.json, and a Settings API endpoint + UI Settings page allows runtime updates.

```csharp
namespace PDDM.Core.Configuration;

public sealed class PddmSettings
{
    public LmStudioSettings LmStudio { get; set; } = new();
    public JiraSettings Jira { get; set; } = new();
    public ZVecSettings ZVec { get; set; } = new();
    public IngestionSettings Ingestion { get; set; } = new();
}

/// <summary>
/// LM Studio connection and model settings.
/// ALL fields are configurable via appsettings.json AND via the UI Settings page.
/// The UI sends updated settings to the API's /api/settings endpoint.
/// The API stores current settings in a mutable singleton that can be refreshed at runtime.
/// </summary>
public sealed class LmStudioSettings
{
    public string BaseUrl { get; set; } = "http://localhost:1234/v1";
    public string EmbeddingModel { get; set; } = "text-embedding-nomic-embed-text-v1.5";
    public string ChatModel { get; set; } = "lmstudio-community/Qwen2.5-7B-Instruct-GGUF";
    public int EmbeddingDimensions { get; set; } = 768;
    public float ChatTemperature { get; set; } = 0.3f;
    public int ChatMaxTokens { get; set; } = -1;
    public int EmbeddingBatchSize { get; set; } = 50;
}

public sealed class JiraSettings
{
    public string BaseUrl { get; set; } = "https://issues.apache.org/jira/rest/api/2";
    public string ProjectKey { get; set; } = "SPARK";
    public int MaxResultsPerRequest { get; set; } = 100;
    public int RequestDelayMs { get; set; } = 1000;  // Rate limiting: 1 req/sec
}

public sealed class ZVecSettings
{
    public string CollectionPath { get; set; } = "./data/spark-docs";
    public string LogLevel { get; set; } = "Warn";
    public int QueryThreads { get; set; } = -1;
    public int MemoryLimitMb { get; set; } = 512;
    public bool EnableMmap { get; set; } = true;
}

public sealed class IngestionSettings
{
    public int MaxEpics { get; set; } = 74;
    public int MaxStories { get; set; } = 138;
    public int MaxUmbrellas { get; set; } = 506;
    public int MaxBugs { get; set; } = 500;
    public int MaxImprovements { get; set; } = 500;
    public int MaxTasks { get; set; } = 100;
    public int MaxCommentsPerIssue { get; set; } = 10;
    public int MinCommentsForIssue { get; set; } = 2;  // Only fetch issues with >= 2 comments
}
```

**File**: `src/PDDM.Api/appsettings.json`

```json
{
  "Pddm": {
    "LmStudio": {
      "BaseUrl": "http://localhost:1234/v1",
      "EmbeddingModel": "text-embedding-nomic-embed-text-v1.5",
      "ChatModel": "lmstudio-community/Qwen2.5-7B-Instruct-GGUF",
      "EmbeddingDimensions": 768,
      "ChatTemperature": 0.3,
      "ChatMaxTokens": -1,
      "EmbeddingBatchSize": 50
    },
    "Jira": {
      "BaseUrl": "https://issues.apache.org/jira/rest/api/2",
      "ProjectKey": "SPARK",
      "MaxResultsPerRequest": 100,
      "RequestDelayMs": 1000
    },
    "ZVec": {
      "CollectionPath": "./data/spark-docs",
      "LogLevel": "Warn",
      "QueryThreads": -1,
      "MemoryLimitMb": 512,
      "EnableMmap": true
    },
    "Ingestion": {
      "MaxEpics": 74,
      "MaxStories": 138,
      "MaxUmbrellas": 506,
      "MaxBugs": 500,
      "MaxImprovements": 500,
      "MaxTasks": 100,
      "MaxCommentsPerIssue": 10,
      "MinCommentsForIssue": 2
    }
  },
  "ZVec": {
    "LogLevel": "Warn",
    "QueryThreads": -1,
    "MemoryLimitMb": 512,
    "MaxConcurrentNativeCalls": 0
  }
}
```

**File**: `src/PDDM.UI/appsettings.json`

```json
{
  "PddmUi": {
    "ApiBaseUrl": "http://localhost:5100"
  }
}
```

The UI only needs to know where the API is. ALL model configuration happens on the API side.

### Story 1.3: ZVec.NET DI Integration (API Project Only)

**File**: `src/PDDM.Api/Program.cs`

ZVec.NET is registered ONLY in PDDM.Api. The UI project has no ZVec references.

```csharp
using ZVec.NET;
using ZVec.NET.DependencyInjection;
using ZVec.NET.Mapping;
using PDDM.Core.Configuration;
using PDDM.Core.Models;
using PDDM.Core.Services;

var builder = WebApplication.CreateBuilder(args);

// Bind PDDM settings from appsettings.json
builder.Services.Configure<PddmSettings>(builder.Configuration.GetSection("Pddm"));

// ⚠️ Fix 12: Use explicit mutable singleton PddmRuntimeSettings for runtime config.
// IOptionsMonitor does NOT magically sync with PUT /api/settings — it only reloads
// when the JSON file changes on disk AND the file watcher triggers (which is unreliable).
// Instead: PddmRuntimeSettings is initialized from IOptions<PddmSettings> at startup,
// then updated directly by SettingsService when the UI sends PUT /api/settings.
// All services that need runtime-updated config (EmbeddingService, ChatService, etc.)
// inject PddmRuntimeSettings, NOT IOptionsMonitor.
builder.Services.AddSingleton<PddmRuntimeSettings>(sp =>
{
    var initial = sp.GetRequiredService<IOptions<PddmSettings>>().Value;
    return new PddmRuntimeSettings(initial);
});

// ZVec.NET global factory initialization
builder.Services.AddZVec(options =>
{
    options.LogLevel = ZVecLogLevel.Warn;
    options.QueryThreads = -1;
    options.MemoryLimitMb = 512;
    options.MaxConcurrentNativeCalls = 0;
});

// ZVec.NET typed collection — OpenOrCreate (restart-safe; package README “Create vs Open”)
builder.Services.AddSingleton<IZvecCollection<JiraDocChunk>>(sp =>
{
    var factory = sp.GetRequiredService<IZvecFactory>();
    var settings = sp.GetRequiredService<IOptions<PddmSettings>>().Value;
    var options = new ZVecCollectionOptions { EnableMmap = settings.ZVec.EnableMmap };
    var path = settings.ZVec.CollectionPath;
    var schema = ZVecCollectionSchemaBuilder.From<JiraDocChunk>().Build();
    // Prefer factory.OpenOrCreate over CreateAndOpen (throws if path exists) / Open branching.
    // DI AddZVecCollection defaults OpenMode = OpenOrCreate; obsolete Create bool → CreateOnly/OpenOnly.
    var untyped = factory.OpenOrCreate(path, schema, options);
    return new ZVecCollection<JiraDocChunk>(untyped);
});

// LM Studio HTTP client (API-side only)
builder.Services.AddHttpClient("LmStudio", client =>
{
    var baseUrl = builder.Configuration["Pddm:LmStudio:BaseUrl"]!;
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(120);
});

// Register core services (ALL in API project only)
// ⚠️ Fix 12: Services inject PddmRuntimeSettings (not IOptionsMonitor)
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<JiraFetcherService>();
builder.Services.AddSingleton<ChunkingService>();
builder.Services.AddSingleton<DecisionDetector>();
builder.Services.AddSingleton<VectorStoreService>();
builder.Services.AddSingleton<HybridIndexService>();
builder.Services.AddSingleton<NavigationEngine>();
builder.Services.AddSingleton<ContextBuilderService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<IntentClassifier>();
builder.Services.AddSingleton<IngestionOrchestrator>();

// Settings service (allows UI to update model config at runtime)
builder.Services.AddSingleton<SettingsService>();

// CORS — allow PDDM.UI (different port) to call PDDM.Api
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowUI", policy =>
    {
        policy.WithOrigins("http://localhost:5200")  // PDDM.UI Blazor Server
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors("AllowUI");

// Initialize ZVecFactory on startup
var factory = app.Services.GetRequiredService<IZvecFactory>();
factory.Initialize(new ZVecOptions
{
    LogLevel = ZVecLogLevel.Warn,
    QueryThreads = -1,
    MemoryLimitMb = 512
});

// ⚠️ Fix 6: Rebuild HybridIndex from ZVec on startup (restart safety)
// If ZVec collection has data (from previous ingestion), rebuild the in-memory
// navigation cache so navigation works immediately after restart — no re-ingest needed.
var collection = app.Services.GetRequiredService<IZvecCollection<JiraDocChunk>>();
var hybridIndex = app.Services.GetRequiredService<HybridIndexService>();
if (collection.Stats.RecordCount > 0)
{
    hybridIndex.RebuildFromZVec(collection);
}

// Map API endpoints (see Epic 4)
app.MapControllers();

app.Run();
```

**⚠️ CRITICAL**: `IZvecFactory.Initialize()` must be called BEFORE any collection operations.

**⚠️ CRITICAL**: CORS is needed because PDDM.Api and PDDM.UI run on different ports. SSE also requires CORS.

### Story 1.4: LM Studio HTTP Client (API-Side Only)

**File**: `src/PDDM.Core/Services/LmStudioHttpClient.cs`

A typed HTTP client for LM Studio's OpenAI-compatible API. Only used in PDDM.Api.

**Endpoints needed**:
- `POST /v1/embeddings` — batch text → float[N] vectors (dimensions configurable)
- `POST /v1/chat/completions` — RAG inference (with `stream: true` for SSE)
- `GET /v1/models` — verify models are loaded

**Verification check**: On startup, PDDM.Api should call `GET /v1/models` to verify LM Studio is running.

### Story 1.5: SSE Infrastructure (API-Side)

**File**: `src/PDDM.Api/Endpoints/SseChatEndpoint.cs`

The SSE endpoint is the core communication pattern between UI and API for chat. The UI sends a question, the API streams the LLM response token-by-token via SSE.

```csharp
namespace PDDM.Api.Endpoints;

/// <summary>
/// SSE chat endpoint — streams LLM response token-by-token to the UI.
/// 
/// Flow:
/// 1. UI creates EventSource connection to GET /api/chat/stream?question=...
/// 2. API classifies intent, navigates docs, assembles context
/// 3. API calls LM Studio /v1/chat/completions with stream=true
/// 4. API forwards each token as SSE event: data: {"token": "...", "intent": "..."}
/// 5. When done, API sends: data: {"done": true, "context": [...]}
/// </summary>
public static class SseChatEndpoint
{
    public static async Task StreamChat(
        string question,
        NavigationEngine navigationEngine,
        ContextBuilderService contextBuilder,
        ChatService chatService,
        HttpResponse response,
        CancellationToken ct)
    {
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";

        // Step 1: Navigate (intent classification + retrieval + hierarchy expansion)
        var navContext = await navigationEngine.Navigate(question);

        // Step 2: Build RAG context
        navContext.AssembledContext = contextBuilder.BuildContext(navContext);

        // Step 3: Send intent as first SSE event
        var intentEvent = JsonSerializer.Serialize(new { intent = navContext.Intent.ToString() });
        await response.WriteAsync($"event: intent\ndata: {intentEvent}\n\n", ct);
        await response.Body.FlushAsync(ct);

        // Step 4: Stream LLM response (LM Studio with stream=true)
        // ChatService now returns IAsyncEnumerable<string> for streaming
        var contextItems = GetContextItems(navContext);
        
        await foreach (var token in chatService.StreamRagResponseAsync(
            navContext.AssembledContext, question, ct))
        {
            var tokenEvent = JsonSerializer.Serialize(new { token });
            await response.WriteAsync($"event: token\ndata: {tokenEvent}\n\n", ct);
            await response.Body.FlushAsync(ct);
        }

        // Step 5: Send final context info
        var doneEvent = JsonSerializer.Serialize(new
        {
            done = true,
            contextItems = contextItems,
            retrievedIds = GetRetrievedIds(navContext)
        });
        await response.WriteAsync($"event: done\ndata: {doneEvent}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    // ... helper methods same as before
}
```

### Story 1.6: Settings API Endpoint (Configurable Models from UI)

**File**: `src/PDDM.Api/Controllers/SettingsController.cs`

Allows the UI to read and update model configuration at runtime.

```csharp
namespace PDDM.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SettingsController : ControllerBase
{
    private readonly SettingsService _settingsService;

    public SettingsController(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>Get current settings (UI displays these in Settings page)</summary>
    [HttpGet]
    public ActionResult<LmStudioSettings> GetSettings()
    {
        return Ok(_settingsService.GetCurrentSettings());
    }

    /// <summary>Update settings (UI Settings page submits changes)</summary>
    /// ⚠️ Fix 7+12: Returns success/error — blocks dimension changes without reset
    [HttpPut]
    public ActionResult UpdateSettings([FromBody] LmStudioSettingsDto newSettings)
    {
        var (success, error) = _settingsService.UpdateSettings(newSettings);
        if (!success)
            return BadRequest(new { error });
        return Ok();
    }

    /// <summary>Reset ZVec collection (required before changing embedding dimensions)</summary>
    [HttpPost("reset-collection")]
    public ActionResult ResetCollection()
    {
        _settingsService.ResetCollection();
        return Ok(new { message = "Collection reset. Re-ingest to populate with new model." });
    }

    /// <summary>Verify LM Studio is running with current settings</summary>
    [HttpGet("verify")]
    public async Task<ActionResult<bool>> VerifyLmStudio(CancellationToken ct)
    {
        return Ok(await _settingsService.VerifyLmStudioAsync(ct));
    }
}
```

**File**: `src/PDDM.Core/Configuration/PddmRuntimeSettings.cs` + `src/PDDM.Core/Services/SettingsService.cs`

**⚠️ Fix 12**: Replace IOptionsMonitor fantasy with explicit mutable singleton. `IOptionsMonitor` does NOT sync with PUT /api/settings — it only reloads when the JSON file changes on disk AND the file watcher triggers (which is unreliable for runtime updates). Instead, `PddmRuntimeSettings` is initialized from `IOptions<PddmSettings>` at startup, then updated directly by `SettingsService`.

```csharp
// src/PDDM.Core/Configuration/PddmRuntimeSettings.cs
namespace PDDM.Core.Configuration;

/// <summary>
/// Explicit mutable singleton for runtime-updated settings.
/// Initialized from appsettings.json at startup, updated directly
/// by SettingsService when UI sends PUT /api/settings.
/// Services inject this, NOT IOptionsMonitor.
/// 
/// ⚠️ Fix 7: EmbeddingDimensions is pinned for the collection lifetime.
/// Changing EmbeddingModel/Dimensions requires destroying the ZVec collection
/// and re-ingesting. The SettingsService.UpdateSettings method enforces this.
/// </summary>
public sealed class PddmRuntimeSettings
{
    public LmStudioSettings LmStudio { get; set; }
    public JiraSettings Jira { get; set; }
    public ZVecSettings ZVec { get; set; }
    public IngestionSettings Ingestion { get; set; }

    public PddmRuntimeSettings(PddmSettings initial)
    {
        LmStudio = initial.LmStudio;
        Jira = initial.Jira;
        ZVec = initial.ZVec;
        Ingestion = initial.Ingestion;
    }
}
```

```csharp
// src/PDDM.Core/Services/SettingsService.cs
namespace PDDM.Core.Services;

/// <summary>
/// Manages runtime-updatable settings using explicit mutable singleton.
/// 
/// ⚠️ Fix 12: Does NOT rely on IOptionsMonitor. Instead, directly updates
/// PddmRuntimeSettings singleton + persists to appsettings.json for restart persistence.
/// 
/// ⚠️ Fix 7: Blocks dimension changes without collection reset.
/// </summary>
public sealed class SettingsService
{
    private readonly PddmRuntimeSettings _runtimeSettings;
    private readonly EmbeddingService _embeddingService;
    private readonly VectorStoreService _vectorStore;
    private readonly IZvecCollection<JiraDocChunk> _collection;

    // The original dimensions — pinned at startup, cannot be changed without reset
    private readonly int _pinnedDimensions;

    public SettingsService(
        PddmRuntimeSettings runtimeSettings,
        EmbeddingService embeddingService,
        VectorStoreService vectorStore,
        IZvecCollection<JiraDocChunk> collection)
    {
        _runtimeSettings = runtimeSettings;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _collection = collection;
        _pinnedDimensions = runtimeSettings.LmStudio.EmbeddingDimensions;
    }

    public LmStudioSettings GetCurrentSettings() => _runtimeSettings.LmStudio;

    public (bool Success, string? Error) UpdateSettings(LmStudioSettings newSettings)
    {
        // ⚠️ Fix 7: Block dimension changes without collection reset
        if (newSettings.EmbeddingDimensions != _pinnedDimensions)
        {
            return (false, 
                $"Embedding dimensions cannot be changed from {_pinnedDimensions} to {newSettings.EmbeddingDimensions}. " +
                "Changing dimensions requires destroying the ZVec collection and re-ingesting. " +
                "Use the 'Reset Collection' button in Settings before changing the embedding model.");
        }

        // Update the mutable singleton directly (immediate effect)
        _runtimeSettings.LmStudio = newSettings;

        // Persist to appsettings.json for restart survival
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var json = File.ReadAllText(configPath);
        var doc = JsonNode.Parse(json)!;
        
        doc["Pddm"]!["LmStudio"]!["BaseUrl"] = newSettings.BaseUrl;
        doc["Pddm"]!["LmStudio"]!["EmbeddingModel"] = newSettings.EmbeddingModel;
        doc["Pddm"]!["LmStudio"]!["ChatModel"] = newSettings.ChatModel;
        doc["Pddm"]!["LmStudio"]!["EmbeddingDimensions"] = newSettings.EmbeddingDimensions;
        doc["Pddm"]!["LmStudio"]!["ChatTemperature"] = newSettings.ChatTemperature;
        doc["Pddm"]!["LmStudio"]!["ChatMaxTokens"] = newSettings.ChatMaxTokens;
        doc["Pddm"]!["LmStudio"]!["EmbeddingBatchSize"] = newSettings.EmbeddingBatchSize;
        
        File.WriteAllText(configPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return (true, null);
    }

    /// <summary>Reset the ZVec collection (required before changing embedding dimensions)</summary>
    public void ResetCollection()
    {
        // Delete the ZVec collection directory and recreate
        var path = _runtimeSettings.ZVec.CollectionPath;
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        
        // The collection will be recreated on next startup or ingestion
    }

    public async Task<bool> VerifyLmStudioAsync(CancellationToken ct)
    {
        return await _embeddingService.VerifyLmStudioAsync(ct);
    }
}
```

---

## Epic 2: Data Models (ZVec.NET Verified)

**Goal**: Define all POCOs with proper ZVec.NET attributes, Jira API DTOs, LM Studio DTOs, and PDDM internal models. Shared DTOs live in PDDM.Shared for both API and UI.

### Story 2.1: JiraDocChunk POCO (ZVec.NET Typed Collection Document)

**File**: `src/PDDM.Core/Models/JiraDocChunk.cs`

This is the **single most critical model** — it defines what goes into ZVec.NET.

```csharp
using ZVec.NET.Mapping;

namespace PDDM.Core.Models;

/// <summary>
/// Single ZVec.NET collection document representing one chunk of Jira project docs.
/// Stored as typed collection via IZvecCollection<JiraDocChunk>.
/// 
/// ID format: "{chunkType}_{issueKey}" or "comment_{issueKey}_{commentIndex}"
/// Examples: "epic_SPARK-56664", "story_SPARK-56962", "bug_SPARK-8469",
///           "subtask_SPARK-51530", "comment_SPARK-8469_0"
/// </summary>
[ZVecCollection("spark_docs")]  // Collection name in ZVec.NET
public sealed class JiraDocChunk
{
    // ── Identity ──
    // Convention: "Id" property is auto-recognized as [ZVecId]
    public string Id { get; set; } = "";

    // ── Vector Field ──
    // Dimensions come from appsettings (default 768 for nomic-embed-text-v1.5)
    // But ZVec.NET requires compile-time constant for [ZVecVector] attribute.
    // SOLUTION: Use the maximum possible dimensions in the attribute (768),
    // and if user switches to a smaller model, unused dimensions are zero-padded.
    // For larger models, we'd need to recreate the collection.
    // For POC: 768 is sufficient — we document this limitation.
    [ZVecVector(768, Metric = ZVecMetricType.Cosine, M = 32, EfConstruction = 256)]
    public ReadOnlyMemory<float> Embedding { get; set; }

    // ── Tier & Type ──
    // Tier: 0=Epic/Umbrella, 1=Issue, 2=Sub-task, 3=Comment
    // Expression filter: p.Tier == 1 ✅ WORKS
    public int Tier { get; set; }

    // IssueType: "Epic", "Story", "Umbrella", "Bug", "Improvement", 
    //             "Task", "New Feature", "Sub-task", "Comment"
    // Expression filter: p.IssueType == "Bug" ✅ WORKS
    public string IssueType { get; set; } = "";

    // ── Jira Identity ──
    // Key: "SPARK-56664", "SPARK-8469"
    // Expression filter: p.Key == "SPARK-56664" ✅ WORKS
    public string Key { get; set; } = "";

    // ── Hierarchy Navigation ──
    // EpicLink: parent Epic key (from Jira customfield_12311120)
    // For Tier 0 (Epic): empty string
    // For Tier 1 (Issue): the Epic they belong to (e.g., "SPARK-56664")
    // For Tier 2 (Sub-task): inherited from parent issue
    // For Tier 3 (Comment): inherited from parent issue's EpicLink
    // Expression filter: p.EpicLink == "SPARK-56664" ✅ WORKS
    public string EpicLink { get; set; } = "";

    // ParentKey: parent issue/subtask/comment key
    // For Tier 0/1: empty string
    // For Tier 2 (Sub-task): parent issue key
    // For Tier 3 (Comment): the issue this comment belongs to
    // Expression filter: p.ParentKey == "SPARK-8469" ✅ WORKS
    public string ParentKey { get; set; } = "";

    // ── Content ──
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";

    // ── Categorization ──
    public string Components { get; set; } = "";  // semicolon-separated
    public string Labels { get; set; } = "";
    public string FixVersions { get; set; } = "";
    public string Priority { get; set; } = "";
    public string Assignee { get; set; } = "";

    // ── Comment-Specific (Tier 3 only) ──
    public string CommentAuthor { get; set; } = "";
    
    // ContainsDecision: THE key filter for Scenario C
    // Expression filter: p.ContainsDecision == true ✅ WORKS
    public bool ContainsDecision { get; set; }

    // ── Umbrella Navigation (Fix 11) ──
    // UmbrellaLink: parent Umbrella key (from issuelinks field)
    // For Tier 0 (Epic): linked Umbrella key (if any)
    // For Tier 1 (Issue): inherited from parent Epic's UmbrellaLink
    // For Tier 2/3: inherited from parent
    // Expression filter: p.UmbrellaLink == "SPARK-12345" ✅ WORKS
    // Enables UP-UP navigation: Issue → Epic → Umbrella
    public string UmbrellaLink { get; set; } = "";
}
```

**⚠️ DESIGN NOTE on Vector Dimensions**: The `[ZVecVector]` attribute requires compile-time constants. The default dimension (768) is baked into the POCO. If the user switches to a different embedding model with different dimensions via the Settings page, two cases exist:
- **⚠️ CRITICAL (Fix 7)**: Zero-padding smaller models to 768 dims **is NOT safe** — different embedding models produce incompatible vector spaces even at the same dimension. Padding changes norms/geometry. The correct approach is: **pin one embedding model for the collection lifetime**. If the user switches model or dimensions → destroy the ZVec collection + re-ingest. The Settings UI must block dimension changes without a collection reset (not silently pad).
- **Larger model** (> 768 dims): Not supported — would require recreating the ZVec collection with a new `[ZVecVector]` dimension attribute. Document this limitation.

### Story 2.2: Jira API Response DTOs

**File**: `src/PDDM.Core/Models/JiraApiModels.cs`

Same as previous plan — unchanged. Raw JSON response DTOs from Apache Jira REST API.

```csharp
namespace PDDM.Core.Models.JiraApi;

public sealed class JiraSearchResult
{
    public int StartAt { get; set; }
    public int MaxResults { get; set; }
    public int Total { get; set; }
    public List<JiraIssue> Issues { get; set; } = [];
}

public sealed class JiraIssue
{
    public string Key { get; set; } = "";
    public string Id { get; set; } = "";
    public JiraIssueFields Fields { get; set; } = new();
}

public sealed class JiraIssueFields
{
    public JiraIssueType? Issuetype { get; set; }
    public string Summary { get; set; } = "";
    public string? Description { get; set; }
    public JiraStatus? Status { get; set; }
    public JiraPriority? Priority { get; set; }
    public List<JiraComponent> Components { get; set; } = [];
    public List<JiraVersion> FixVersions { get; set; } = [];
    public List<string> Labels { get; set; } = [];
    public JiraUser? Assignee { get; set; }
    public JiraUser? Creator { get; set; }
    public JiraUser? Reporter { get; set; }
    
    // ── Hierarchy fields ──
    public string? Customfield_12311120 { get; set; }  // Epic Link
    public string? Customfield_12311121 { get; set; }  // Epic Name
    
    // ⚠️ Fix 2: Parent field for sub-tasks (Jira REST API returns parent for sub-task type)
    // Sub-tasks have: { "parent": { "key": "SPARK-XXXX", "fields": { "summary": "...", "status": {...} } } }
    public JiraLinkedIssue? Parent { get; set; }  // Parent issue reference for sub-tasks
    
    public List<JiraSubtask> Subtasks { get; set; } = [];
    public List<JiraIssueLink> Issuelinks { get; set; } = [];
    public JiraComments? Comment { get; set; }
}

public sealed class JiraIssueType { public string Name { get; set; } = ""; public bool Subtask { get; set; } }
public sealed class JiraStatus { public string Name { get; set; } = ""; }
public sealed class JiraPriority { public string Name { get; set; } = ""; }
public sealed class JiraComponent { public string Name { get; set; } = ""; }
public sealed class JiraVersion { public string Name { get; set; } = ""; }
public sealed class JiraUser { public string DisplayName { get; set; } = ""; public string? Name { get; set; } }
public sealed class JiraSubtask { public string Key { get; set; } = ""; public JiraIssueType? Issuetype { get; set; } public string Summary { get; set; } = ""; public JiraStatus? Status { get; set; } }
public sealed class JiraIssueLink { public string Type { get; set; } = ""; public JiraLinkedIssue? OutwardIssue { get; set; } public JiraLinkedIssue? InwardIssue { get; set; } }
public sealed class JiraLinkedIssue { public string Key { get; set; } = ""; public JiraIssueType? Issuetype { get; set; } public string Summary { get; set; } = ""; public JiraStatus? Status { get; set; } }
public sealed class JiraComments { public List<JiraComment> Comments { get; set; } = []; }
public sealed class JiraComment { public string Id { get; set; } = ""; public string Body { get; set; } = ""; public JiraUser? Author { get; set; } public DateTime Created { get; set; } }
```

### Story 2.3: LM Studio API DTOs (with streaming support)

**File**: `src/PDDM.Core/Models/LmStudioModels.cs`

Updated to include streaming response types (for SSE).

```csharp
namespace PDDM.Core.Models.LmStudio;

// ── Embedding API ──
public sealed class EmbeddingRequest
{
    public string Model { get; set; } = "";
    public List<string> Input { get; set; } = [];
}

public sealed class EmbeddingResponse
{
    public string Object { get; set; } = "";
    public string Model { get; set; } = "";
    public List<EmbeddingData> Data { get; set; } = [];
    public EmbeddingUsage Usage { get; set; } = new();
}

public sealed class EmbeddingData
{
    public string Object { get; set; } = "";
    public List<float> Embedding { get; set; } = [];
    public int Index { get; set; }
}

public sealed class EmbeddingUsage
{
    public int PromptTokens { get; set; }
    public int TotalTokens { get; set; }
}

// ── Chat Completions API (with stream support) ──
public sealed class ChatCompletionRequest
{
    public string Model { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = [];
    public float Temperature { get; set; } = 0.3f;
    public int MaxTokens { get; set; } = -1;
    /// <summary>Set to true for SSE streaming — LM Studio returns chunks</summary>
    public bool Stream { get; set; } = false;
}

public sealed class ChatMessage
{
    public string Role { get; set; } = "";  // "system", "user", "assistant"
    public string Content { get; set; } = "";
}

// ── Streaming response chunk (from LM Studio when stream=true) ──
public sealed class ChatStreamChunk
{
    public string Id { get; set; } = "";
    public string Object { get; set; } = "";  // "chat.completion.chunk"
    public string Model { get; set; } = "";
    public List<ChatStreamChoice> Choices { get; set; } = [];
}

public sealed class ChatStreamChoice
{
    public int Index { get; set; }
    public ChatStreamDelta? Delta { get; set; }
    public string? FinishReason { get; set; }  // null while streaming, "stop" when done
}

public sealed class ChatStreamDelta
{
    public string? Role { get; set; }      // Only in first chunk
    public string? Content { get; set; }   // Token text in subsequent chunks
}

// ── Non-streaming response (fallback) ──
public sealed class ChatCompletionResponse
{
    public string Id { get; set; } = "";
    public string Model { get; set; } = "";
    public List<ChatChoice> Choices { get; set; } = [];
}

public sealed class ChatChoice
{
    public int Index { get; set; }
    public ChatMessage Message { get; set; } = new();
    public string FinishReason { get; set; } = "";
}

// ── Models List API ──
public sealed class ModelsResponse
{
    public string Object { get; set; } = "";
    public List<ModelInfo> Data { get; set; } = [];
}

public sealed class ModelInfo
{
    public string Id { get; set; } = "";
    public string Object { get; set; } = "";
}
```

### Story 2.4: PDDM Internal Models (Navigation, Context, Intent)

**File**: `src/PDDM.Core/Models/PddmModels.cs`

```csharp
namespace PDDM.Core.Models;

/// <summary>Three input scenarios for PDDM</summary>
public enum QueryIntent
{
    AssignedIssue,      // "I got assigned SPARK-57337" → known key
    NewRequirement,     // "I need to add validation..." → no existing ticket
    DecisionRationale,  // "Why did they decide X?" → decision search
    GeneralQuestion     // Fallback
}

/// <summary>Assembled hierarchy context for RAG prompt</summary>
public sealed class NavigatedContext
{
    // Scenario A: Assigned issue
    public JiraDocChunk? CentralIssue { get; set; }
    public JiraDocChunk? ParentEpic { get; set; }
    public List<JiraDocChunk> SiblingIssues { get; set; } = [];
    public List<JiraDocChunk> SubTasks { get; set; } = [];
    public List<JiraDocChunk> DecisionComments { get; set; } = [];
    public List<JiraDocChunk> CrossReferences { get; set; } = [];

    // Scenario B: New requirement
    public string? RequirementText { get; set; }
    public List<JiraDocChunk> RelatedEpics { get; set; } = [];
    public List<JiraDocChunk> RelatedStories { get; set; } = [];
    public List<JiraDocChunk> StandaloneRelatedIssues { get; set; } = [];

    // Scenario C: Decision rationale
    public List<JiraDocChunk> ParentIssues { get; set; } = [];
    public List<JiraDocChunk> ParentEpics { get; set; } = [];

    // Common
    public QueryIntent Intent { get; set; }
    public string AssembledContext { get; set; } = "";

    public static NavigatedContext NotFound(string key) => new()
    {
        Intent = QueryIntent.AssignedIssue,
        AssembledContext = $"No documentation found for issue key: {key}"
    };
}

/// <summary>Ingestion progress report</summary>
public sealed class IngestionProgress
{
    public int IssuesFetched { get; set; }
    public int ChunksCreated { get; set; }
    public int EmbeddingsGenerated { get; set; }
    public int ChunksInserted { get; set; }
    public string Status { get; set; } = "NotStarted";
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
```

### Story 2.5: Shared DTOs (PDDM.Shared — used by BOTH API and UI)

**File**: `src/PDDM.Shared/ApiDtos.cs`

These DTOs are the contract between PDDM.Api and PDDM.UI. The UI project references PDDM.Shared, NOT PDDM.Core.

```csharp
namespace PDDM.Shared;

/// <summary>SSE event types sent from API to UI</summary>
public sealed class SseEvent
{
    public string EventType { get; set; } = "";  // "intent", "token", "done", "error"
    public string Data { get; set; } = "";
}

/// <summary>Intent event (first SSE event)</summary>
public sealed class IntentEvent
{
    public string Intent { get; set; } = "";
}

/// <summary>Token event (streaming)</summary>
public sealed class TokenEvent
{
    public string Token { get; set; } = "";
}

/// <summary>Done event (final SSE event)</summary>
public sealed class DoneEvent
{
    public bool Done { get; set; } = true;
    public List<ContextItem> ContextItems { get; set; } = [];
    public List<string> RetrievedIds { get; set; } = [];
}

/// <summary>Context item shown in UI expandable panel</summary>
public sealed class ContextItem
{
    public string Key { get; set; } = "";
    public string IssueType { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "";
    public int Tier { get; set; }
    public string TierLabel { get; set; } = "";  // "Epic", "Story", "Bug", "Comment"
}

/// <summary>Settings DTO for UI → API communication</summary>
public sealed class LmStudioSettingsDto
{
    public string BaseUrl { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";
    public string ChatModel { get; set; } = "";
    public int EmbeddingDimensions { get; set; } = 768;
    public float ChatTemperature { get; set; } = 0.3f;
    public int ChatMaxTokens { get; set; } = -1;
    public int EmbeddingBatchSize { get; set; } = 50;
}

/// <summary>Ingestion status DTO</summary>
public sealed class IngestionStatusDto
{
    public string Status { get; set; } = "";
    public int TotalChunks { get; set; }
    public int Tier0Count { get; set; }
    public int Tier1Count { get; set; }
    public int Tier2Count { get; set; }
    public int Tier3Count { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>Stats response DTO</summary>
public sealed class StatsResponseDto
{
    public long TotalDocuments { get; set; }
    public int Tier0Count { get; set; }
    public int Tier1Count { get; set; }
    public int Tier2Count { get; set; }
    public int Tier3Count { get; set; }
    public int DecisionCommentCount { get; set; }
    public bool LmStudioConnected { get; set; }
}

/// <summary>Error event (SSE)</summary>
public sealed class ErrorEvent
{
    public string Message { get; set; } = "";
}
```

---

## Epic 3: Core Services (ZVec.NET Verified — API Project Only)

**Goal**: Implement all business logic services using ZVec.NET's actual API. ALL services live in PDDM.Core and are registered in PDDM.Api only.

### Story 3.1: EmbeddingService

**File**: `src/PDDM.Core/Services/EmbeddingService.cs`

Calls LM Studio `/v1/embeddings` to convert text chunks to float[N] vectors. Uses `PddmRuntimeSettings` (explicit mutable singleton — Fix 12) instead of IOptionsMonitor fantasy.

```csharp
namespace PDDM.Core.Services;

public sealed class EmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly PddmRuntimeSettings _runtimeSettings;

    public EmbeddingService(IHttpClientFactory httpClientFactory, PddmRuntimeSettings runtimeSettings)
    {
        _httpClient = httpClientFactory.CreateClient("LmStudio");
        _runtimeSettings = runtimeSettings;
    }

    /// <summary>Embed a single text string. Returns float[768] vector (pinned model).</summary>
    public async Task<ReadOnlyMemory<float>> EmbedSingleAsync(string text, CancellationToken ct = default)
    {
        var results = await EmbedBatchAsync([text], ct);
        return results[0];
    }

    /// <summary>Embed a batch of texts using the currently configured model</summary>
    public async Task<List<ReadOnlyMemory<float>>> EmbedBatchAsync(List<string> texts, CancellationToken ct = default)
    {
        var settings = _runtimeSettings.LmStudio;
        
        var request = new EmbeddingRequest
        {
            Model = settings.EmbeddingModel,
            Input = texts
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/embeddings", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct);
        
        // ⚠️ Fix 7: NO zero-padding. The embedding model MUST output exactly 768 dims
        // (or whatever the ZVec collection is configured for). Mixing embedding spaces
        // is fundamentally wrong. If model dims != collection dims → reject and warn user.
        var actualDims = result!.Data.First().Embedding.Count;
        if (actualDims != settings.EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"Embedding model returned {actualDims}-dim vectors, but collection expects {settings.EmbeddingDimensions}. " +
                "Switching dimensions requires destroying the collection and re-ingesting. " +
                "Use the Settings UI to reset before changing the embedding model.");
        }

        return result.Data
            .OrderBy(d => d.Index)
            .Select(d => new ReadOnlyMemory<float>(d.Embedding.ToArray()))
            .ToList();
    }

    /// <summary>Verify LM Studio is running and models are loaded</summary>
    public async Task<bool> VerifyLmStudioAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/v1/models", ct);
            response.EnsureSuccessStatusCode();
            var models = await response.Content.ReadFromJsonAsync<ModelsResponse>(ct);
            return models!.Data.Any();
        }
        catch { return false; }
    }
}
```

### Story 3.2: DecisionDetector

**File**: `src/PDDM.Core/Services/DecisionDetector.cs`

Same as previous plan — keyword-based detection for `ContainsDecision` flag.

```csharp
namespace PDDM.Core.Services;

public sealed class DecisionDetector
{
    private static readonly string[] DecisionKeywords = [
        "decided", "decision", "agreed", "agree", "approved", "approve",
        "because", "rationale", "reason", "we will", "let's go with",
        "let us go with", "chosen", "chose", "preferred", "preference",
        "after discussion", "conclusion", "resolved to", "determined",
        "we decided", "the approach", "the strategy", "the plan is"
    ];

    private static readonly string[] AutoGeneratedPatterns = [
        "has created a pull request", "Pull request #", "PR #"
    ];

    public bool IsDecisionComment(string body)
    {
        var lower = body.ToLowerInvariant();
        return DecisionKeywords.Any(kw => lower.Contains(kw));
    }

    public bool IsAutoGeneratedComment(string body)
    {
        return AutoGeneratedPatterns.Any(p => body.Contains(p));
    }
}
```

### Story 3.3: JiraFetcherService

**File**: `src/PDDM.Core/Services/JiraFetcherService.cs`

Same as previous plan — fetches from Apache Jira REST API with pagination and rate limiting.

```csharp
namespace PDDM.Core.Services;

public sealed class JiraFetcherService
{
    private readonly HttpClient _httpClient;
    private readonly PddmSettings _settings;
    private readonly DecisionDetector _decisionDetector;

    public JiraFetcherService(IHttpClientFactory httpClientFactory,
                               IOptionsMonitor<PddmSettings> settings,
                               DecisionDetector decisionDetector)
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(settings.CurrentValue.Jira.BaseUrl) };
        _settings = settings.CurrentValue;
        _decisionDetector = decisionDetector;
    }

    public async Task<List<JiraIssue>> FetchByTypeAsync(string issueType, int maxTotal, CancellationToken ct = default)
    {
        var jql = $"project={_settings.Jira.ProjectKey} AND issuetype={issueType}";
        return await FetchPaginatedAsync(jql, maxTotal, ct);
    }

    public async Task<List<JiraIssue>> FetchEpicChildrenAsync(string epicKey, CancellationToken ct = default)
    {
        var jql = $"project={_settings.Jira.ProjectKey} AND \"Epic Link\"={epicKey}";
        return await FetchPaginatedAsync(jql, 100, ct);
    }

    public async Task<JiraIssue?> FetchSingleAsync(string issueKey, CancellationToken ct = default)
    {
        var url = $"issue/{issueKey}?expand=comments";
        var response = await _httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<JiraIssue>(ct);
    }

    private async Task<List<JiraIssue>> FetchPaginatedAsync(string jql, int maxTotal, CancellationToken ct = default)
    {
        var allIssues = new List<JiraIssue>();
        var startAt = 0;
        var maxResults = _settings.Jira.MaxResultsPerRequest;

        while (startAt < maxTotal)
        {
            var encodedJql = Uri.EscapeDataString(jql);
            var url = $"search?jql={encodedJql}&startAt={startAt}&maxResults={maxResults}&expand=comments";
            
            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            
            var result = await response.Content.ReadFromJsonAsync<JiraSearchResult>(ct);
            if (result == null || result.Issues.Count == 0) break;
            
            allIssues.AddRange(result.Issues);
            startAt += maxResults;
            
            await Task.Delay(_settings.Jira.RequestDelayMs, ct);
        }

        return allIssues;
    }
}
```

### Story 3.4: ChunkingService

**File**: `src/PDDM.Core/Services/ChunkingService.cs`

**⚠️ BUG FIXES (from review)**:
- **Fix 2**: `DetermineParentKey` was a stub returning `""`. Sub-tasks never got ParentKey → DOWN navigation empty. Now: use `JiraIssue.Fields.Parent?.Key` for sub-tasks.
- **Fix 8**: `MaxCommentsPerIssue` and `MinCommentsForIssue` are in config but not wired in code. Now: applied in chunking loop.
- **Fix 9**: Comment embedding text was using generic `ComposeEmbeddingText` (empty Summary → weak retrieval). Now: restored POC-style comment embedding: `"On {ParentKey}: {Author} said: {Body}"`.
- **Fix 11**: No `UmbrellaLink` field for UP-UP navigation. Now: `JiraDocChunk` has `UmbrellaLink` field populated from `issuelinks`.

```csharp
namespace PDDM.Core.Services;

public sealed class ChunkingService
{
    private readonly DecisionDetector _decisionDetector;
    private readonly PddmRuntimeSettings _runtimeSettings;

    public ChunkingService(DecisionDetector decisionDetector, PddmRuntimeSettings runtimeSettings)
    {
        _decisionDetector = decisionDetector;
        _runtimeSettings = runtimeSettings;
    }

    public List<JiraDocChunk> CreateChunks(List<JiraIssue> issues)
    {
        var chunks = new List<JiraDocChunk>();
        var ingestion = _runtimeSettings.Ingestion;

        foreach (var issue in issues)
        {
            var issueType = issue.Fields.Issuetype?.Name ?? "Unknown";
            var epicLink = issue.Fields.Customfield_12311120 ?? "";
            
            // ⚠️ Fix 11: Extract UmbrellaLink from issuelinks
            // Umbrella→Epic relationship is via issuelinks (not a custom field)
            var umbrellaLink = ExtractUmbrellaLink(issue);
            
            var components = issue.Fields.Components.Select(c => c.Name).ConcatWithSeparator(";");
            var labels = string.Join(";", issue.Fields.Labels);
            var fixVersions = issue.Fields.FixVersions.Select(v => v.Name).ConcatWithSeparator(";");
            var status = issue.Fields.Status?.Name ?? "Unknown";
            var priority = issue.Fields.Priority?.Name ?? "Unknown";
            var assignee = issue.Fields.Assignee?.DisplayName ?? "Unassigned";
            var summary = issue.Fields.Summary;
            var description = issue.Fields.Description ?? "";

            var tier = DetermineTier(issueType);
            // ⚠️ Fix 2: DetermineParentKey is no longer a stub
            var parentKey = DetermineParentKey(issue, tier);

            var issueChunk = new JiraDocChunk
            {
                Id = FormatChunkId(issueType, issue.Key),
                Tier = tier,
                IssueType = issueType,
                Key = issue.Key,
                EpicLink = tier >= 1 ? epicLink : "",
                UmbrellaLink = umbrellaLink,  // ⚠️ Fix 11
                ParentKey = parentKey,
                Summary = summary,
                Description = description,
                Status = status,
                Components = components,
                Labels = labels,
                FixVersions = fixVersions,
                Priority = priority,
                Assignee = assignee,
                CommentAuthor = "",
                ContainsDecision = false
            };

            chunks.Add(issueChunk);

            // Create Tier 3 chunks (comments)
            var comments = issue.Fields.Comment?.Comments ?? [];
            
            // ⚠️ Fix 8: Apply MinCommentsForIssue — skip issues with too few comments
            // (Only relevant for filtering at fetch level; we still process what we receive)
            
            foreach (var (comment, idx) in comments.Select((c, i) => (c, i)))
            {
                // ⚠️ Fix 8: Apply MaxCommentsPerIssue — limit comments per issue
                if (idx >= ingestion.MaxCommentsPerIssue) break;
                
                if (_decisionDetector.IsAutoGeneratedComment(comment.Body)) continue;

                var isDecision = _decisionDetector.IsDecisionComment(comment.Body);

                var commentChunk = new JiraDocChunk
                {
                    Id = $"comment_{issue.Key}_{idx}",
                    Tier = 3,
                    IssueType = "Comment",
                    Key = issue.Key,
                    EpicLink = epicLink,
                    UmbrellaLink = umbrellaLink,  // Inherited from parent
                    ParentKey = issue.Key,
                    Summary = "",
                    Description = comment.Body,
                    Status = "",
                    Components = components,
                    Labels = labels,
                    FixVersions = fixVersions,
                    Priority = "",
                    Assignee = "",
                    CommentAuthor = comment.Author?.DisplayName ?? "Unknown",
                    ContainsDecision = isDecision
                };

                chunks.Add(commentChunk);
            }
        }

        return chunks;
    }

    // ⚠️ Fix 2: No longer a stub — set ParentKey for sub-tasks
    private string DetermineParentKey(JiraIssue issue, int tier)
    {
        if (tier == 2 && issue.Fields.Issuetype?.Subtask == true)
        {
            // Sub-tasks have a parent field in Jira REST API
            // We need to add this to the DTO (see Story 2.2)
            return issue.Fields.Parent?.Key ?? "";
        }
        return "";
    }

    // ⚠️ Fix 11: Extract Umbrella link from issuelinks
    private string ExtractUmbrellaLink(JiraIssue issue)
    {
        // Umbrella→Epic is modeled via issuelinks in Apache Jira
        // Look for outward links where the target is an Umbrella type
        foreach (var link in issue.Fields.Issuelinks ?? [])
        {
            var outward = link.OutwardIssue;
            if (outward?.Issuetype?.Name == "Umbrella")
                return outward.Key;
            var inward = link.InwardIssue;
            if (inward?.Issuetype?.Name == "Umbrella")
                return inward.Key;
        }
        return "";
    }

    private int DetermineTier(string issueType) => issueType switch
    {
        "Epic" => 0, "Umbrella" => 0, "Story" => 1, "Bug" => 1,
        "Improvement" => 1, "Task" => 1, "New Feature" => 1,
        "Sub-task" => 2, _ => 1
    };

    private string FormatChunkId(string issueType, string key) => issueType switch
    {
        "Epic" => $"epic_{key}", "Umbrella" => $"umbrella_{key}",
        "Story" => $"story_{key}", "Bug" => $"bug_{key}",
        "Improvement" => $"improvement_{key}", "Task" => $"task_{key}",
        "New Feature" => $"feature_{key}", "Sub-task" => $"subtask_{key}",
        _ => $"issue_{key}"
    };

    /// <summary>Compose embedding text per tier (sent to LM Studio, NOT stored in ZVec)</summary>
    public string ComposeEmbeddingText(JiraDocChunk chunk) => chunk.Tier switch
    {
        0 => $"{chunk.Summary}: {chunk.Description}",  // Epic — business intent
        1 => $"{chunk.Key}: {chunk.Summary}\n{chunk.Description}\nType: {chunk.IssueType}",
        2 => $"{chunk.Key}: {chunk.Summary}\n{chunk.Description}",  // Sub-task
        // ⚠️ Fix 9: Comment embedding uses POC-style composition with author + parent context
        3 => $"On {chunk.ParentKey}: {chunk.CommentAuthor} said: {chunk.Description}",
        _ => $"{chunk.Key}: {chunk.Summary}\n{chunk.Description}"
    };

    private string ConcatWithSeparator(this IEnumerable<string> items, string sep) =>
        string.Join(sep, items);
}
```

**Note**: `ConcatWithSeparator` extension method needed — add to `src/PDDM.Core/Extensions/StringExtensions.cs`.

### Story 3.5: VectorStoreService

**File**: `src/PDDM.Core/Services/VectorStoreService.cs`

Same as previous plan — wraps ZVec.NET typed collection operations.

```csharp
namespace PDDM.Core.Services;

public sealed class VectorStoreService
{
    private readonly IZvecCollection<JiraDocChunk> _collection;

    public VectorStoreService(IZvecCollection<JiraDocChunk> collection)
    {
        _collection = collection;
    }

    public ZVecStatus InsertBatch(IReadOnlyList<JiraDocChunk> chunks)
    {
        return _collection.Insert(chunks);
    }

    public JiraDocChunk? FetchByKey(string key, bool includeVector = false)
    {
        var doc = _collection.Fetch(key, includeVector);
        if (doc != null) return doc;

        var possibleIds = GeneratePossibleIds(key);
        foreach (var id in possibleIds)
        {
            doc = _collection.Fetch(id, includeVector);
            if (doc != null) return doc;
        }
        return null;
    }

    public IReadOnlyList<ZVecQueryResult<JiraDocChunk>> QueryWithFilter(
        ReadOnlyMemory<float> queryVector,
        int topK,
        Expression<Func<JiraDocChunk, bool>>? filter = null,
        bool includeVector = false)
    {
        return _collection.Query(
            p => p.Embedding,
            queryVector,
            topK: topK,
            filter: filter,
            includeVector: includeVector
        );
    }

    public IReadOnlyList<ZVecQueryResult<JiraDocChunk>> QueryNoFilter(
        ReadOnlyMemory<float> queryVector,
        int topK,
        bool includeVector = false)
    {
        return _collection.Query(
            p => p.Embedding,
            queryVector,
            topK: topK,
            includeVector: includeVector
        );
    }

    public ZVecCollectionStats GetStats() => _collection.Stats;
    public void Optimize() => _collection.Optimize();

    private static List<string> GeneratePossibleIds(string key) => [
        key, $"epic_{key}", $"umbrella_{key}", $"story_{key}",
        $"bug_{key}", $"improvement_{key}", $"task_{key}",
        $"feature_{key}", $"subtask_{key}"
    ];
}
```

### Story 3.6: HybridIndexService (In-Memory Navigation Index)

**File**: `src/PDDM.Core/Services/HybridIndexService.cs`

Same as previous plan — in-memory dictionary for hierarchy navigation.

```csharp
namespace PDDM.Core.Services;

/// <summary>
/// In-memory navigation CACHE for hierarchy lookups.
/// ZVec.NET's typed Query requires a vector (similarity search).
/// For "find all children of Epic X" (filter-only), this provides O(1) lookups.
/// 
/// ⚠️ BUG FIX (from review): Previous version indexed by chunk.Key,
/// but Comment chunks also set Key = parent issue key, causing overwrite.
/// Now: Primary index by chunk.Id (globally unique), secondary by Jira Key
/// (Tier 0/1/2 only — never Tier 3 comments).
/// 
/// ⚠️ RESTART SAFETY: This is a CACHE of ZVec.NET data, rebuilt on startup
/// from ZVec using filter-only queries (see RebuildFromZVec method).
/// After API restart, navigation is NOT broken — HybridIndex hydrates from ZVec.
/// </summary>
public sealed class HybridIndexService
{
    // Primary: chunk.Id → JiraDocChunk (ALL tiers, globally unique)
    // e.g. "epic_SPARK-56664", "comment_SPARK-8469_0"
    private readonly ConcurrentDictionary<string, JiraDocChunk> _byId = new();
    
    // Secondary: Jira issue Key → JiraDocChunk (Tier 0/1/2 ONLY)
    // e.g. "SPARK-56664" → epic chunk, "SPARK-8469" → bug chunk
    // Comments NEVER overwrite this — they're excluded
    private readonly ConcurrentDictionary<string, JiraDocChunk> _byJiraKey = new();
    
    // Navigation: EpicLink → children (all tiers with that EpicLink)
    private readonly ConcurrentDictionary<string, ConcurrentBag<JiraDocChunk>> _byEpicLink = new();
    
    // Navigation: ParentKey → children (sub-tasks + comments under an issue)
    private readonly ConcurrentDictionary<string, ConcurrentBag<JiraDocChunk>> _byParentKey = new();
    
    // Navigation: Tier → all chunks at that tier level
    private readonly ConcurrentDictionary<int, ConcurrentBag<JiraDocChunk>> _byTier = new();

    public void Add(JiraDocChunk chunk)
    {
        // Always index by unique Id (never overwritten)
        _byId[chunk.Id] = chunk;
        
        // Index by Jira Key ONLY for Tier 0/1/2 (issues, not comments)
        // This prevents comment chunks overwriting issue chunks
        if (chunk.Tier <= 2)
            _byJiraKey[chunk.Key] = chunk;
        
        if (!string.IsNullOrEmpty(chunk.EpicLink))
            _byEpicLink.AddOrUpdate(chunk.EpicLink, new ConcurrentBag<JiraDocChunk> { chunk },
                (_, bag) => { bag.Add(chunk); return bag; });
        
        if (!string.IsNullOrEmpty(chunk.ParentKey))
            _byParentKey.AddOrUpdate(chunk.ParentKey, new ConcurrentBag<JiraDocChunk> { chunk },
                (_, bag) => { bag.Add(chunk); return bag; });
        
        _byTier.AddOrUpdate(chunk.Tier, new ConcurrentBag<JiraDocChunk> { chunk },
            (_, bag) => { bag.Add(chunk); return bag; });
    }

    public void AddRange(IEnumerable<JiraDocChunk> chunks) { foreach (var c in chunks) Add(c); }
    
    /// <summary>Get by unique chunk Id (any tier)</summary>
    public JiraDocChunk? GetById(string id) => _byId.TryGetValue(id, out var c) ? c : null;
    
    /// <summary>Get by Jira issue key (Tier 0/1/2 only — never returns a comment)</summary>
    public JiraDocChunk? GetByKey(string jiraKey) => _byJiraKey.TryGetValue(jiraKey, out var c) ? c : null;
    
    public List<JiraDocChunk> GetByEpicLink(string epicLink) => _byEpicLink.TryGetValue(epicLink, out var bag) ? bag.ToList() : [];
    public List<JiraDocChunk> GetByParentKey(string parentKey) => _byParentKey.TryGetValue(parentKey, out var bag) ? bag.ToList() : [];
    public List<JiraDocChunk> GetByTier(int tier) => _byTier.TryGetValue(tier, out var bag) ? bag.ToList() : [];
    public void Clear() { _byId.Clear(); _byJiraKey.Clear(); _byEpicLink.Clear(); _byParentKey.Clear(); _byTier.Clear(); }
    public int TotalCount => _byId.Count;

    /// <summary>
    /// ⚠️ RESTART SAFETY: Rebuild HybridIndex from ZVec.NET collection.
    /// Uses untyped filter-only queries (high topK, filter by Tier) to hydrate
    /// all chunks back into memory. Called on startup after ZVec opens.
    /// 
    /// This makes HybridIndex a CACHE (not a second DB) — it can always be
    /// reconstructed from the authoritative ZVec.NET store.
    /// </summary>
    public void RebuildFromZVec(IZvecCollection<JiraDocChunk> collection)
    {
        Clear();
        // Strategy: Query each tier separately with a filter + high topK
        // Since typed Query requires a vector, we use a dummy zero-vector
        // + tier filter. This is a startup-only operation, not performance-critical.
        var dummyVector = new ReadOnlyMemory<float>(new float[768]);
        
        for (int tier = 0; tier <= 3; tier++)
        {
            var hits = collection.Query(
                p => p.Embedding,
                dummyVector,
                topK: 20000,  // High topK to get all chunks at this tier
                filter: p => p.Tier == tier,
                includeVector: false
            );
            
            foreach (var hit in hits)
            {
                Add(hit.Record);  // hit.Record or hit.Hit — VERIFY at compile time
            }
        }
    }
}
```

### Story 3.7: IntentClassifier (heuristic) + HybridIntentClassifier

**Files**:
- `src/PDDM.Core/Services/IntentClassifier.cs` — sync heuristic fast path
- `src/PDDM.Core/Services/HybridIntentClassifier.cs` — `IIntentClassifier` implementation used in DI

**⚠️ BUG FIX (from review)**: Previous version used boolean `Regex.IsMatch` for issue key detection, which matched ANY string containing PROJ-N pattern (even "unlike SPARK-1"). Also, `GeneralQuestion` had no real navigation path.

**Fixes applied**:
- Extract issue key via `Regex.Match` with capture group, validate it's the FOCUS of the query (not negated)
- `GeneralQuestion` now routes to `NewRequirement` navigation (semantic search) — this is the sensible fallback
- Ordered priority: explicit key → decision keywords → requirement keywords → general (semantic search)

**Current behavior (post hot-path upgrade)**:
- Heuristic hits (key / decision / requirement phrases) skip the LLM — keeps Q1–Q3 deterministic and fast
- When heuristic returns `GeneralQuestion`, `ClassifyAsync` calls a tiny non-streaming JSON completion; timeout/failure → `GeneralQuestion`
- Chat classifies **once** via `ClassifyAsync`, then `NavigateAsync(userInput, intent)` — no second classify inside the engine
- `BuildSystemPrompt(intent)` / `BuildUserPrompt(context, question, intent)` inject `SCENARIO` so the answer model matches retrieval

```csharp
namespace PDDM.Core.Services;

public sealed class IntentClassifier
{
    // Ordered rules: most specific first
    public QueryIntent Classify(string userInput)
    {
        // Rule 1: EXPLICIT assigned issue — user mentions a key as THE topic
        // e.g. "I got assigned SPARK-57337", "help me with SPARK-56664"
        // NOT: "unlike SPARK-1", "similar to SPARK-8469 but different"
        var keyMatch = Regex.Match(userInput, @"(?<!\bunlike\s)(?<!\bsimilar\s to\s)(?<!\bnot\s like\s)[A-Z]+-\d+", RegexOptions.IgnoreCase);
        if (keyMatch.Success)
        {
            // Check if the key is the subject, not a comparison reference
            // Simple heuristic: key appears in first half of input, or input is short
            var keyPosition = keyMatch.Index;
            if (keyPosition < userInput.Length / 2 || userInput.Length < 30)
                return QueryIntent.AssignedIssue;
        }

        // Rule 2: Decision/rationale question — specific question patterns
        var lower = userInput.ToLowerInvariant();
        if (lower.ContainsAny("why did they", "what was the decision", "why was",
                               "rationale for", "reason for choosing", "how did they decide",
                               "what's the reasoning", "why did we choose", "decision behind"))
            return QueryIntent.DecisionRationale;

        // Rule 3: New requirement / action intent
        if (lower.ContainsAny("i need to", "i want to add", "we should add",
                               "i have to implement", "add validation",
                               "when x", "should update", "new feature",
                               "requirement", "i'm working on", "how to implement"))
            return QueryIntent.NewRequirement;

        // Rule 4: General question — route to semantic search (Scenario B)
        // This is the sensible fallback: any question gets vector-searched
        return QueryIntent.GeneralQuestion;
    }

    /// <summary>Extract the Jira issue key from input (for Scenario A)</summary>
    public string? ExtractIssueKey(string userInput)
    {
        var match = Regex.Match(userInput, @"[A-Z]+-\d+", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static bool ContainsAny(this string source, params string[] keywords)
        => keywords.Any(kw => source.Contains(kw));
}
```

### Story 3.8: NavigationEngine (THE CORE)

**File**: `src/PDDM.Core/Services/NavigationEngine.cs`

**⚠️ BUG FIXES (from review)**:
- **Fix 3**: CROSS navigation used `issueChunk.Embedding.Length > 0` but embedding is empty after `includeVector: false` fetch. Now: embed `Summary + Description` on-the-fly for CROSS query (preferred — avoids storing vectors in HybridIndex).
- **Fix 10**: `GeneralQuestion` now routes to `NavigateFromNewRequirement` (semantic search). This is the sensible fallback.
- **Fix 1**: Uses `_hybridIndex.GetByKey()` which now only returns Tier 0/1/2 (never a comment that could overwrite).

```csharp
namespace PDDM.Core.Services;

public sealed class NavigationEngine
{
    private readonly VectorStoreService _vectorStore;
    private readonly HybridIndexService _hybridIndex;
    private readonly EmbeddingService _embeddingService;
    private readonly IntentClassifier _intentClassifier;

    public NavigationEngine(
        VectorStoreService vectorStore,
        HybridIndexService hybridIndex,
        EmbeddingService embeddingService,
        IntentClassifier intentClassifier)
    {
        _vectorStore = vectorStore;
        _hybridIndex = hybridIndex;
        _embeddingService = embeddingService;
        _intentClassifier = intentClassifier;
    }

    // ── Scenario A: Assigned Issue ──
    public async Task<NavigatedContext> NavigateFromAssignedIssue(string issueKey)
    {
        var context = new NavigatedContext { Intent = QueryIntent.AssignedIssue };

        // GetByKey returns Tier 0/1/2 only (comments never overwrite)
        var issueChunk = _hybridIndex.GetByKey(issueKey);
        if (issueChunk == null)
        {
            issueChunk = await _vectorStore.FetchByKeyAsync(issueKey, includeVector: false);
            if (issueChunk == null) return NavigatedContext.NotFound(issueKey);
        }
        context.CentralIssue = issueChunk;

        // UP: parent Epic
        if (!string.IsNullOrEmpty(issueChunk.EpicLink))
            context.ParentEpic = _hybridIndex.GetByKey(issueChunk.EpicLink);

        // SIDE: siblings
        if (!string.IsNullOrEmpty(issueChunk.EpicLink))
            context.SiblingIssues = _hybridIndex.GetByEpicLink(issueChunk.EpicLink)
                .Where(c => c.Tier == 1 && c.Key != issueKey).ToList();

        // DOWN: sub-tasks (requires ParentKey — Fix 2 ensures this is set)
        context.SubTasks = _hybridIndex.GetByParentKey(issueKey).Where(c => c.Tier == 2).ToList();

        // DOWN: decision comments
        context.DecisionComments = _hybridIndex.GetByParentKey(issueKey)
            .Where(c => c.Tier == 3 && c.ContainsDecision).ToList();

        // CROSS: related issues
        // ⚠️ Fix 3: issueChunk.Embedding is empty (fetched with includeVector:false)
        // Instead of relying on stored vector, embed Summary+Description on-the-fly.
        // This also works when HybridIndex data was rebuilt from ZVec (no vectors).
        var crossEmbedText = $"{issueChunk.Summary}: {issueChunk.Description}";
        var crossVec = await _embeddingService.EmbedSingleAsync(crossEmbedText);
        var crossHits = _vectorStore.QueryWithFilter(
            crossVec, topK: 5,
            filter: p => p.Key != issueKey && p.Tier == 1,
            includeVector: false);
        context.CrossReferences = crossHits.Select(h => h.Record).ToList();

        return context;
    }

    // ── Scenario B: New Requirement ──
    // Also used as fallback for GeneralQuestion (semantic search is the sensible default)
    public async Task<NavigatedContext> NavigateFromNewRequirement(string requirementText)
    {
        var context = new NavigatedContext
        {
            Intent = QueryIntent.NewRequirement,
            RequirementText = requirementText
        };

        var requirementVec = await _embeddingService.EmbedSingleAsync(requirementText);
        var allHits = _vectorStore.QueryNoFilter(requirementVec, topK: 20, includeVector: false);

        // Cluster by EpicLink (client-side grouping — QueryGroupBy NOT supported)
        var clusters = allHits
            .Where(h => !string.IsNullOrEmpty(h.Record.EpicLink))
            .GroupBy(h => h.Record.EpicLink)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .ToList();

        foreach (var cluster in clusters)
        {
            var epic = _hybridIndex.GetByKey(cluster.Key);
            if (epic != null)
            {
                context.RelatedEpics.Add(epic);
                context.RelatedStories.AddRange(
                    _hybridIndex.GetByEpicLink(cluster.Key).Where(c => c.Tier == 1));
            }
            context.DecisionComments.AddRange(
                cluster.Where(h => h.Record.Tier == 3 && h.Record.ContainsDecision)
                    .Select(h => h.Record).Take(3));
        }

        context.StandaloneRelatedIssues = allHits
            .Where(h => string.IsNullOrEmpty(h.Record.EpicLink) && h.Record.Tier == 1)
            .Select(h => h.Record).Take(5).ToList();

        return context;
    }

    // ── Scenario C: Decision Rationale ──
    public async Task<NavigatedContext> NavigateFromDecisionQuestion(string question)
    {
        var context = new NavigatedContext { Intent = QueryIntent.DecisionRationale };

        var questionVec = await _embeddingService.EmbedSingleAsync(question);
        var decisionHits = _vectorStore.QueryWithFilter(
            questionVec, topK: 10,
            filter: p => p.ContainsDecision == true && p.Tier == 3,
            includeVector: false);

        foreach (var hit in decisionHits.Take(5))
        {
            var comment = hit.Record;
            var parentIssue = _hybridIndex.GetByKey(comment.ParentKey);
            if (parentIssue != null) context.ParentIssues.Add(parentIssue);

            if (!string.IsNullOrEmpty(comment.EpicLink))
            {
                var epic = _hybridIndex.GetByKey(comment.EpicLink);
                if (epic != null && !context.ParentEpics.Any(e => e.Key == epic.Key))
                    context.ParentEpics.Add(epic);
            }
            context.DecisionComments.Add(comment);
        }

        return context;
    }

    // ── Unified entry point (intent pre-classified once in ChatController) ──
    public async Task<NavigatedContext> NavigateAsync(string userInput, QueryIntent intent)
    {
        // Do NOT re-classify here — hybrid ClassifyAsync already ran upstream.
        return intent switch
        {
            QueryIntent.AssignedIssue => await NavigateFromAssignedIssue(
                _intentClassifier.ExtractIssueKey(userInput)!),
            QueryIntent.NewRequirement => await NavigateFromNewRequirement(userInput),
            QueryIntent.DecisionRationale => await NavigateFromDecisionQuestion(userInput),
            // ⚠️ Fix 10: GeneralQuestion routes to semantic search (Scenario B)
            QueryIntent.GeneralQuestion => await NavigateFromNewRequirement(userInput),
            _ => await NavigateFromNewRequirement(userInput)
        };
    }
}
```

### Story 3.9: ContextBuilderService

**File**: `src/PDDM.Core/Services/ContextBuilderService.cs`

Same as previous plan — assembles RAG context string per scenario.

```csharp
namespace PDDM.Core.Services;

public sealed class ContextBuilderService
{
    public string BuildContext(NavigatedContext nav)
    {
        var sb = new StringBuilder();
        switch (nav.Intent)
        {
            case QueryIntent.AssignedIssue: BuildAssignedIssueContext(sb, nav); break;
            case QueryIntent.NewRequirement: BuildNewRequirementContext(sb, nav); break;
            case QueryIntent.DecisionRationale: BuildDecisionContext(sb, nav); break;
            default: BuildNewRequirementContext(sb, nav); break;
        }
        return sb.ToString();
    }

    private void BuildAssignedIssueContext(StringBuilder sb, NavigatedContext nav)
    {
        if (nav.ParentEpic != null)
        {
            sb.AppendLine("EPIC: " + FormatChunk(nav.ParentEpic));
            sb.AppendLine();
        }
        if (nav.CentralIssue != null)
        {
            sb.AppendLine("YOUR ISSUE: " + FormatChunk(nav.CentralIssue));
            sb.AppendLine();
        }
        if (nav.SiblingIssues.Count > 0)
        {
            sb.AppendLine("SIBLING WORK under this Epic:");
            foreach (var s in nav.SiblingIssues)
                sb.AppendLine($"  {s.IssueType}: {s.Summary} ({s.Key}) -- {s.Status}");
            sb.AppendLine();
        }
        if (nav.DecisionComments.Count > 0)
        {
            sb.AppendLine("KEY DECISIONS:");
            foreach (var c in nav.DecisionComments)
                sb.AppendLine($"  {c.CommentAuthor} on {c.ParentKey}: {Truncate(c.Description, 200)}");
            sb.AppendLine();
        }
        if (nav.CrossReferences.Count > 0)
        {
            sb.AppendLine("RELATED ISSUES:");
            foreach (var r in nav.CrossReferences.Take(3))
                sb.AppendLine($"  {r.IssueType}: {r.Summary} ({r.Key}) -- {r.Status}");
        }
    }

    private void BuildNewRequirementContext(StringBuilder sb, NavigatedContext nav)
    {
        sb.AppendLine("No exact match found. Here is the relevant landscape:");
        sb.AppendLine();

        foreach (var epic in nav.RelatedEpics)
        {
            sb.AppendLine("EPIC: " + FormatChunk(epic));
            sb.AppendLine();
        }
        if (nav.RelatedStories.Count > 0)
        {
            sb.AppendLine("RELATED WORK:");
            foreach (var s in nav.RelatedStories.Take(10))
                sb.AppendLine($"  {s.IssueType}: {s.Summary} ({s.Key}) -- {s.Status}");
            sb.AppendLine();
        }
        if (nav.StandaloneRelatedIssues.Count > 0)
        {
            sb.AppendLine("OTHER RELEVANT ISSUES:");
            foreach (var i in nav.StandaloneRelatedIssues.Take(5))
                sb.AppendLine($"  {i.IssueType}: {i.Summary} ({i.Key}) -- {i.Status}");
            sb.AppendLine();
        }
        if (nav.DecisionComments.Count > 0)
        {
            sb.AppendLine("RELEVANT DECISIONS:");
            foreach (var c in nav.DecisionComments)
                sb.AppendLine($"  {c.CommentAuthor}: {Truncate(c.Description, 200)}");
        }
    }

    private void BuildDecisionContext(StringBuilder sb, NavigatedContext nav)
    {
        foreach (var epic in nav.ParentEpics)
        {
            sb.AppendLine("EPIC: " + FormatChunk(epic));
            sb.AppendLine();
        }
        foreach (var issue in nav.ParentIssues)
        {
            sb.AppendLine("CONTEXT: " + FormatChunk(issue));
            sb.AppendLine();
        }
        sb.AppendLine("DECISION COMMENTS:");
        foreach (var c in nav.DecisionComments)
        {
            sb.AppendLine($"  {c.CommentAuthor} on {c.ParentKey}:");
            sb.AppendLine($"  {c.Description}");
            sb.AppendLine();
        }
    }

    private string FormatChunk(JiraDocChunk c) =>
        $"{c.Key}: {c.Summary} | Type: {c.IssueType} | Status: {c.Status} | Component: {c.Components}";

    private string Truncate(string t, int max) => t.Length <= max ? t : t[..max] + "...";
}
```

### Story 3.10: ChatService (SSE Streaming)

**File**: `src/PDDM.Core/Services/ChatService.cs`

**KEY CHANGE**: ChatService now streams LLM responses via `IAsyncEnumerable<string>` instead of returning a single string. This enables SSE streaming from API → UI.

**Prompts (current):** live in `PddmDefaults` — `BuildSystemPrompt(QueryIntent)` appends a single scenario structure rule; `BuildUserPrompt(context, question, intent)` includes `SCENARIO: …`. `CompleteAsync` is non-streaming (used by hybrid intent classify).

```csharp
namespace PDDM.Core.Services;

public sealed class ChatService
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<PddmSettings> _settingsMonitor;

    private const string SystemPrompt = @"You are a Project Docs Navigator for the Projects Docs Deep Mind (PDDM) system. Your job is to guide the developer through project documentation hierarchy.

For assigned issues: Show the business context (Epic), the scope (sibling stories), and any relevant decisions or open items.

For new requirements: Show the landscape of related existing work, suggest which Epics/Stories are most relevant, and note what patterns or decisions from similar work might inform their approach.

For decision questions: Show the decision rationale from comments, with the surrounding context of what triggered the discussion.

Always structure your response with clear sections. Use the hierarchy information provided to navigate the developer through:
- What's ABOVE them (business context, Epic/Umbrella)
- What's BESIDE them (scope, sibling stories, related bugs)
- What's BELOW them (details, sub-tasks, decision comments)

Keep responses concise but informative. If the context doesn't fully answer the question, acknowledge what's missing.";

    public ChatService(IHttpClientFactory httpClientFactory, IOptionsMonitor<PddmSettings> settingsMonitor)
    {
        _httpClient = httpClientFactory.CreateClient("LmStudio");
        _settingsMonitor = settingsMonitor;
    }

    /// <summary>
    /// Stream RAG response token-by-token from LM Studio.
    /// Uses stream=true in chat/completions request.
    /// Each yielded string is a single token from the LLM.
    /// </summary>
    public async IAsyncEnumerable<string> StreamRagResponseAsync(
        string assembledContext,
        string userQuestion,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var settings = _settingsMonitor.CurrentValue.LmStudio;

        var request = new ChatCompletionRequest
        {
            Model = settings.ChatModel,
            Messages = [
                new ChatMessage { Role = "system", Content = SystemPrompt },
                new ChatMessage { Role = "user", Content = $"{assembledContext}\n\n---\n\nQuestion: {userQuestion}" }
            ],
            Temperature = settings.ChatTemperature,
            MaxTokens = settings.ChatMaxTokens,
            Stream = true  // KEY: Enable streaming
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/chat/completions", request, ct);
        response.EnsureSuccessStatusCode();

        // Read the SSE stream from LM Studio
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            
            if (line == null) break;
            if (line.StartsWith("data: "))
            {
                var json = line["data: ".Length..];
                if (json == "[DONE]") break;

                var chunk = JsonSerializer.Deserialize<ChatStreamChunk>(json);
                if (chunk?.Choices?.FirstOrDefault()?.Delta?.Content != null)
                {
                    yield return chunk.Choices[0].Delta.Content;
                }
            }
        }
    }

    /// <summary>Non-streaming fallback (for debugging or when SSE is not needed)</summary>
    public async Task<string> GetRagResponseAsync(string assembledContext, string userQuestion, CancellationToken ct = default)
    {
        var settings = _settingsMonitor.CurrentValue.LmStudio;
        var request = new ChatCompletionRequest
        {
            Model = settings.ChatModel,
            Messages = [
                new ChatMessage { Role = "system", Content = SystemPrompt },
                new ChatMessage { Role = "user", Content = $"{assembledContext}\n\n---\n\nQuestion: {userQuestion}" }
            ],
            Temperature = settings.ChatTemperature,
            MaxTokens = settings.ChatMaxTokens,
            Stream = false
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/chat/completions", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(ct);
        return result!.Choices[0].Message.Content;
    }
}
```

### Story 3.11: IngestionOrchestrator

**File**: `src/PDDM.Core/Services/IngestionOrchestrator.cs`

Same as previous plan but uses `IOptionsMonitor<PddmSettings>` for runtime config.

```csharp
namespace PDDM.Core.Services;

public sealed class IngestionOrchestrator
{
    private readonly JiraFetcherService _jiraFetcher;
    private readonly ChunkingService _chunkingService;
    private readonly EmbeddingService _embeddingService;
    private readonly VectorStoreService _vectorStore;
    private readonly HybridIndexService _hybridIndex;
    private readonly IOptionsMonitor<PddmSettings> _settingsMonitor;

    public IngestionProgress Progress { get; } = new();

    public IngestionOrchestrator(
        JiraFetcherService jiraFetcher,
        ChunkingService chunkingService,
        EmbeddingService embeddingService,
        VectorStoreService vectorStore,
        HybridIndexService hybridIndex,
        IOptionsMonitor<PddmSettings> settingsMonitor)
    {
        _jiraFetcher = jiraFetcher;
        _chunkingService = chunkingService;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _hybridIndex = hybridIndex;
        _settingsMonitor = settingsMonitor;
    }

    public async Task<IngestionProgress> RunIngestionAsync(CancellationToken ct = default)
    {
        Progress.Status = "Fetching";
        Progress.StartedAt = DateTime.UtcNow;
        var settings = _settingsMonitor.CurrentValue;

        try
        {
            // Phase 1: Fetch from Jira
            var allIssues = new List<JiraIssue>();
            allIssues.AddRange(await _jiraFetcher.FetchByTypeAsync("Epic", settings.Ingestion.MaxEpics, ct));
            allIssues.AddRange(await _jiraFetcher.FetchByTypeAsync("Story", settings.Ingestion.MaxStories, ct));
            allIssues.AddRange(await _jiraFetcher.FetchByTypeAsync("Umbrella", settings.Ingestion.MaxUmbrellas, ct));
            allIssues.AddRange(await _jiraFetcher.FetchByTypeAsync("Bug", settings.Ingestion.MaxBugs, ct));
            allIssues.AddRange(await _jiraFetcher.FetchByTypeAsync("Improvement", settings.Ingestion.MaxImprovements, ct));
            allIssues.AddRange(await _jiraFetcher.FetchByTypeAsync("Task", settings.Ingestion.MaxTasks, ct));
            Progress.IssuesFetched = allIssues.Count;

            // Phase 2: Create chunks
            Progress.Status = "Chunking";
            var chunks = _chunkingService.CreateChunks(allIssues);
            Progress.ChunksCreated = chunks.Count;

            // Phase 3: Embed (batch, using currently configured model)
            Progress.Status = "Embedding";
            var batchSize = settings.LmStudio.EmbeddingBatchSize;
            for (var i = 0; i < chunks.Count; i += batchSize)
            {
                var batch = chunks.Skip(i).Take(batchSize).ToList();
                var texts = batch.Select(c => _chunkingService.ComposeEmbeddingText(c)).ToList();
                var vectors = await _embeddingService.EmbedBatchAsync(texts, ct);
                for (var j = 0; j < batch.Count && j < vectors.Count; j++)
                    batch[j].Embedding = vectors[j];
                Progress.EmbeddingsGenerated += batch.Count;
            }

            // Phase 4: Insert into ZVec.NET (sync batch)
            Progress.Status = "Inserting";
            for (var i = 0; i < chunks.Count; i += 100)
            {
                var batch = chunks.Skip(i).Take(100).ToArray();
                _vectorStore.InsertBatch(batch);
                Progress.ChunksInserted += batch.Length;
            }

            // Phase 5: Populate HybridIndexService
            _hybridIndex.Clear();
            _hybridIndex.AddRange(chunks);

            // Phase 6: Optimize
            _vectorStore.Optimize();

            Progress.Status = "Completed";
            Progress.CompletedAt = DateTime.UtcNow;
            return Progress;
        }
        catch (Exception ex)
        {
            Progress.Status = "Failed";
            Progress.ErrorMessage = ex.Message;
            return Progress;
        }
    }
}
```

---

## Epic 4: ASP.NET Core API Endpoints (SSE + REST)

**Goal**: Create API endpoints in PDDM.Api. The chat endpoint uses SSE streaming. The UI communicates via HTTP + SSE.

### Story 4.1: SSE Chat Endpoint

**File**: `src/PDDM.Api/Controllers/ChatController.cs`

```csharp
namespace PDDM.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ChatController : ControllerBase
{
    private readonly NavigationEngine _navigationEngine;
    private readonly ContextBuilderService _contextBuilder;
    private readonly ChatService _chatService;

    public ChatController(
        NavigationEngine navigationEngine,
        ContextBuilderService contextBuilder,
        ChatService chatService)
    {
        _navigationEngine = navigationEngine;
        _contextBuilder = contextBuilder;
        _chatService = chatService;
    }

    /// <summary>
    /// SSE streaming chat endpoint.
    /// UI connects via EventSource or fetch with ReadableStream.
    /// 
    /// GET /api/chat/stream?question=...
    /// 
    /// SSE events:
    ///   event: intent  → data: {"intent": "AssignedIssue"}
    ///   event: token   → data: {"token": "The "}
    ///   event: token   → data: {"token": "Epic "}
    ///   event: token   → data: {"token": "SPARK-56664 "}
    ///   ...
    ///   event: done    → data: {"done": true, "contextItems": [...]}
    /// </summary>
    [HttpGet("stream")]
    public async Task StreamChat([FromQuery] string question, CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";

        // Step 1: Navigate
        var navContext = await _navigationEngine.Navigate(question);

        // Step 2: Build RAG context
        navContext.AssembledContext = _contextBuilder.BuildContext(navContext);

        // Step 3: Send intent as first SSE event
        var intentJson = JsonSerializer.Serialize(new { intent = navContext.Intent.ToString() });
        await Response.WriteAsync($"event: intent\ndata: {intentJson}\n\n", ct);
        await Response.Body.FlushAsync(ct);

        // Step 4: Stream LLM response token-by-token
        await foreach (var token in _chatService.StreamRagResponseAsync(
            navContext.AssembledContext, question, ct))
        {
            var tokenJson = JsonSerializer.Serialize(new { token });
            await Response.WriteAsync($"event: token\ndata: {tokenJson}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        // Step 5: Send context info as final event
        var contextItems = BuildContextItems(navContext);
        var doneJson = JsonSerializer.Serialize(new
        {
            done = true,
            contextItems = contextItems,
            retrievedIds = GetRetrievedIds(navContext)
        });
        await Response.WriteAsync($"event: done\ndata: {doneJson}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    /// <summary>Non-streaming fallback endpoint (POST, returns full response)</summary>
    [HttpPost]
    public async Task<ActionResult<ChatResponseDto>> Chat([FromBody] ChatRequestDto request, CancellationToken ct)
    {
        var navContext = await _navigationEngine.Navigate(request.Question);
        navContext.AssembledContext = _contextBuilder.BuildContext(navContext);
        var answer = await _chatService.GetRagResponseAsync(navContext.AssembledContext, request.Question, ct);

        return Ok(new ChatResponseDto
        {
            Answer = answer,
            DetectedIntent = navContext.Intent,
            RetrievedChunkIds = GetRetrievedIds(navContext)
        });
    }

    private List<ContextItem> BuildContextItems(NavigatedContext nav)
    {
        var items = new List<ContextItem>();
        if (nav.ParentEpic != null) items.Add(ToContextItem(nav.ParentEpic));
        if (nav.CentralIssue != null) items.Add(ToContextItem(nav.CentralIssue));
        items.AddRange(nav.SiblingIssues.Select(ToContextItem));
        items.AddRange(nav.DecisionComments.Select(ToContextItem));
        items.AddRange(nav.CrossReferences.Select(ToContextItem));
        items.AddRange(nav.RelatedEpics.Select(ToContextItem));
        items.AddRange(nav.RelatedStories.Select(ToContextItem));
        items.AddRange(nav.ParentIssues.Select(ToContextItem));
        items.AddRange(nav.ParentEpics.Select(ToContextItem));
        return items;
    }

    private ContextItem ToContextItem(JiraDocChunk c) => new()
    {
        Key = c.Key, IssueType = c.IssueType, Summary = c.Summary,
        Status = c.Status, Tier = c.Tier,
        TierLabel = c.IssueType switch
        {
            "Epic" => "Epic", "Umbrella" => "Umbrella",
            "Story" => "Story", "Bug" => "Bug",
            "Improvement" => "Improvement", "Task" => "Task",
            "Sub-task" => "Sub-task", "Comment" => "Comment",
            _ => "Issue"
        }
    };

    private List<string> GetRetrievedIds(NavigatedContext nav)
    {
        var ids = new List<string>();
        if (nav.CentralIssue != null) ids.Add(nav.CentralIssue.Key);
        if (nav.ParentEpic != null) ids.Add(nav.ParentEpic.Key);
        ids.AddRange(nav.SiblingIssues.Select(s => s.Key));
        ids.AddRange(nav.DecisionComments.Select(c => c.Id));
        ids.AddRange(nav.CrossReferences.Select(r => r.Key));
        ids.AddRange(nav.RelatedEpics.Select(e => e.Key));
        ids.AddRange(nav.RelatedStories.Select(s => s.Key));
        ids.AddRange(nav.ParentIssues.Select(i => i.Key));
        ids.AddRange(nav.ParentEpics.Select(e => e.Key));
        return ids;
    }
}
```

**Note**: `ChatResponseDto` and `ChatRequestDto` are simple internal DTOs (not in PDDM.Shared — they're API-internal):

```csharp
// src/PDDM.Api/Models/ChatDtos.cs
namespace PDDM.Api.Models;

public sealed class ChatRequestDto { public string Question { get; set; } = ""; }
public sealed class ChatResponseDto
{
    public string Answer { get; set; } = "";
    public QueryIntent DetectedIntent { get; set; }
    public List<string> RetrievedChunkIds { get; set; } = [];
}
```

### Story 4.2: Ingestion Endpoint

**File**: `src/PDDM.Api/Controllers/IngestionController.cs`

```csharp
namespace PDDM.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class IngestionController : ControllerBase
{
    private readonly IngestionOrchestrator _orchestrator;

    public IngestionController(IngestionOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    [HttpPost("start")]
    public async Task<ActionResult<IngestionProgress>> StartIngestion(CancellationToken ct)
    {
        var progress = await _orchestrator.RunIngestionAsync(ct);
        return Ok(progress);
    }

    [HttpGet("status")]
    public ActionResult<IngestionProgress> GetStatus() => Ok(_orchestrator.Progress);
}
```

### Story 4.3: Stats Endpoint

**File**: `src/PDDM.Api/Controllers/StatsController.cs`

```csharp
namespace PDDM.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class StatsController : ControllerBase
{
    private readonly VectorStoreService _vectorStore;
    private readonly HybridIndexService _hybridIndex;
    private readonly EmbeddingService _embeddingService;

    public StatsController(VectorStoreService vectorStore, HybridIndexService hybridIndex, EmbeddingService embeddingService)
    {
        _vectorStore = vectorStore;
        _hybridIndex = hybridIndex;
        _embeddingService = embeddingService;
    }

    [HttpGet]
    public async Task<ActionResult<StatsResponseDto>> GetStats(CancellationToken ct)
    {
        var lmStudioConnected = await _embeddingService.VerifyLmStudioAsync(ct);
        return Ok(new StatsResponseDto
        {
            TotalDocuments = _hybridIndex.TotalCount,
            Tier0Count = _hybridIndex.GetByTier(0).Count,
            Tier1Count = _hybridIndex.GetByTier(1).Count,
            Tier2Count = _hybridIndex.GetByTier(2).Count,
            Tier3Count = _hybridIndex.GetByTier(3).Count,
            DecisionCommentCount = _hybridIndex.GetByTier(3).Count(c => c.ContainsDecision),
            LmStudioConnected = lmStudioConnected
        });
    }
}
```

### Story 4.4: Settings Endpoint (Configurable Models)

**File**: `src/PDDM.Api/Controllers/SettingsController.cs`

Already defined in Story 1.6. Allows UI to GET/PUT model configuration.

---

## Epic 5: Blazor Server UI with MudBlazor (Thin Client)

**Goal**: Create a clean, functional Blazor Server UI that communicates with PDDM.Api via HTTP + SSE. The UI does NOT reference ZVec.NET or PDDM.Core — only PDDM.Shared.

### Story 5.1: Main Layout & Theme

**File**: `src/PDDM.UI/Layouts/MainLayout.razor`

MudBlazor layout with:
- Top bar: PDDM title, LM Studio status indicator (from API), ZVec stats (from API)
- Left sidebar: Navigation (Chat, Ingestion, Stats, Settings)
- Main content: Active page

**File**: `src/PDDM.UI/Program.cs`

```csharp
using MudBlazor;

var builder = WebApplication.CreateBuilder(args);

// MudBlazor
builder.Services.AddMudBlazor();
builder.Services.AddMudServices();

// HttpClient for calling PDDM.Api
builder.Services.AddHttpClient("PddmApi", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["PddmUi:ApiBaseUrl"]!);
    client.Timeout = TimeSpan.FromSeconds(300);  // Long timeout for ingestion
});

// UI-only services (thin client — no ZVec, no LM Studio references)
builder.Services.AddSingleton<ChatClientService>();     // Manages SSE connection + message history
builder.Services.AddSingleton<ApiClientService>();       // REST calls to PDDM.Api

var app = builder.Build();

app.UseMudBlazor();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
```

### Story 5.2: Chat Page (THE Main Page — SSE Streaming)

**File**: `src/PDDM.UI/Pages/Chat.razor`

MudBlazor chat interface with SSE streaming. The user sees the LLM answer building up in real-time.

```razor
@page "/chat"
@inject ChatClientService ChatClient

<MudPaper Class="d-flex flex-column" Style="height: calc(100vh - 64px)">
    <!-- Chat History -->
    <MudStack Row="false" Class="flex-grow-1 pa-4" Style="overflow-y: auto" @ref="_scrollContainer">
        @foreach (var msg in ChatClient.Messages)
        {
            <MudCard Class="mb-4">
                <MudCardContent>
                    @if (msg.IsUser)
                    {
                        <MudChip Color="Color.Primary">You</MudChip>
                        <p>@msg.Text</p>
                    }
                    else
                    {
                        <MudChip Color="@GetIntentColor(msg.Intent)">@msg.Intent</MudChip>
                        <p>@msg.Text</p>
                        
                        <!-- Expandable navigation context -->
                        @if (msg.ContextItems.Any())
                        {
                            <MudExpansionPanel>
                                <MudExpansionPanelHeader>Retrieved Context (@msg.ContextItems.Count items)</MudExpansionPanelHeader>
                                <MudExpansionPanelContent>
                                    @foreach (var item in msg.ContextItems)
                                    {
                                        <MudChip Size="Size.Small" Variant="Variant.Outlined" Color="@GetTierColor(item.Tier)">
                                            @item.TierLabel @item.Key: @item.Summary
                                        </MudChip>
                                    }
                                </MudExpansionPanelContent>
                            </MudExpansionPanel>
                        }
                    }
                </MudCardContent>
            </MudCard>
        }
    </MudStack>

    <!-- Demo Suggestions -->
    <MudStack Row="true" Class="pa-2" Spacing="2">
        <MudButton Variant="Variant.Outlined" OnClick="@(() => SendQuestion("I got assigned SPARK-56664"))">Scenario A</MudButton>
        <MudButton Variant="Variant.Outlined" OnClick="@(() => SendQuestion("I need to add validation for ANSI mode casting"))">Scenario B</MudButton>
        <MudButton Variant="Variant.Outlined" OnClick="@(() => SendQuestion("Why did they decide ANSI mode as default?"))">Scenario C</MudButton>
    </MudStack>

    <!-- Input -->
    <MudStack Row="true" Class="pa-4">
        <MudTextField @bind-Value="_inputText" Label="Ask PDDM..." FullWidth="true" 
                      OnKeyDown="@OnKeyDown" />
        <MudButton Color="Color.Primary" OnClick="SendMessage" Disabled="@ChatClient.IsStreaming">Send</MudButton>
    </MudStack>
</MudPaper>

@code {
    private string _inputText = "";
    private MudStack _scrollContainer;

    private async Task SendQuestion(string question)
    {
        _inputText = question;
        await SendMessage();
    }

    private async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(_inputText)) return;
        await ChatClient.SendQuestionAsync(_inputText);
        _inputText = "";
        StateHasChanged();
        // Auto-scroll to bottom
        await _scrollContainer.ScrollToAsync(0, 999999);
    }

    private void OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey)
        {
            SendMessage();
        }
    }

    private Color GetIntentColor(string intent) => intent switch
    {
        "AssignedIssue" => Color.Primary,
        "NewRequirement" => Color.Secondary,
        "DecisionRationale" => Color.Tertiary,
        _ => Color.Default
    };

    private Color GetTierColor(int tier) => tier switch
    {
        0 => Color.Primary,    // Epic
        1 => Color.Secondary,  // Issue
        2 => Color.Tertiary,   // Sub-task
        3 => Color.Warning,    // Comment
        _ => Color.Default
    };
}
```

### Story 5.3: ChatClientService (SSE Connection Manager)

**File**: `src/PDDM.UI/Services/ChatClientService.cs`

This is the UI-side service that manages SSE connections to PDDM.Api. It handles the streaming protocol and builds up the message in real-time.

```csharp
namespace PDDM.UI.Services;

/// <summary>
/// Manages SSE connection to PDDM.Api for streaming chat responses.
/// The UI creates an EventSource (or uses fetch ReadableStream) to
/// connect to GET /api/chat/stream?question=...
/// 
/// SSE protocol from API:
///   event: intent  → data: {"intent": "AssignedIssue"}
///   event: token   → data: {"token": "The "}
///   event: token   → data: {"token": "Epic "}
///   ...
///   event: done    → data: {"done": true, "contextItems": [...]}
/// </summary>
public sealed class ChatClientService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;

    public List<ChatMessageUi> Messages { get; } = [];
    public bool IsStreaming { get; private set; }

    public ChatClientService(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClient = httpClientFactory.CreateClient("PddmApi");
        _apiBaseUrl = config["PddmUi:ApiBaseUrl"]!;
    }

    public async Task SendQuestionAsync(string question)
    {
        // Add user message
        Messages.Add(new ChatMessageUi { IsUser = true, Text = question });

        // Add placeholder for assistant response
        var assistantMsg = new ChatMessageUi { IsUser = false, Text = "", Intent = "" };
        Messages.Add(assistantMsg);

        IsStreaming = true;

        try
        {
            // Connect to SSE endpoint via fetch + ReadableStream
            // (Blazor Server can't use browser EventSource directly — use server-side HTTP)
            var url = $"{_apiBaseUrl}/api/chat/stream?question={Uri.EscapeDataString(question)}";
            
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;

                if (line.StartsWith("event: "))
                {
                    var eventType = line["event: ".Length..];
                    var dataLine = await reader.ReadLineAsync();
                    if (dataLine?.StartsWith("data: ") == true)
                    {
                        var data = dataLine["data: ".Length..];
                        ProcessSseEvent(eventType, data, assistantMsg);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            assistantMsg.Text = $"Error: {ex.Message}";
        }
        finally
        {
            IsStreaming = false;
        }
    }

    private void ProcessSseEvent(string eventType, string data, ChatMessageUi targetMsg)
    {
        switch (eventType)
        {
            case "intent":
                var intentObj = JsonSerializer.Deserialize<IntentEvent>(data);
                targetMsg.Intent = intentObj?.Intent ?? "Unknown";
                break;

            case "token":
                var tokenObj = JsonSerializer.Deserialize<TokenEvent>(data);
                if (tokenObj?.Token != null)
                    targetMsg.Text += tokenObj.Token;
                break;

            case "done":
                var doneObj = JsonSerializer.Deserialize<DoneEvent>(data);
                if (doneObj?.ContextItems != null)
                    targetMsg.ContextItems = doneObj.ContextItems;
                break;

            case "error":
                var errorObj = JsonSerializer.Deserialize<ErrorEvent>(data);
                targetMsg.Text += $" [Error: {errorObj?.Message}]";
                break;
        }
    }
}

public sealed class ChatMessageUi
{
    public bool IsUser { get; set; }
    public string Text { get; set; } = "";
    public string Intent { get; set; } = "";
    public List<ContextItem> ContextItems { get; set; } = [];
}
```

### Story 5.4: ApiClientService (REST Calls)

**File**: `src/PDDM.UI/Services/ApiClientService.cs`

```csharp
namespace PDDM.UI.Services;

/// <summary>
/// REST API client for PDDM.Api — used for ingestion, stats, settings.
/// Chat uses SSE (ChatClientService), not this.
/// </summary>
public sealed class ApiClientService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;

    public ApiClientService(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClient = httpClientFactory.CreateClient("PddmApi");
        _apiBaseUrl = config["PddmUi:ApiBaseUrl"]!;
    }

    public async Task<IngestionStatusDto?> StartIngestionAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsync($"{_apiBaseUrl}/api/ingestion/start", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IngestionStatusDto>(ct);
    }

    public async Task<IngestionStatusDto?> GetIngestionStatusAsync(CancellationToken ct = default)
    {
        return await _httpClient.GetFromJsonAsync<IngestionStatusDto>($"{_apiBaseUrl}/api/ingestion/status", ct);
    }

    public async Task<StatsResponseDto?> GetStatsAsync(CancellationToken ct = default)
    {
        return await _httpClient.GetFromJsonAsync<StatsResponseDto>($"{_apiBaseUrl}/api/stats", ct);
    }

    public async Task<LmStudioSettingsDto?> GetSettingsAsync(CancellationToken ct = default)
    {
        return await _httpClient.GetFromJsonAsync<LmStudioSettingsDto>($"{_apiBaseUrl}/api/settings", ct);
    }

    public async Task UpdateSettingsAsync(LmStudioSettingsDto settings, CancellationToken ct = default)
    {
        var response = await _httpClient.PutAsJsonAsync($"{_apiBaseUrl}/api/settings", settings, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> VerifyLmStudioAsync(CancellationToken ct = default)
    {
        return await _httpClient.GetFromJsonAsync<bool>($"{_apiBaseUrl}/api/settings/verify", ct);
    }
}
```

### Story 5.5: Ingestion Page

**File**: `src/PDDM.UI/Pages/Ingestion.razor`

Simple page calling PDDM.Api via `ApiClientService`:
- "Start Ingestion" button
- Progress display: Issues fetched, chunks created, embeddings generated, inserted
- Status badge

### Story 5.6: Stats Page

**File**: `src/PDDM.UI/Pages/Stats.razor`

Dashboard showing data from `ApiClientService.GetStatsAsync()`:
- Total documents
- Breakdown by tier
- Decision comments count
- LM Studio connectivity status

### Story 5.7: Settings Page (Configurable Models from UI)

**File**: `src/PDDM.UI/Pages/Settings.razor`

This is the NEW page that allows the user to configure LM Studio model settings from the UI. Changes are sent to PDDM.Api's `/api/settings` endpoint.

```razor
@page "/settings"
@inject ApiClientService Api

<MudPaper Class="pa-4">
    <MudText Typo="Typo.h5">Model Configuration</MudText>
    <MudText Typo="Typo.body1">Configure embedding and chat models. Changes are applied immediately to the API server.</MudText>
    
    <MudAlert Severity="Severity.Info" Class="mt-4">
        Note: Changing the embedding model dimensions requires re-ingestion. 
        Current ZVec.NET collection uses 768-dim vectors. Models with fewer dims are zero-padded.
    </MudAlert>

    <MudForm Class="mt-4">
        <MudTextField @bind-Value="_settings.BaseUrl" Label="LM Studio Base URL" />
        <MudTextField @bind-Value="_settings.EmbeddingModel" Label="Embedding Model ID" />
        <MudTextField @bind-Value="_settings.ChatModel" Label="Chat Model ID" />
        <MudNumericField @bind-Value="_settings.EmbeddingDimensions" Label="Embedding Dimensions" Min="1" Max="768" />
        <MudNumericField @bind-Value="_settings.ChatTemperature" Label="Chat Temperature" Min="0" Max="2" Step="0.1" />
        <MudNumericField @bind-Value="_settings.ChatMaxTokens" Label="Chat Max Tokens (-1 = unlimited)" Min="-1" />
        <MudNumericField @bind-Value="_settings.EmbeddingBatchSize" Label="Embedding Batch Size" Min="1" Max="200" />
        
        <MudButton Color="Color.Primary" OnClick="SaveSettings" Class="mt-4">Save Settings</MudButton>
        <MudButton Variant="Variant.Outlined" OnClick="VerifyConnection" Class="mt-4 ml-2">Verify LM Studio</MudButton>
    </MudForm>

    @if (_lmStudioStatus != null)
    {
        <MudAlert Severity="@(_lmStudioStatus.Value ? Severity.Success : Severity.Error)" Class="mt-4">
            LM Studio is @(_lmStudioStatus.Value ? "connected" : "not reachable")
        </MudAlert>
    }
</MudPaper>

@code {
    private LmStudioSettingsDto _settings = new();
    private bool? _lmStudioStatus;

    protected override async Task OnInitializedAsync()
    {
        var current = await Api.GetSettingsAsync();
        if (current != null) _settings = current;
    }

    private async Task SaveSettings()
    {
        await Api.UpdateSettingsAsync(_settings);
        MudAlert("Settings saved successfully!");
    }

    private async Task VerifyConnection()
    {
        _lmStudioStatus = await Api.VerifyLmStudioAsync();
    }
}
```

---

## Epic 6: Integration Testing & Demo Polish

### Story 6.1: Startup Verification

**File**: `src/PDDM.Api/Services/StartupVerificationService.cs`

On PDDM.Api startup, verify:
1. LM Studio is running and models are loaded
2. ZVec.NET factory initialized and collection accessible
3. Log status to console

### Story 6.2: End-to-End Demo Test

Manual test with the three scenarios:
- Scenario A: Type "SPARK-56664" → SSE stream shows Epic + siblings in real-time
- Scenario B: Type "I need to add ANSI mode validation" → SSE stream shows related landscape
- Scenario C: Type "Why ANSI mode?" → SSE stream shows decision comments

### Story 6.3: Error Handling

Handle edge cases in both API and UI:
- LM Studio not running → SSE sends error event → UI shows friendly error
- ZVec.NET collection empty → UI shows "Run Ingestion first" prompt
- Issue key not found → API streams "No docs found" message
- SSE connection drops → UI shows reconnect button

### Story 6.4: README

Create README covering:
- Prerequisites (LM Studio, .NET 10 SDK, ZVec.NET)
- How to start LM Studio with required models
- How to run PDDM.Api and PDDM.UI (two separate processes)
- How to trigger ingestion
- How to test the three scenarios
- How to configure models via appsettings.json or Settings page

---

## Implementation Order & Dependencies

```
Epic 1 (Foundation & SSE Infrastructure) ← No dependencies, start here
    │
Epic 2 (Data Models)                      ← Depends on Epic 1 (project structure)
    │
Epic 3 (Core Services)                    ← Depends on Epic 2 (models)
    │
Epic 4 (API Endpoints)                    ← Depends on Epic 3 (services)
    │
Epic 5 (Blazor UI)                        ← Depends on Epic 4 (API to call)
    │
Epic 6 (Testing & Polish)                 ← Depends on Epic 5 (UI to test)
```

**Implementation sequence**: 1 → 2 → 3 → 4 → 5 → 6

**Estimated effort per Epic**:
| Epic | Stories | Est. Time |
|---|---|---|
| 1: Foundation + SSE + Settings | 6 | 3-4 hours |
| 2: Data Models | 5 | 2-3 hours |
| 3: Core Services | 11 | 6-8 hours |
| 4: API Endpoints (SSE + REST) | 4 | 2-3 hours |
| 5: Blazor UI (SSE + Settings) | 7 | 4-6 hours |
| 6: Testing & Polish | 4 | 2-3 hours |
| **Total** | **37** | **19-27 hours** |

---

## Critical ZVec.NET Usage Checklist (Verify Before Each Story)

Before implementing any story that uses ZVec.NET, verify:

- [ ] Are we using typed `IZvecCollection<T>`? (Yes — POCO with `[ZVecVector]`)
- [ ] Are expression filters only using `==`, `!=`, `<`, `>`, `&&`, `||`, `bool`? (Yes — no `Contains()`)
- [ ] Prefer `IZvecFactory.OpenOrCreate` / DI `OpenMode.OpenOrCreate` (not obsolete `Create` bool)?
- [ ] Is `Fetch` null-checked? (Yes — returns `T?`)
- [ ] Is `Query` result accessed via `.Record` and `.Score`? (Yes — **VERIFY type name at first compile** — could be `ZVecQueryResult<T>` or `ZVecHit<T>` depending on NuGet version; do not assume either)
- [ ] Is `includeVector: false` used when we don't need result vectors? (Yes — lower latency)
- [ ] ⚠️ **Fix 3**: Never rely on `Embedding.Length > 0` after `includeVector: false` fetch — embed on-the-fly for CROSS queries instead
- [ ] Is batch insert using sync APIs? (Yes — `Insert(IReadOnlyList<T>)`)
- [ ] Are ALL string fields in create-time schema? (Yes — no DDL for string fields; **UmbrellaLink** is included — Fix 11)
- [ ] No `QueryGroupBy` calls? (Yes — group client-side)
- [ ] ZVec.NET is ONLY in PDDM.Api project? (Yes — UI is thin client, no ZVec references)
- [ ] ⚠️ **Fix 6**: HybridIndex is rebuilt from ZVec on startup — navigation never breaks after restart
- [ ] ⚠️ **Fix 7**: Embedding model is pinned — no cross-model padding; dimension changes blocked without collection reset

---

## Architecture Decision Records

### ADR-001: .NET 10 Target Framework
- **Decision**: Use net10.0 for all projects
- **Rationale**: User explicitly specified .NET Core 10
- **Impact**: All `dotnet new` commands use `-f net10.0`, all `.csproj` files target net10.0

### ADR-002: Separate API + UI Projects
- **Decision**: PDDM.Api and PDDM.UI are separate projects running on separate ports
- **Rationale**: User explicitly stated "the API is separate project from the UI, the ZVec is on the API only"
- **Impact**: ZVec.NET lives only in PDDM.Api; UI is thin client; CORS needed; SSE crosses process boundary

### ADR-003: SSE Streaming for Chat
- **Decision**: Chat responses stream from API to UI via SSE (Server-Sent Events)
- **Rationale**: User explicitly stated "the UI will communicate with the API sending the user Message and receive SSE response"
- **Impact**: Chat endpoint is GET with SSE events; LM Studio called with stream=true; UI uses ReadableStream to consume SSE

### ADR-004: Configurable Models via appsettings + UI
- **Decision**: Embedding model and chat model are configurable via appsettings.json AND via a Settings page in the UI
- **Rationale**: User explicitly stated "chat & embeddings models should be configured in the app settings or from the UI"
- **Impact**: SettingsService in API allows runtime updates via PddmRuntimeSettings (explicit mutable singleton); Settings page in UI sends changes via PUT /api/settings; services inject PddmRuntimeSettings, NOT IOptionsMonitor

### ADR-005: ZVec.NET Only (No SQLite)
- **Decision**: Use ZVec.NET as the sole persistent storage engine; HybridIndexService is a CACHE (not a second DB)
- **Rationale**: User stated "db: all ZVec (ZVec + SQLite if beneficial but ZVec is sufficient)" — ZVec alone is sufficient for POC
- **Impact**: HybridIndexService provides in-memory hierarchy navigation as a CACHE of ZVec data. It is rebuilt from ZVec on startup (Fix 6), so navigation is never broken after restart. Document HybridIndex as a cache, not a second DB.

### ADR-006: WASM Compatibility is Irrelevant
- **Decision**: Do not consider WASM compatibility for any component
- **Rationale**: User stated "your comment ZVec is not Wasm Compatible is useless, whatever the choice is the ZVec will be on the API project only"
- **Impact**: No WASM concern documented; UI choice (Blazor Server) is based on simplicity, not ZVec compatibility

### ADR-007: Pin Embedding Model (No Cross-Model Padding)
- **Decision**: One embedding model is pinned for the ZVec collection lifetime. Switching models/dimensions requires destroying the collection and re-ingesting.
- **Rationale**: Fix 7 — Zero-padding across different embedding spaces is mathematically invalid (changes norms/geometry). Even models with the same dimension count produce incompatible vectors.
- **Impact**: Settings UI blocks dimension changes without a "Reset Collection" step. Changing only the model name (same dims) is allowed (same embedding space family).

### ADR-008: Explicit Mutable Runtime Settings (Not IOptionsMonitor)
- **Decision**: Use PddmRuntimeSettings (explicit mutable singleton) for runtime-updated config, not IOptionsMonitor
- **Rationale**: Fix 12 — IOptionsMonitor only reloads when appsettings.json file changes on disk, which is unreliable for runtime updates from UI. The PUT /api/settings needs to update config immediately, not wait for a file watcher.
- **Impact**: Services inject PddmRuntimeSettings; SettingsService directly updates the singleton + persists to appsettings.json for restart survival.

---

## Applied Review Fixes (Pre-Implementation Patch List)

The following fixes were applied to this plan before coding begins, based on a thorough review:

| Fix # | Issue | What Changed |
|---|---|---|
| 1 | HybridIndex key collision — comments overwrite issues | Primary index by `chunk.Id` (unique); secondary `_byJiraKey` only for Tier 0-2; `GetByKey()` never returns comments |
| 2 | `DetermineParentKey` stub returning "" | Implemented: uses `issue.Fields.Parent?.Key` for sub-tasks; added `Parent` field to JiraIssueFields DTO |
| 3 | CROSS query never runs (empty embedding after includeVector:false) | Embed `Summary + Description` on-the-fly for CROSS query instead of relying on stored vector |
| 4 | Type name ZVecQueryResult vs ZVecHit | Added note: verify against NuGet assembly at first compile; do not invent type names |
| 5 | POC-Plan API snippets are unsafe | Added warning: implement only from PDDM-Implementation-Plan, not from POC-Plan snippets |
| 6 | HybridIndex volatile — no restart rebuild | Added `RebuildFromZVec()` method; called on startup after ZVec opens; HybridIndex is a CACHE, not a second DB |
| 7 | Zero-padding myth | Removed zero-padding claim; pin one model; Settings UI blocks dimension changes without collection reset |
| 8 | Ingestion filters not wired | Applied `MaxCommentsPerIssue` in ChunkingService loop; noted `MinCommentsForIssue` for fetch-level filtering |
| 9 | Comment embedding text regression | Restored POC-style: `"On {ParentKey}: {Author} said: {Body}"` for Tier 3 |
| 10 | Intent classifier brittle | Ordered heuristic rules + `ExtractIssueKey()`; hybrid LLM JSON classify on ambiguous (`GeneralQuestion`); single classify per request; intent passed into prompts |
| 11 | Missing UmbrellaLink field | Added `UmbrellaLink` to JiraDocChunk POCO; `ExtractUmbrellaLink()` parses issuelinks |
| 12 | IOptionsMonitor fantasy | Replaced with explicit `PddmRuntimeSettings` mutable singleton; `SettingsService` directly updates singleton + persists to JSON |

---

## Recommended Implementation Order (After Patches)

Epic order unchanged: 1 → 2 → 3 → 4 → 5 → 6. However:

- **Epic 3 must include**: restart-safe HybridIndex hydration (`RebuildFromZVec`) and parent-key parsing (`DetermineParentKey`) before Scenario A demo work.
- **Do NOT start Epic 5 (UI) until** Scenarios A, B, C return correct context in an API-only smoke test (console or Swagger).
- **Verify ZVec.NET type names** (ZVecQueryResult vs ZVecHit) at first compile against the actual NuGet assembly — do not assume either name.

*End of PDDM Implementation Plan*
