# CLAUDE.md - Agent Guidelines for Pixelbadger.Toolkit.Rag

## Project Overview

A .NET 10 ASP.NET Core app (minimal APIs, v3.x, shipped as a container) for Retrieval-Augmented Generation (RAG): **hybrid-only** search (Lucene.NET BM25 + SQL Server vector, fused with RRF) over text, image and audio documents, embedded locally with EmbeddingGemma 2 (ONNX Runtime). Documents are created by multipart upload (`POST /api/documents`), which enqueues a background job per file in a persistent SQL queue; re-ingest and delete are REST too; query is a REST endpoint; MCP (Streamable HTTP at `/mcp`, `Search` tool only) is for AI assistants like Claude. There is no CLI, no stdio transport and no dotnet-tool packaging.

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
- Documents: **upload only** (`POST /api/documents`, batch allowed); the server never reads caller-named paths from its own disk. A document's id is a server-assigned `Guid` (v7) created with the upload; the *logical path* (the multipart filename) is metadata only and may repeat. One job = one file for one document; jobs live in SQL and are processed by a hosted worker.
- **Single-instance assumption.** One instance owns a Lucene index directory (multi-instance is unsupported), and the code leans on it in three marked places: startup resets `Processing` jobs to `Queued` (`ResetInFlightJobsAsync`), `IngestJobRegistry` cancels in-process jobs on document delete, and `IndexWriteGate` serialises SQL+Lucene writes with deletes. If the app is ever scaled out, revisit all three (and rely on lease expiry instead of the reset).
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
curl -F "files=@./guide.md;filename=docs/guide.md" http://localhost:8080/api/documents   # 202 + document ids
curl http://localhost:8080/api/documents/<documentId>                                    # document + latest job status
curl -X POST -F "files=@./guide.md;filename=docs/guide.md" http://localhost:8080/api/documents/<documentId>  # re-ingest (409 while processing)
curl -X DELETE http://localhost:8080/api/documents/<documentId>                          # cancel job + delete
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
│   ├── DocumentEndpoints.cs    # POST /api/documents (batch), GET/POST/DELETE /api/documents/{id}
│   ├── QueryEndpoints.cs       # POST /api/query
│   └── ProblemExceptionHandler.cs # Unhandled exceptions -> generic 500 ProblemDetails
├── Ingestion/                   # Queue + worker
│   ├── IIngestQueue.cs, SqlIngestQueue.cs # Persistent SQL queue of documents + jobs (claim with UPDLOCK/READPAST, leases, attempts, re-ingest rules) + DTOs
│   ├── IngestWorker.cs         # BackgroundService: claim job -> ingest its file -> complete/fail; quiet stop on cancel / deleted document
│   ├── IngestJobRegistry.cs    # The job being processed (cancel on document delete, await its end)
│   ├── DocumentService.cs      # Delete orchestration: cancel job, await worker, delete SQL + Lucene
│   ├── InFlightJobRecovery.cs  # Startup reset of Processing jobs (hosted service + once-guard the worker also calls)
│   ├── DatabaseMigrationHostedService.cs # Runs migrations first (registered first), then InFlightJobRecoveryHostedService, then the worker
│   ├── IngestRequestValidator.cs # Upload validation incl. safe logical paths
│   ├── IngestWorkerSignal.cs   # In-process nudge so uploads are picked up immediately
│   └── IngestSettings.cs       # Rag:Ingest limits/timings
├── Mcp/
│   ├── McpRagServer.cs         # MCP tool class (DI-resolved per call): Search
│   └── SearchResultFormatter.cs# MCP text rendering (locators, [image]/[audio] markers)
├── Components/                  # Core pipeline
│   ├── ContentIngester.cs      # IngestAsync(IngestSource): routes by modality, embeds, stores in SQL + Lucene
│   ├── SearchService.cs        # BM25 + vector in parallel, RRF, hydrate from SQL
│   ├── LuceneRepository.cs     # BM25 index (chunk_id, document_id, content)
│   ├── IndexWriteGate.cs       # Serialises SQL+Lucene writes with deletes (single-process)
│   ├── RrfReranker.cs          # RRF fusion (k = 60)
│   ├── ChunkerFactory.cs, FileReaders/  # Extension-based text chunking / reading
│   └── DependencyInjection.cs  # AddRagServices(RagOptions), AddRagHostedServices()
├── Domain/                      # Document, Chunk, IngestJob, Modality, IndexStatus, LogicalPath, MediaTypes
├── Persistence/                 # IDocumentStore + EF Core SQL implementation, RagDbContext, SqlStoreOptions
├── Embeddings/                  # IEmbeddingService, Text/ (Gemma), Vision/, Audio/, Onnx/ (sessions, options)
├── Dtos/                        # SearchResult, IngestResult/IngestSource, IngestOptions
└── Migrations/                  # EF Core migrations (InitialCreate, AddIngestQueue, DocumentGuidIdsAndFlatJobs)
Dockerfile, .dockerignore        # Multi-stage image (aspnet:10.0 + ffmpeg); model and index are mounted
tools/golden/                    # transformers.js scripts that generate golden fixtures from the real model
```

### Key Abstractions

| Interface | Purpose | Implementations |
|-----------|---------|-----------------|
| `IContentIngester` | Ingests one `IngestSource(LocalPath, LogicalPath, DocumentId)` into an existing document | `ContentIngester` |
| `IIngestQueue` | Persistent document/job queue (create documents, re-ingest, claim, lease, complete, startup reset) | `SqlIngestQueue` |
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

- `Document`: int PK + unique `GlobalId` **Guid** (`Guid.CreateVersion7()`, assigned when the document is created at upload time; the id callers see and filter by). The row exists from POST time (status `Queued`, empty `ContentHash`) so the id can be returned; `SourcePath` / `Title` / `Modality` come from the latest upload. Uploading the same path twice makes two documents. `IndexStatus` (`Queued/Processing/Indexed/Failed`) follows the latest job.
- `Chunk` (table `dbo.Chunks_EG2_256`): int clustered PK (stored in Lucene as `chunk_id`), unique `GlobalId` Guid (shown to users as chunk id), `DocumentId` FK (cascade delete), ordinal, modality, locator range (chars for text, ms for audio, none for images), text (text chunks only), `vector(256)` embedding.
- `IngestJob` (`dbo.IngestJobs`, one row per job, FK to the document with cascade delete): status (`Queued/Processing/Succeeded/Skipped/Failed`), attempts, lease owner/expiry, logical path, `varbinary(max)` content that is NULLed once the job is terminal, chunk count, error. Job history is kept; the document view shows the latest job. A document has at most one non-terminal job.

### Data Flow

**Ingestion (queued):**
```
POST /api/documents (multipart, filename = logical path) -> IngestRequestValidator -> IIngestQueue.EnqueueNewDocumentsAsync (Document + job + bytes per file, one transaction) -> 202 + ids
POST /api/documents/{id} -> EnqueueReingestAsync -> Created | ReplacedQueued (queued job's content replaced in place) | Conflict (Processing -> 409) | NotFound
Startup: DatabaseMigrationHostedService -> InFlightJobRecoveryHostedService (Processing -> Queued; the worker repeats it before its first claim if SQL was down) -> IngestWorker
IngestWorker: TryClaimNextAsync (lease, attempts++, document -> Processing) -> registry.Begin -> bytes -> temp file (original extension)
     -> IContentIngester.IngestAsync(IngestSource(temp, logical, documentId)): MediaTypes (extension) -> text: reader -> chunker | image: preprocess+encode | audio: ffmpeg windows+encode
     -> IEmbeddingService -> [IndexWriteGate] IDocumentStore.ReplaceDocumentAsync (existing document only, one transaction) + LuceneRepository.ReplaceDocumentAsync (text chunks)
     -> CompleteAsync (result recorded, bytes NULLed, document status follows) -> when the queue is idle: EnsureVectorIndexAsync (>= 100 rows)
```

**Delete:**
```
DELETE /api/documents/{id} -> DocumentService: registry.CancelDocument (await the worker, Rag:Ingest:CancelTimeoutSeconds, else 409)
     -> [IndexWriteGate] IDocumentStore.DeleteDocumentAsync (jobs, then document; chunks cascade) + LuceneRepository.DeleteDocumentAsync -> 204 | 404
```
The worker treats a cancelled job (registry) and `DocumentNotFoundException` (document deleted between claim and write) as quiet stops: no requeue, no failure.

**Search:**
```
Query -> SearchService -> [Lucene BM25 || SQL vector] (max(2n, 20) each) -> RRF -> hydrate via IDocumentStore -> SearchResults
```

## Code Conventions

### Dependency Injection

`AddRagServices(this IServiceCollection, RagOptions)` is called once by `Program.cs` (the options object is built from the `Rag` configuration section); `AddRagHostedServices()` adds the migration service, the in-flight job reset and the worker, in that order (hosted services start in registration order):
- Constructor injection; register interfaces to implementations. Code that runs outside a request (the worker, the migration service) resolves scoped/transient services from an `IServiceScopeFactory` scope per job.
- Singletons for expensive resources (ONNX sessions, embedding service); transient for stateless services.
- **Constructors must be lazy**: nothing may load a model or open a DB connection at construction (`HostDependencyInjectionTests` enforces this against the real web host with an unreachable server and an empty model directory, with `ValidateOnBuild`/`ValidateScopes` on).
- Logging is registered by `AddRagServices`; the web host's default console logging applies. Use `ILogger<T>`, never `Console`.

### Endpoints (Api/)

- Endpoints are thin: validate -> call a service -> return `IResult`. One static `Map*Endpoints` extension per area, called from `Program.cs`; keep `Program.cs` small.
- Errors are ProblemDetails (`AddProblemDetails`): bad input -> `Results.ValidationProblem` / `Results.Problem(..., 400)`; unexpected exceptions -> `ProblemExceptionHandler` (generic 500, details logged only). Never put exception messages of unexpected failures in a response.
- JSON enums are strings (`JsonStringEnumConverter` in `ConfigureHttpJsonOptions`).
- The upload endpoints validate synchronously and reject the whole request on any violation (nothing created). Logical paths must be relative with plain segments (see `IngestRequestValidator.TryNormalizePath`) but need not be unique. The body-size limit is raised for those endpoints only. Re-ingest takes exactly one file; `409` while the document's job is processing; delete answers `409` when the running job does not stop within the cancel timeout.
- The search filter is `documentIds` (Guids) in REST and MCP; both parse the strings with `DocumentIdFilter.Parse` (a bad value is a `400` / tool error naming it).
- Configuration is bound and validated once at startup by `RagConfiguration.Bind` (presence + model directory exists; never loads the model or opens SQL). Missing config exits with code 1 and a clear message. Add new settings there and to the README table.
- API tests use `WebApplicationFactory<Program>` via `RagWebApplicationFactory` (settings go through `UseSetting`, because `Program` binds configuration eagerly).

### Ingest queue / worker

- `IngestWorker` handles one job (one file) at a time. A problem with the file (ingester exception) completes the job `Failed` without retry; an infrastructure exception (queue read/complete) goes through `FailAsync` (requeue until `MaxAttempts`, then `Failed`). An `OperationCanceledException` on shutdown leaves the job `Processing` (lease expiry or the startup reset recovers it). A registry cancellation (document delete) or `DocumentNotFoundException` ends the job quietly. The worker must never throw out of `ExecuteAsync` (SQL errors are logged and backed off).
- Lock order is job row, then document row, everywhere (claim, complete, fail, reset, re-ingest, delete): keep it that way to avoid deadlocks. Re-ingest additionally takes a per-document `sp_getapplock`.
- Stored file bytes are NULLed as soon as a job is terminal (including `Failed`).
- Changing the claim SQL: it is raw T-SQL in `SqlIngestQueue`; the `SqlIngestQueueTests` / `SqlIngestWorkerTests` cover it and need Docker.

### MCP

- ModelContextProtocol.AspNetCore 2.x: `AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<McpRagServer>()` and `app.MapMcp("/mcp")`. `McpRagServer` is a `[McpServerToolType]` with constructor-injected `ISearchService`; the host creates it per call (no static state). Only `Search` is exposed; uploads and deletes stay REST-only.
- One tool, `Search(query, maxResults = 5, documentIds = null)` (`documentIds` is `string[]`, parsed as Guids). Errors are `CallToolResult { IsError = true }`: `ArgumentException` (incl. a bad Guid) -> its message; anything else -> a generic message plus a stderr log.
- Results are formatted by `SearchResultFormatter.FormatForMcp` and framed as untrusted content.

### Async/Await

All I/O is async: repositories return `Task<T>`, pass `CancellationToken`, avoid `.Result` / `.Wait()`.

### Error Handling

- Throw specific exceptions (`ArgumentException` for bad input, `DocumentNotFoundException` when a document was deleted underneath a write, `RagConfigurationException` for missing/invalid config, which stops startup with a clear message).
- Endpoints return ProblemDetails for expected failures (400/404/409); unexpected exceptions go through `ProblemExceptionHandler` as a generic 500 with details logged only.

## Testing

### Test Project Structure

```
Pixelbadger.Toolkit.Rag.Tests/
├── Support/               # MockEmbeddingService (deterministic 256-d), SqlServerFixture, RagWebApplicationFactory, TestModelPaths
├── Api/                   # Query/document endpoint tests (WebApplicationFactory, mocked ISearchService / IIngestQueue / IDocumentStore / ILuceneRepository, no worker)
├── Host/                  # RagConfiguration binding, DI graph / laziness of the real web host
├── Mcp/                   # SearchResultFormatter + tool tests, MCP client over /mcp (Streamable HTTP, in-process)
├── Ingestion/             # Validator, signal, worker behaviour (in-memory + SQL queue; cancel, deleted document, startup reset), SqlIngestQueue tests
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

Ingester tests go through `PipelineHarness.IngestAsync(path)` (logical path = path relative to the content dir; creates a new document, or pass `documentId:` to re-ingest into one) or `Ingester.IngestAsync(new IngestSource(local, logical, documentId))` after `h.NewDocumentAsync(logical)` when the logical path matters.

## CI/CD

### GitHub Actions Workflows

| Workflow | Trigger | Purpose |
|----------|---------|---------|
| `pr-validation.yml` | PR to master | Build, test, build the container image (no push), validate the version bump |
| `publish-container.yml` | Push to master (and manual, master only) | Build, test, then push the image to GHCR and tag `v<Version>` |

Both use `actions/setup-dotnet` with `10.0.x`. `ubuntu-latest` has Docker, so Testcontainers works. Tests run without `continue-on-error`: a failing test fails the job (and blocks publishing).

The image is `ghcr.io/pixelbadger/pixelbadger.toolkit.rag` with tags `<Version from the csproj>`, `latest` and `sha-<short>`. The registry login uses the built-in `GITHUB_TOKEN` (job permissions `packages: write` and `contents: write` for the tag); there are no repository secrets and nothing is published to NuGet. The package may need its visibility set to public in GitHub once, after the first push.

### Version Requirements

**IMPORTANT:** PRs that modify code in `Pixelbadger.Toolkit.Rag/` (excluding tests) MUST increment the version.

The CI runs `.github/scripts/check-version-increment.ps1` which:
1. Reads `<Version>` from the `.csproj`
2. Finds the latest `v<MAJOR.MINOR.PATCH>` git tag (a release; none yet means the check passes)
3. Fails if current version <= the latest released version

`publish-container.yml` also refuses to push when the tag `v<Version>` already exists (it never overwrites a released image), and creates and pushes that tag after a successful push.

Edit `Pixelbadger.Toolkit.Rag/Pixelbadger.Toolkit.Rag.csproj` (`<Version>X.Y.Z</Version>`) using semantic versioning: major = breaking, minor = backward-compatible feature, patch = fix.

### CI Behavior Notes

- Path filters exclude `.github/` and test project changes from triggering the workflows' version checks and publish.
- Versions are tracked by git tags, not by a package feed. 3.0.0 is unreleased until the first merge to master pushes the image and the `v3.0.0` tag.

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
| `Rag:Ingest:MaxFileSizeBytes` / `MaxFilesPerRequest` / `MaxChunkCharacters` | | Default 10 MiB / 100 / 20000. Also drive the request body limits. |
| `Rag:Ingest:MaxAttempts` / `LeaseSeconds` / `PollIntervalSeconds` / `CancelTimeoutSeconds` | | Default 3 / 600 / 2 / 30. |

Prerequisites outside the repo: SQL Server 2025 (e.g. `docker run -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e 'MSSQL_SA_PASSWORD=...' -p 1433:1433 mcr.microsoft.com/mssql/server:2025-latest`; the DiskANN index is a preview feature on SQL Server 2025 and needs `PREVIEW_FEATURES = ON`), the model files (`huggingface-cli download onnx-community/embeddinggemma-2-ONNX` with `tokenizer.json`, `config.json`, `processor_config.json` and fp32 `onnx/model`, `onnx/vision_encoder`, `onnx/audio_encoder`; each `.onnx_data` must sit beside its `.onnx`), and `ffmpeg` on PATH for audio (included in the container image).

## Index Storage

- **Lucene index:** `{index-path}/` (BM25 only: `chunk_id`, `document_id` (the Guid in `"D"` form), indexed-but-not-stored `content`)
- **SQL:** `dbo.Documents`, `dbo.Chunks_EG2_256` (text, metadata, `vector(256)`), `dbo.IngestJobs` (queue and job history); schema managed by EF Core migrations applied at startup (`Rag:ApplyMigrationsOnStartup`)

## MCP Server Integration

`/mcp` (Streamable HTTP, stateless) exposes a single MCP tool:

```json
{
  "name": "Search",
  "parameters": {
    "query": "required string",
    "maxResults": "optional int, default 5",
    "documentIds": "optional string[] (GUIDs)"
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

**Job stuck in `Processing`**: after a restart it is requeued at startup; otherwise a crashed worker's lease expires after `Rag:Ingest:LeaseSeconds` and the job is retried (up to `MaxAttempts`).

**Re-ingest or delete answers `409`**: the document's job is running (re-ingest: wait and upload again) or did not stop within `Rag:Ingest:CancelTimeoutSeconds` (delete: retry).

**Upgrading from 2.x**: the 3.0 migration drops and recreates the document, chunk and job tables; use a fresh Lucene index directory and re-upload.

**Empty search results**: check that content was chunked (non-empty paragraphs), that the ingest job completed, and that the Lucene index and SQL database belong together.

**Audio ingestion fails**: install `ffmpeg` and put it on `PATH` (the container image includes it).

**Vector index problems on local SQL Server**: the DiskANN index is a preview on SQL Server 2025; set `Rag:ExactVectorSearch=true`.

**Model fails to load**: confirm each `.onnx_data` sits beside its `.onnx` with its original filename.
