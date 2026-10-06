# CLAUDE.md - Agent Guidelines for Pixelbadger.Toolkit.Rag

## Project Overview

A .NET 10 ASP.NET Core app (minimal APIs, v3.x, shipped as a container) for Retrieval-Augmented Generation (RAG): **hybrid-only** search (Lucene.NET BM25 + SQL Server vector, fused with RRF) over text, image and audio documents, embedded locally with EmbeddingGemma 2 (ONNX Runtime). Ingest is a multipart upload that enqueues a background job in a persistent SQL queue; query is a REST endpoint; MCP (Streamable HTTP at `/mcp`, `Search` tool only) is for AI assistants like Claude. There is no CLI, no stdio transport and no dotnet-tool packaging.

### Architectural Boundary

**This is an MCP server.** We provide search capabilities; the LLM is the client.

**Our responsibilities:**
- Index documents (chunks, embeddings, BM25)
- Execute search queries efficiently
- Return ranked results

**NOT our responsibilities:**
- Query formulation (LLM client does this)
- Result synthesis (LLM client does this)
- Multi-hop reasoning (LLM client does this)
- LLM inference costs (client's concern)

**Implication for development:** When evaluating search quality or costs, focus on the retrieval operation itself. Don't optimize for "how an LLM might use this" - that's the client's job. We provide fast, accurate document retrieval.

### Key decisions

- Embeddings: local `onnx-community/embeddinggemma-2-ONNX`, fp32, 256-d Matryoshka (truncate + re-normalise). **The service never downloads models**; `Rag:ModelPath` / `PBRAG_MODEL_PATH` points at a local copy. No OpenAI.
- Persistence: SQL Server 2025 / Azure SQL (EF Core 10, `vector(256)`). Lucene is only the BM25 index; everything shown to users is hydrated from SQL.
- Search: hybrid only. There is no `bm25` / `vector` mode, no search-mode option, no MCP `searchMode`.
- Ingest: **upload only** (`POST /api/ingest`); the server never reads caller-named paths from its own disk. A file's *logical path* (the multipart filename) is the document identity. Jobs live in SQL and are processed by a hosted worker; one instance owns a Lucene index directory (multi-instance is unsupported).
- Auth is out of scope.
- Multimodal: each image / audio file is its own document. Video and images inside documents are out of scope.

## Quick Reference

```bash
# Build and test (tests need Docker for SQL Server via Testcontainers)
dotnet build
dotnet test

# Config: Rag section (appsettings / Rag__* env vars / --Rag:Key=value), PBRAG_* env vars as fallbacks
export PBRAG_CONNECTION_STRING='Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=False'
export PBRAG_MODEL_PATH=~/models/embeddinggemma-2-onnx
export PBRAG_INDEX_PATH=./index

# Run (http://localhost:8080 with the launch profile)
dotnet run --project Pixelbadger.Toolkit.Rag

# Use it
curl -F "files=@./guide.md;filename=docs/guide.md" http://localhost:8080/api/ingest      # 202 + Location
curl http://localhost:8080/api/ingest/<jobId>                                            # job status
curl -X POST http://localhost:8080/api/query -H 'Content-Type: application/json' \
     -d '{"query":"search term","maxResults":5}'
# MCP: http://localhost:8080/mcp (tool: Search)

# Container
docker build -t pixelbadger-rag .
```

## Architecture

### Project Structure

```
Pixelbadger.Toolkit.Rag/
├── Program.cs                   # Small: bind config -> AddRagServices -> MCP -> map endpoints; `public partial class Program`
├── RagOptions.cs                # App config (IndexPath, Sql, Model, Ingest, ApplyMigrationsOnStartup)
├── appsettings*.json            # `Rag` section defaults (no secrets)
├── Configuration/
│   └── RagConfiguration.cs     # Binds/validates the `Rag` section (+ PBRAG_* fallbacks); RagConfigurationException
├── Api/                         # Minimal-API endpoints (thin: validate -> call service -> ProblemDetails)
│   ├── IngestEndpoints.cs      # POST /api/ingest (multipart -> queue, 202), GET /api/ingest/{jobId}
│   ├── QueryEndpoints.cs       # POST /api/query
│   └── ProblemExceptionHandler.cs # Unhandled exceptions -> generic 500 ProblemDetails
├── Ingestion/                   # Queue + worker
│   ├── IIngestQueue.cs, SqlIngestQueue.cs # Persistent SQL queue (claim with UPDLOCK/READPAST, leases, attempts) + status DTOs
│   ├── IngestWorker.cs         # BackgroundService: claim job -> files sequentially -> complete/fail
│   ├── DatabaseMigrationHostedService.cs # Runs migrations before the worker (registered first)
│   ├── IngestRequestValidator.cs # Upload validation incl. safe logical paths
│   ├── IngestWorkerSignal.cs   # In-process nudge so uploads are picked up immediately
│   └── IngestSettings.cs       # Rag:Ingest limits/timings
├── Mcp/
│   ├── McpRagServer.cs         # MCP tool class (DI-resolved per call): Search
│   └── SearchResultFormatter.cs# MCP text rendering (locators, [image]/[audio] markers)
├── Components/                  # Core pipeline
│   ├── ContentIngester.cs      # IngestAsync(IngestSource): routes by modality, embeds, stores in SQL + Lucene
│   ├── SearchService.cs        # BM25 + vector in parallel, RRF, hydrate from SQL
│   ├── LuceneRepository.cs     # BM25 index (chunk_id, document_id, source_id, content)
│   ├── RrfReranker.cs          # RRF fusion (k = 60)
│   ├── ChunkerFactory.cs, FileReaders/  # Extension-based text chunking / reading
│   └── DependencyInjection.cs  # AddRagServices(RagOptions), AddRagHostedServices()
├── Domain/                      # Document, Chunk, IngestJob/IngestJobFile, Modality, IndexStatus, DocumentIds, MediaTypes
├── Persistence/                 # IDocumentStore + EF Core SQL implementation, RagDbContext, SqlStoreOptions
├── Embeddings/                  # IEmbeddingService, Text/ (Gemma), Vision/, Audio/, Onnx/ (sessions, options)
├── Dtos/                        # SearchResult, IngestResult/IngestSource, IngestOptions
└── Migrations/                  # EF Core migrations (InitialCreate, AddIngestQueue)
Dockerfile, .dockerignore        # Multi-stage image (aspnet:10.0 + ffmpeg); model and index are mounted
tools/golden/                    # transformers.js scripts that generate golden fixtures from the real model
```

### Key Abstractions

| Interface | Purpose | Implementations |
|-----------|---------|-----------------|
| `IContentIngester` | Ingests one `IngestSource(LocalPath, LogicalPath)` | `ContentIngester` |
| `IIngestQueue` | Persistent job queue (enqueue, claim, lease, complete) | `SqlIngestQueue` |
| `ISearchService` | Hybrid search orchestration | `SearchService` |
| `IDocumentStore` | SQL persistence + vector search + migrations | `SqlDocumentStore` |
| `ILuceneRepository` | BM25 index operations | `LuceneRepository` |
| `ITextChunker` | Text chunking | `ParagraphTextChunker`, `MarkdownTextChunker` |
| `IFileReader` | Text file reading | `PlainTextFileReader`, `MarkdownFileReader` |
| `IReranker` | Result fusion | `RrfReranker` |
| `IEmbeddingService` | Query / text / image / audio embeddings | `GemmaEmbeddingService` |
| `IImagePreprocessor` / `IVisionEncoder` | Image -> features | `ImagePreprocessor` / `VisionEncoder` |
| `IAudioPreprocessor` / `IAudioEncoder` | Audio (ffmpeg, log-mel) -> features | `AudioPreprocessor` / `AudioEncoder` |

### Domain model

- `Document`: int PK + unique `GlobalId` (`doc_` + 32 hex of SHA-256 over the normalised *logical path*; see `DocumentIds.FromLogicalPath`). One per ingested file; re-ingesting the same logical path replaces its chunks. `SourcePath` is the logical path.
- `Chunk` (table `dbo.Chunks_EG2_256`): int clustered PK (stored in Lucene as `chunk_id`), unique `GlobalId` Guid (shown to users as chunk id), `DocumentId` FK (cascade delete), ordinal, modality, locator range (chars for text, ms for audio, none for images), text (text chunks only), `vector(256)` embedding.
- `IngestJob` (`dbo.IngestJobs`: status, attempts, lease owner/expiry) and `IngestJobFile` (`dbo.IngestJobFiles`: logical path, `varbinary(max)` content that is NULLed once the file is terminal, per-file result).

### Data Flow

**Ingestion (queued):**
```
POST /api/ingest (multipart, filename = logical path) -> IngestRequestValidator -> IIngestQueue.EnqueueAsync (job + bytes, one transaction) -> 202 + Location
IngestWorker: TryClaimNextAsync (lease, attempts++) -> per pending file (sequential): bytes -> temp file (original extension)
     -> IContentIngester.IngestAsync(IngestSource(temp, logical)): MediaTypes (extension) -> text: reader -> chunker | image: preprocess+encode | audio: ffmpeg windows+encode
     -> IEmbeddingService -> IDocumentStore.ReplaceDocumentAsync (one transaction) -> LuceneRepository.ReplaceDocumentAsync (text chunks)
     -> CompleteFileAsync (result recorded, bytes NULLed) -> after the job: EnsureVectorIndexAsync (>= 100 rows) -> CompleteJobAsync
```

**Search:**
```
Query -> SearchService -> [Lucene BM25 || SQL vector] (max(2n, 20) each) -> RRF -> hydrate via IDocumentStore -> SearchResults
```

## Code Conventions

### Dependency Injection

`AddRagServices(this IServiceCollection, RagOptions)` is called once by `Program.cs` (the options object is built from the `Rag` configuration section); `AddRagHostedServices()` adds the migration service and the worker, in that order (hosted services start in registration order):
- Constructor injection; register interfaces to implementations. Code that runs outside a request (the worker, the migration service) resolves scoped/transient services from an `IServiceScopeFactory` scope per job.
- Singletons for expensive resources (ONNX sessions, embedding service); transient for stateless services.
- **Constructors must be lazy**: nothing may load a model or open a DB connection at construction (`HostDependencyInjectionTests` enforces this against the real web host with an unreachable server and an empty model directory, with `ValidateOnBuild`/`ValidateScopes` on).
- Logging is registered by `AddRagServices`; the web host's default console logging applies. Use `ILogger<T>`, never `Console`.

### Endpoints (Api/)

- Endpoints are thin: validate -> call a service -> return `IResult`. One static `Map*Endpoints` extension per area, called from `Program.cs`; keep `Program.cs` small.
- Errors are ProblemDetails (`AddProblemDetails`): bad input -> `Results.ValidationProblem` / `Results.Problem(..., 400)`; unexpected exceptions -> `ProblemExceptionHandler` (generic 500, details logged only). Never put exception messages of unexpected failures in a response.
- JSON enums are strings (`JsonStringEnumConverter` in `ConfigureHttpJsonOptions`).
- The ingest endpoint validates synchronously and rejects the whole request on any violation (nothing enqueued). Logical paths must be relative with plain segments (see `IngestRequestValidator.TryNormalizePath`). The body-size limit is raised for that endpoint only.
- Configuration is bound and validated once at startup by `RagConfiguration.Bind` (presence + model directory exists; never loads the model or opens SQL). Missing config exits with code 1 and a clear message. Add new settings there and to the README table.
- API tests use `WebApplicationFactory<Program>` via `RagWebApplicationFactory` (settings go through `UseSetting`, because `Program` binds configuration eagerly).

### Ingest queue / worker

- `IngestWorker` handles one job at a time, files sequentially; per-file exceptions fail the file, not the job; an `OperationCanceledException` on shutdown leaves the job `Processing` (lease expiry recovers it). Job-level exceptions go through `FailJobAsync` (requeue until `MaxAttempts`, then `Failed`). The worker must never throw out of `ExecuteAsync` (SQL errors are logged and backed off).
- Stored file bytes are NULLed as soon as a file is terminal (and for files of a `Failed` job).
- Changing the claim SQL: it is raw T-SQL in `SqlIngestQueue`; the `SqlIngestQueueTests` / `SqlIngestWorkerTests` cover it and need Docker.

### MCP

- ModelContextProtocol.AspNetCore 2.x: `AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<McpRagServer>()` and `app.MapMcp("/mcp")`. `McpRagServer` is a `[McpServerToolType]` with constructor-injected `ISearchService`; the host creates it per call (no static state). Only `Search` is exposed; ingest stays REST-only.
- One tool, `Search(query, maxResults = 5, sourceIds = null)`. Errors are `CallToolResult { IsError = true }`: `ArgumentException` -> its message; anything else -> a generic message plus a stderr log.
- Results are formatted by `SearchResultFormatter.FormatForMcp` and framed as untrusted content.

### Async/Await

All I/O is async: repositories return `Task<T>`, pass `CancellationToken`, avoid `.Result` / `.Wait()`.

### Error Handling

- Throw specific exceptions (`FileNotFoundException`, `DirectoryNotFoundException`, `ArgumentException` for bad input, `CliConfigurationException` for missing config).
- Commands catch exceptions, print `Error: ...` to stderr and return a non-zero exit code.

## Testing

### Test Project Structure

```
Pixelbadger.Toolkit.Rag.Tests/
├── Support/               # MockEmbeddingService (deterministic 256-d), SqlServerFixture, RagWebApplicationFactory, TestModelPaths
├── Api/                   # Query/ingest endpoint tests (WebApplicationFactory, mocked ISearchService / IIngestQueue, no worker)
├── Host/                  # RagConfiguration binding, DI graph / laziness of the real web host
├── Mcp/                   # SearchResultFormatter + tool tests, MCP client over /mcp (Streamable HTTP, in-process)
├── Ingestion/             # Validator, signal, worker behaviour (in-memory + SQL queue), SqlIngestQueue tests
├── Persistence/           # SQL store integration tests (SqlServerFixture)
├── Pipeline/              # Ingester / search / Lucene / RRF tests
├── Embeddings/            # Text, Vision, Audio unit tests
├── Golden/                # Real-model golden tests (skipped unless PBRAG_MODEL_PATH is set)
└── test-assets/golden/    # Fixtures generated by tools/golden
```

### Setup

- **SQL Server**: `SqlServerFixture` (`[Collection("SqlServer")]`) starts SQL Server 2025 with **Testcontainers** (Docker required). To use an existing server, set `PBRAG_TEST_SQL_CONNECTION_STRING`.
- **Real model**: golden tests use `[SkippableFact]` and run only when `PBRAG_MODEL_PATH` points at a local EmbeddingGemma 2 snapshot (`TestModelPaths.ModelPath`). Fixtures come from `tools/golden`.
- **Everything else** uses `MockEmbeddingService`: no model, no network.

### Test Patterns

```csharp
// API tests: the real app, mocked services, no hosted services
using var factory = new RagWebApplicationFactory();
factory.Search.Setup(s => s.SearchAsync("q", 10, null, It.IsAny<CancellationToken>())).ReturnsAsync([]);
using var client = factory.CreateClient();
var response = await client.PostAsJsonAsync("/api/query", new { query = "q" });

// Tests use IDisposable for temp directory cleanup; FluentAssertions for assertions
results.Should().HaveCount(1);
await act.Should().ThrowAsync<FileNotFoundException>();
```

Ingester tests go through `PipelineHarness.IngestAsync(path)` (logical path = path relative to the content dir) or `Ingester.IngestAsync(new IngestSource(local, logical))` when the logical path matters.

## CI/CD

### GitHub Actions Workflows

| Workflow | Trigger | Purpose |
|----------|---------|---------|
| `pr-validation.yml` | PR to master | Build, test, build the container image (no push), validate version bump |
| `master-build.yml` | Push to master | Build, test, build the container image (no registry push yet) |

Both use `actions/setup-dotnet` with `10.0.x`. `ubuntu-latest` has Docker, so Testcontainers works. Tests run without `continue-on-error`: a failing test fails the job (and blocks publishing).

### Version Requirements

**IMPORTANT:** PRs that modify code in `Pixelbadger.Toolkit.Rag/` (excluding tests) MUST increment the version.

The CI runs `.github/scripts/check-version-increment.ps1` which:
1. Reads `<Version>` from the `.csproj`
2. Queries NuGet.org for the latest published version
3. Fails if current version <= published version

Edit `Pixelbadger.Toolkit.Rag/Pixelbadger.Toolkit.Rag.csproj` (`<Version>X.Y.Z</Version>`) using semantic versioning: major = breaking, minor = backward-compatible feature, patch = fix.

### CI Behavior Notes

- Path filters exclude `.github/` and test project changes from triggering version checks
- The version check still compares against the NuGet.org history of `Pixelbadger.Toolkit.Rag` (3.0.0 > published 2.x); the app is no longer published to NuGet. Pushing the image to a registry is a follow-up.

## Search

Hybrid only. Algorithm (RRF):
1. Fetch `max(2n, 20)` candidates from BM25 (Lucene) and vector search (SQL) in parallel
2. Score each chunk `1 / (60 + rank)` per list
3. Sum scores for chunks appearing in both
4. Return the top N by fused score, hydrated from SQL (`SearchResult` carries `KeywordRank` / `VectorRank`)

Vector search is approximate (`VECTOR_SEARCH`, DiskANN) when the index exists and `VectorSearchMode.Auto` allows it, otherwise exact `VECTOR_DISTANCE` (`Rag:ExactVectorSearch` forces exact). Only text chunks are in Lucene; image/audio chunks are found by the vector side.

## Configuration

Bound from the `Rag` section (appsettings, env `Rag__*`, command line) by `RagConfiguration.Bind`; the `PBRAG_*` variables are fallbacks.

| Key | Fallback env var | Notes |
|---|---|---|
| `Rag:IndexPath` | `PBRAG_INDEX_PATH` | Required. Created if missing. One instance per directory. |
| `Rag:ConnectionString` | `PBRAG_CONNECTION_STRING` | Required. SQL Server 2025 / Azure SQL. |
| `Rag:ModelPath` | `PBRAG_MODEL_PATH` | Required. Local `onnx-community/embeddinggemma-2-ONNX` snapshot; must exist. |
| `Rag:ExactVectorSearch` | | Default false. |
| `Rag:ApplyMigrationsOnStartup` | | Default true. |
| `Rag:Ingest:MaxFileSizeBytes` / `MaxFilesPerJob` / `MaxChunkCharacters` | | Default 10 MiB / 100 / 20000. Also drive the request body limits. |
| `Rag:Ingest:MaxAttempts` / `LeaseSeconds` / `PollIntervalSeconds` | | Default 3 / 600 / 2. |

Prerequisites outside the repo: SQL Server 2025 (e.g. `docker run -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e 'MSSQL_SA_PASSWORD=...' -p 1433:1433 mcr.microsoft.com/mssql/server:2025-latest`; the DiskANN index is a preview feature on SQL Server 2025 and needs `PREVIEW_FEATURES = ON`), the model files (`huggingface-cli download onnx-community/embeddinggemma-2-ONNX` with `tokenizer.json`, `config.json`, `processor_config.json` and fp32 `onnx/model`, `onnx/vision_encoder`, `onnx/audio_encoder`; each `.onnx_data` must sit beside its `.onnx`), and `ffmpeg` on PATH for audio (included in the container image).

## Index Storage

- **Lucene index:** `{index-path}/` (BM25 only: `chunk_id`, `document_id`, `source_id`, indexed-but-not-stored `content`)
- **SQL:** `dbo.Documents`, `dbo.Chunks_EG2_256` (text, metadata, `vector(256)`), `dbo.IngestJobs`, `dbo.IngestJobFiles` (queue); schema managed by EF Core migrations applied at startup (`Rag:ApplyMigrationsOnStartup`)

## MCP Server Integration

`/mcp` (Streamable HTTP, stateless) exposes a single MCP tool:

```json
{
  "name": "Search",
  "parameters": {
    "query": "required string",
    "maxResults": "optional int, default 5",
    "sourceIds": "optional string[]"
  }
}
```

## Key Dependencies

| Package | Purpose |
|---------|---------|
| Lucene.Net 4.8.0-beta | BM25 search |
| Microsoft.EntityFrameworkCore.SqlServer 10 / Microsoft.Data.SqlClient | SQL persistence, `vector` type, ingest queue |
| Microsoft.ML.OnnxRuntime | Local embedding inference |
| SkiaSharp | Image decoding |
| ModelContextProtocol.AspNetCore 2.x | MCP server (Streamable HTTP) |
| Microsoft.AspNetCore.Mvc.Testing (tests) | In-process API/MCP tests |

## Common Tasks

### Adding a New File Type

1. Add the extension to `Domain/MediaTypes.cs` (and, for text, create an `IFileReader` in `Components/FileReaders/`, register it in `DependencyInjection.cs`, and update `ChunkerFactory` if it needs a new chunker). The upload validator picks supported extensions up from these.
2. Update the extension tables in `README.md`.

### Adding a Configuration Setting

1. Add it to `RagOptions` / `IngestSettings` and read/validate it in `RagConfiguration.Bind` (and `appsettings.json` if it has a default).
2. Add tests in `Tests/Host/RagConfigurationTests.cs` and a row in the README configuration table.

### Adding an Endpoint

1. Add a `Map*Endpoints` extension in `Api/` and call it from `Program.cs`.
2. Return ProblemDetails for errors; add tests in `Tests/Api/` using `RagWebApplicationFactory`.

### Changing the Embedding Model or Dimension

The chunk table name (`Chunks_EG2_256`) and `EmbeddingModelOptions.ModelId` encode the model and dimension. Changing either requires a new table, a migration and a full re-embed.

## Troubleshooting

**Startup exits with "Configuration error: Missing ..."**: set `Rag:IndexPath` / `Rag:ConnectionString` / `Rag:ModelPath` (or `PBRAG_INDEX_PATH` / `PBRAG_CONNECTION_STRING` / `PBRAG_MODEL_PATH`); the model directory must exist.

**Ingest job stays `Queued`**: the worker polls the queue; check the log for SQL errors ("Ingest worker could not process the queue; retrying in ...") and that migrations ran (`Rag:ApplyMigrationsOnStartup`).

**Job stuck in `Processing`**: a crashed worker's lease expires after `Rag:Ingest:LeaseSeconds`, then the job is retried (up to `MaxAttempts`).

**Empty search results**: check that content was chunked (non-empty paragraphs), that the ingest job completed, and that the Lucene index and SQL database belong together.

**Audio ingestion fails**: install `ffmpeg` and put it on `PATH` (the container image includes it).

**Vector index problems on local SQL Server**: the DiskANN index is a preview on SQL Server 2025; set `Rag:ExactVectorSearch=true`.

**Model fails to load**: confirm each `.onnx_data` sits beside its `.onnx` with its original filename.
