# CLAUDE.md - Agent Guidelines for Pixelbadger.Toolkit.Rag

## Project Overview

A .NET 10 ASP.NET Core app (minimal APIs, v6.x, shipped as a container) for Retrieval-Augmented Generation (RAG): **vector-only** search (SQL Server cosine similarity) over text, image and audio documents, embedded locally with EmbeddingGemma 2 (ONNX Runtime). Documents are created by multipart upload (`POST /api/documents`), which creates a document and a background job per file (jobs persisted in SQL, run by in-process message-bus consumers); re-ingest and delete are REST too; query is a REST endpoint; MCP (Streamable HTTP at `/mcp`, `Search` tool only) is for AI assistants like Claude. A React SPA (`ClientApp/`, built into `wwwroot` in the image) is served from the same origin at `/` (jobs dashboard), `/query` and `/ingest`. There is no CLI, no stdio transport and no dotnet-tool packaging.

### Architectural Boundary

**This is an MCP server.** We provide search capabilities; the LLM is the client.

**Our responsibilities:**
- Index documents (chunks, embeddings)
- Execute search queries efficiently
- Return ranked results

**NOT our responsibilities:**
- Query formulation (LLM client does this)
- Result synthesis (LLM client does this)
- Multi-hop reasoning (LLM client does this)
- LLM inference costs (client's concern)

**Implication for development:** When evaluating search quality or costs, focus on the retrieval operation itself. Don't optimize for "how an LLM might use this" - that's the client's job. We provide fast, accurate document retrieval.

### Key decisions

- Embeddings: local `onnx-community/embeddinggemma-2-ONNX`, q8 (`onnx/model_quantized.onnx`, `vision_encoder_quantized.onnx`, `audio_encoder_quantized.onnx`; fp32/fp16 unused), model id `embeddinggemma-2-q8@256`, 256-d Matryoshka (truncate + re-normalise). **The service never downloads models**; `Rag:ModelPath` / `PBRAG_MODEL_PATH` points at a local copy. No OpenAI.
- Persistence: SQL Server 2025 / Azure SQL (EF Core 10, `vector(256)`). SQL is the only index; everything shown to users is hydrated from SQL.
- Search: vector only (cosine similarity over SQL). There is no BM25 / keyword / hybrid mode, no reranker, no search-mode option, no MCP `searchMode`. BM25 and RRF were removed in 4.0 because image/audio chunks could only score from one list and were under-ranked.
- Documents: **upload only** (`POST /api/documents`, batch allowed); the server never reads caller-named paths from its own disk. A document's id is a server-assigned `Guid` (v7) created with the upload; the *logical path* (the multipart filename) is metadata only and may repeat. One job = one file for one document; jobs live in SQL. Every job status change publishes a `JobStatusChanged` event through the SlimMessageBus outbox in the same SQL transaction as the write, and in-process consumers run the jobs (no polling).
- **Single-instance assumption.** One instance runs the ingest consumers (multi-instance is unsupported), and the code leans on it in two marked places: `IngestJobRegistry` cancels the in-process job on document delete, and each subscription (`ingest`, `vector-index`) has a single consumer (`PollBatchSize` 1, one message at a time). There is no startup reset: a job whose process died is redelivered when its bus message lock expires (`Rag:Ingest:LeaseSeconds`). If the app is ever scaled out, revisit both (the registry needs shared state).
- **Canonical source.** A document's uploaded file is kept in `Documents.SourceContent` (+ `ContentType`) and served by `GET /api/documents/{id}/content`. It is written only by `IDocumentStore.ReplaceDocumentAsync`, in the same transaction as the chunk replacement (from `DocumentDraft.SourceContent`), so the source file, path/title/modality and the searchable chunks are always the same (successfully indexed) version. Re-ingest enqueue does not touch them; a failed job leaves them as they were. Job bytes are still NULLed when the job is terminal. Documents from before 4.1 have no source until re-uploaded.
- **Browser UI.** React + TypeScript + Vite + Tailwind + shadcn-style components + TanStack Query, in `Pixelbadger.Toolkit.Rag/ClientApp` (excluded from the .NET build via `DefaultItemExcludes`). Same origin as the API (relative URLs only, no CORS); the Docker `ui-build` stage copies `dist` to `wwwroot`. `Api/SpaEndpoints.cs` serves static files and, for GET/HEAD with no matched endpoint, an extension-less path outside `/api`, `/mcp`, `/health`, returns `index.html` (a middleware, not `MapFallback`, so endpoints keep their 405/415 answers). No wwwroot -> those paths are 404.
- Auth is out of scope.
- Orchestration: .NET Aspire 13.6. `Pixelbadger.Toolkit.Rag.AppHost` runs SQL Server 2025 (container) + the app (project) locally, and publishes to Azure (`azd up` / `aspire deploy`): Azure SQL Database (free offer, bill overage; Entra ID via the app's managed identity) + the Dockerfile image on Azure Container Apps (Consumption profile, 4 vCPU / 8 GiB, scale to zero, max 1 replica) with one Azure Files share at `/data` (model) and the `ingest-active` storage queue (ingest keep-alive). The app references `Pixelbadger.Toolkit.Rag.ServiceDefaults` (OpenTelemetry only).
- Multimodal: each image / audio file is its own document. Video and images inside documents are out of scope.

## Quick Reference

```bash
# Build and test (tests need Docker for SQL Server via Testcontainers)
dotnet build
dotnet test

# Config: Rag section (appsettings / Rag__* env vars / --Rag:Key=value), PBRAG_* env vars as fallbacks
export PBRAG_CONNECTION_STRING='Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=False'
export PBRAG_MODEL_PATH=~/models/embeddinggemma-2-onnx

# Run (http://localhost:8080 with the launch profile)
dotnet run --project Pixelbadger.Toolkit.Rag

# Or the whole stack under Aspire (SQL Server 2025 container + app + dashboard; needs Docker)
dotnet user-secrets set Parameters:model-path ~/models/embeddinggemma-2-onnx --project Pixelbadger.Toolkit.Rag.AppHost
dotnet run --project Pixelbadger.Toolkit.Rag.AppHost

# Azure (Container Apps + Azure SQL); the postprovision hook uploads the model when PBRAG_MODEL_PATH is set
azd up
aspire publish -o ./aspire-output   # inspect the generated Bicep without deploying

# Use it
curl -F "files=@./guide.md;filename=docs/guide.md" http://localhost:8080/api/documents   # 202 + document ids
curl http://localhost:8080/api/documents/<documentId>                                    # document + latest job status
curl -X POST -F "files=@./guide.md;filename=docs/guide.md" http://localhost:8080/api/documents/<documentId>  # re-ingest (409 while processing)
curl -X DELETE http://localhost:8080/api/documents/<documentId>                          # cancel job + delete
curl -OJ http://localhost:8080/api/documents/<documentId>/content                        # indexed source file (?download=true -> attachment)
curl 'http://localhost:8080/api/jobs?page=1&pageSize=25&status=Failed'                   # job history, newest first
curl -X POST http://localhost:8080/api/query -H 'Content-Type: application/json' \
     -d '{"query":"search term","maxResults":5}'
# MCP: http://localhost:8080/mcp (tool: Search); UI: http://localhost:8080/ (container image, or the Vite dev server below)

# Browser UI (Node 22); the dev server proxies /api, /mcp, /health to http://localhost:8080 (PBRAG_DEV_API overrides)
cd Pixelbadger.Toolkit.Rag/ClientApp && npm ci
npm run dev | npm run lint | npm run test -- --run | npm run build   # build -> dist/

# Container
docker build -t pixelbadger-rag .
```

## Architecture

### Project Structure

```
Pixelbadger.Toolkit.Rag/
├── Program.cs                   # Small: bind config -> AddRagServices -> MCP -> map endpoints; `public partial class Program`
├── RagOptions.cs                # App config (Sql, Model, Ingest, ApplyMigrationsOnStartup)
├── appsettings*.json            # `Rag` section defaults (no secrets)
├── Configuration/
│   └── RagConfiguration.cs     # Binds/validates the `Rag` section (+ PBRAG_* fallbacks); RagConfigurationException
├── Api/                         # Minimal-API endpoints (thin: validate -> call service -> ProblemDetails)
│   ├── DocumentEndpoints.cs    # POST /api/documents (batch), GET/POST/DELETE /api/documents/{id}, GET /api/documents/{id}/content
│   ├── JobEndpoints.cs         # GET /api/jobs (page, pageSize 1..100, status) -> IIngestQueue.GetJobsAsync
│   ├── SpaEndpoints.cs         # wwwroot static files + SPA fallback middleware (never /api, /mcp, /health)
│   ├── QueryEndpoints.cs       # POST /api/query
│   └── ProblemExceptionHandler.cs # Unhandled exceptions -> generic 500 ProblemDetails
├── Ingestion/                   # Queue + job service
│   ├── IIngestQueue.cs, SqlIngestQueue.cs # Persistent SQL store of documents + jobs (begin processing, complete, re-ingest rules, has-active-jobs, paged list) + DTOs; writes publish JobStatusChanged
│   ├── IIngestJobService.cs, IngestJobService.cs # Runs one job: begin -> stage bytes -> ingest -> complete/fail; retry/abandon policy; quiet stop on cancel / deleted document
│   ├── IVectorIndexService.cs, VectorIndexService.cs # On each finished job: when none is active, EnsureVectorIndexAsync, purge delivered messages, keep-alive idle
│   ├── IngestJobRegistry.cs    # The job being processed (cancel on document delete, await its end)
│   ├── DocumentService.cs      # Delete orchestration: cancel job, await it, delete SQL rows, keep-alive idle
│   ├── DatabaseMigrationHostedService.cs # Runs migrations first (registered first), then MessageBusStartupService
│   ├── IngestRequestValidator.cs # Upload validation incl. safe logical paths
│   ├── IIngestKeepAlive.cs, QueueIngestKeepAlive.cs # Busy marker in a storage queue (scale-to-zero hosts stay up while jobs remain); no-op default
│   └── IngestSettings.cs       # Rag:Ingest limits/timings
├── Messaging/                   # SlimMessageBus on the app database (SQL transport + outbox, JSON serializer)
│   ├── MessagingServiceCollectionExtensions.cs # AddRagMessaging: topic job-status-changed, subscriptions ingest + vector-index, outbox
│   ├── JobStatusChanged.cs, JobEvents.cs # The event (JobId, Status); topic, subscription and table names
│   ├── JobEventTransaction.cs  # Job writes and the outbox publish in one SQL transaction
│   ├── IngestJobConsumer.cs, VectorIndexConsumer.cs # Queued -> IIngestJobService; terminal -> IVectorIndexService
│   ├── MessageBusStartupService.cs # Hosted: waits until SQL connects, then starts the bus (stops the app if that fails)
│   ├── QuietStopSqlMessageBus.cs # SqlMessageBus whose consumers stop without errors
│   └── DeliveredMessageCleanup.cs # Purges delivered transport messages older than a day
├── Mcp/
│   ├── McpRagServer.cs         # MCP tool class (DI-resolved per call): Search
│   └── SearchResultFormatter.cs# MCP text rendering (locators, [image]/[audio] markers)
├── Components/                  # Core pipeline
│   ├── ContentIngester.cs      # IngestAsync(IngestSource): routes by modality, embeds, stores in SQL
│   ├── SearchService.cs        # Embed query -> SQL vector search -> hydrate from SQL (score = cosine similarity)
│   ├── ChunkerFactory.cs, FileReaders/  # Extension-based text chunking / reading
│   └── DependencyInjection.cs  # AddRagServices(RagOptions) (incl. messaging), AddRagHostedServices()
├── Domain/                      # Document, Chunk, IngestJob, Modality, IndexStatus, LogicalPath, MediaTypes
├── Persistence/                 # IDocumentStore + EF Core SQL implementation, RagDbContext, SqlStoreOptions
├── Embeddings/                  # IEmbeddingService, Text/ (Gemma), Vision/, Audio/, Onnx/ (sessions, options)
├── Dtos/                        # SearchResult, IngestResult/IngestSource, IngestOptions
├── Migrations/                  # EF Core migrations (InitialCreate, AddIngestQueue, DocumentGuidIdsAndFlatJobs, DocumentSourceContent, QuantizedEmbeddingsQ8, EventDrivenIngest)
└── ClientApp/                   # React SPA: src/api (fetch wrapper, types, content URL helpers), src/app (router, shell), src/components, src/pages (jobs, query, ingest), src/test (MSW)
Pixelbadger.Toolkit.Rag.ServiceDefaults/ # Aspire service defaults, trimmed to OpenTelemetry (OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set)
Pixelbadger.Toolkit.Rag.AppHost/ # Aspire AppHost: local orchestration + Azure (ACA + Azure SQL) publish
azure.yaml, aspire.config.json   # azd project (postprovision hook -> scripts/upload-model.sh); Aspire CLI AppHost pointer
scripts/upload-model.sh          # Uploads the local model to the ACA file share (/data/models/embeddinggemma-2-onnx)
Dockerfile, .dockerignore        # Multi-stage image (node:22 UI build -> wwwroot, aspnet:10.0 + ffmpeg); the model is mounted
tools/golden/                    # transformers.js scripts that generate golden fixtures from the real model
```

### Key Abstractions

| Interface | Purpose | Implementations |
|-----------|---------|-----------------|
| `IContentIngester` | Ingests one `IngestSource(LocalPath, LogicalPath, DocumentId)` into an existing document | `ContentIngester` |
| `IIngestQueue` | Persistent document/job store (create documents, re-ingest, begin processing, complete, has-active-jobs, paged job list); every write publishes its `JobStatusChanged` | `SqlIngestQueue` |
| `IIngestJobService` | Runs one ingest job (begin, stage bytes, ingest, record result; retry/abandon policy) | `IngestJobService` |
| `IVectorIndexService` | On each finished job: when none is active, build the vector index and mark the keep-alive idle | `VectorIndexService` |
| `ISearchService` | Vector search orchestration | `SearchService` |
| `IDocumentStore` | SQL persistence (incl. canonical source read/write) + vector search + migrations | `SqlDocumentStore` |
| `ITextChunker` | Text chunking | `ParagraphTextChunker`, `MarkdownTextChunker` |
| `IFileReader` | Text file reading | `PlainTextFileReader`, `MarkdownFileReader` |
| `IEmbeddingService` | Query / text / image / audio embeddings | `GemmaEmbeddingService` |
| `IImagePreprocessor` / `IVisionEncoder` | Image -> features | `ImagePreprocessor` / `VisionEncoder` |
| `IAudioPreprocessor` / `IAudioEncoder` | Audio (ffmpeg, log-mel) -> features | `AudioPreprocessor` / `AudioEncoder` |

### Domain model

- `Document`: int PK + unique `GlobalId` **Guid** (`Guid.CreateVersion7()`, assigned when the document is created at upload time; the id callers see and filter by). The row exists from POST time (status `Queued`, empty `ContentHash`, no `SourceContent`) so the id can be returned; `SourcePath` / `Title` / `Modality` / `SourceContent` / `ContentType` are those of the latest *successfully indexed* upload (the first upload's path until then). Uploading the same path twice makes two documents. `IndexStatus` (`Queued/Processing/Indexed/Failed`) follows the latest job.
- `Chunk` (table `dbo.Chunks_EG2Q8_256`, vector index `VIX_Chunks_EG2Q8_256_Embedding`): int clustered PK, unique `GlobalId` Guid (shown to users as chunk id), `DocumentId` FK (cascade delete), ordinal, modality, locator range (chars for text, ms for audio, none for images), text (text chunks only), `vector(256)` embedding.
- `IngestJob` (`dbo.IngestJobs`, one row per job, FK to the document with cascade delete): status (`Queued/Processing/Succeeded/Skipped/Failed`), attempts (incremented each time the job begins processing), logical path, `varbinary(max)` content that is NULLed once the job is terminal, chunk count, error. Job history is kept; the document view shows the latest job. A document has at most one non-terminal job.

### Data Flow

**Ingestion (event-driven):**
```
POST /api/documents (multipart, filename = logical path) -> IngestRequestValidator -> IIngestQueue.EnqueueNewDocumentsAsync (Document + job + bytes per file, one transaction, JobStatusChanged(Queued) via outbox) -> 202 + ids
POST /api/documents/{id} -> EnqueueReingestAsync -> Created | ReplacedQueued (queued job's content replaced in place) | Conflict (Processing -> 409) | NotFound
Startup: DatabaseMigrationHostedService -> MessageBusStartupService (waits until SQL connects, then starts the consumers and the outbox sender)
Queued event -> IngestJobConsumer -> IngestJobService.ProcessAsync: BeginProcessingAsync (Queued or Processing -> Processing, attempts++, document -> Processing, Processing event) -> registry.Begin -> bytes -> temp file (original extension)
     -> IContentIngester.IngestAsync(IngestSource(temp, logical, documentId)): MediaTypes (extension) -> text: reader -> chunker | image: preprocess+encode | audio: ffmpeg windows+encode
     -> IEmbeddingService -> IDocumentStore.ReplaceDocumentAsync (existing document only, one transaction; also writes the file bytes as Document.SourceContent)
     -> CompleteAsync (result recorded, job bytes NULLed, document status follows, terminal event via outbox)
Terminal event -> VectorIndexConsumer -> VectorIndexService.OnJobFinishedAsync: when no job is Queued/Processing -> EnsureVectorIndexAsync (>= 100 rows), purge delivered bus messages older than 1 day, keep-alive MarkIdleAsync
```

**Delete:**
```
DELETE /api/documents/{id} -> DocumentService: registry.CancelDocument (await the running job, Rag:Ingest:CancelTimeoutSeconds, else 409)
     -> IDocumentStore.DeleteDocumentAsync (jobs, then document; chunks cascade) -> keep-alive MarkIdle if no job is active -> 204 | 404
```
`IngestJobService` treats a cancelled job (registry) and `DocumentNotFoundException` (document deleted between begin and write) as quiet stops: no retry, no failure.

**Search:**
```
Query -> SearchService -> embed -> IDocumentStore.SearchAsync (n nearest documents, best chunk each, cosine) -> hydrate via IDocumentStore -> SearchResults (Score = 1 - distance)
```

## Code Conventions

### Dependency Injection

`AddRagServices(this IServiceCollection, RagOptions)` is called once by `Program.cs` (the options object is built from the `Rag` configuration section) and also calls `AddRagMessaging` (SlimMessageBus on the app database; its own hosted service is removed). `AddRagHostedServices()` adds the migration service, then `MessageBusStartupService`, in that order (hosted services start in registration order):
- Constructor injection; register interfaces to implementations. Code that runs outside a request (the bus consumers, the migration service) resolves scoped/transient services from an `IServiceScopeFactory` scope per job.
- Singletons for expensive resources (ONNX sessions, embedding service); transient for stateless services.
- **Constructors must be lazy**: nothing may load a model or open a DB connection at construction (`HostDependencyInjectionTests` enforces this against the real web host with an unreachable server and an empty model directory, with `ValidateOnBuild`/`ValidateScopes` on).
- Logging is registered by `AddRagServices`; the web host's default console logging applies. Use `ILogger<T>`, never `Console`.

### Endpoints (Api/)

- Endpoints are thin: validate -> call a service -> return `IResult`. One static `Map*Endpoints` extension per area, called from `Program.cs`; keep `Program.cs` small.
- Errors are ProblemDetails (`AddProblemDetails`): bad input -> `Results.ValidationProblem` / `Results.Problem(..., 400)`; unexpected exceptions -> `ProblemExceptionHandler` (generic 500, details logged only). Never put exception messages of unexpected failures in a response.
- JSON enums are strings (`JsonStringEnumConverter` in `ConfigureHttpJsonOptions`).
- The upload endpoints validate synchronously and reject the whole request on any violation (nothing created). Logical paths must be relative with plain segments (see `IngestRequestValidator.TryNormalizePath`) but need not be unique. The body-size limit is raised for those endpoints only. Re-ingest takes exactly one file; `409` while the document's job is processing; delete answers `409` when the running job does not stop within the cancel timeout.
- The search filter is `documentIds` (Guids) in REST and MCP; both parse the strings with `DocumentIdFilter.Parse` (a bad value is a `400` / tool error naming it).
- `GET /api/jobs` binds `page` / `pageSize` / `status` as strings and answers `ValidationProblem` for bad values (status: enum names only, case-insensitive). The SQL query projects to `IngestJobListItemDto` (never `Content` or blobs), ordered `CreatedAtUtc DESC, Id DESC`.
- `GET /api/documents/{id}/content` reads `IDocumentStore.GetContentAsync` (bytes from SQL only; never a path on disk), `inline` by default / `attachment` with `?download=true`, filename via `ContentDispositionHeaderValue`, range enabled, `nosniff`; `404` when unknown or no source.
- Configuration is bound and validated once at startup by `RagConfiguration.Bind` (presence + model directory exists; never loads the model or opens SQL). Missing config exits with code 1 and a clear message. Add new settings there and to the README table.
- API tests use `WebApplicationFactory<Program>` via `RagWebApplicationFactory` (settings go through `UseSetting`, because `Program` binds configuration eagerly).

### Ingest queue / jobs

- `IngestJobService` handles one job (one file) at a time: one consumer per subscription, one message at a time. A problem with the file (ingester exception) completes the job `Failed` without retry. An infrastructure exception (queue read/write) backs off (`Rag:Ingest:PollIntervalSeconds`, doubling, max 60 s) and rethrows so the bus redelivers; the next `BeginProcessingAsync` increments `Attempts`. On the attempt that reaches `MaxAttempts` the job is completed `Failed` instead. A redelivery with `Attempts > MaxAttempts` is completed `Failed` as abandoned (the process keeps dying on it).
- Shutdown leaves the job `Processing`; the bus redelivers it when its message lock expires. Locks are not renewed, so `Rag:Ingest:LeaseSeconds` must exceed the longest ingest. A registry cancellation (document delete) or `DocumentNotFoundException` ends the job quietly. `QuietStopSqlMessageBus` makes the consumers stop without errors (SlimMessageBus 3.5 otherwise throws on stop).
- Every job write goes through `JobEventTransaction` (job/document rows plus the `JobStatusChanged` outbox publish in one SQL transaction). Any new status change must publish its event there.
- Lock order is job row, then document row, everywhere (begin processing, complete, re-ingest, delete): keep it that way to avoid deadlocks. Re-ingest additionally takes a per-document `sp_getapplock`. Its job lookup and begin processing can still deadlock on the jobs table's indexes, so `EnqueueReingestAsync` retries the whole transaction when it is the deadlock victim (SQL error 1205).
- Stored file bytes are NULLed as soon as a job is terminal (including `Failed`).
- Changing `BeginProcessingAsync` / `CompleteAsync` SQL: it is raw T-SQL in `SqlIngestQueue`; `SqlIngestQueueTests` and the `Messaging/` SQL tests cover it and need Docker.
- Bus tables (`BusMessages*`, `BusOutbox*`; names in `Messaging/JobEvents.cs`) are created by SlimMessageBus in the app database when the bus is first built (its first start or first publish). `DeliveredMessageCleanup` purges delivered transport messages older than a day, from `VectorIndexService` when the queue is idle.

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
├── Support/               # MockEmbeddingService (deterministic 256-d), SqlServerFixture, RagWebApplicationFactory, TestModelPaths, BusHarness, TestIngestQueue
├── Api/                   # Query/document endpoint tests (WebApplicationFactory, mocked ISearchService / IIngestQueue / IDocumentStore, no hosted services)
├── Host/                  # RagConfiguration binding, DI graph / laziness of the real web host
├── Mcp/                   # SearchResultFormatter + tool tests, MCP client over /mcp (Streamable HTTP, in-process)
├── Ingestion/             # Validator, job service (in-memory + SQL queue; cancel, deleted document, retry/abandon), VectorIndexService, DocumentService, InMemoryIngestQueue, keep-alive (Azurite), SqlIngestQueue tests
├── Messaging/             # The real bus on SQL (BusHarness): consumer routing, event-driven ingest end to end, outbox publish, startup service
├── Persistence/           # SQL store integration tests (SqlServerFixture), migration tests (EventDrivenIngest, QuantizedEmbeddings)
├── Pipeline/              # Ingester / search tests
├── Embeddings/            # Text, Vision, Audio unit tests
├── Api/SpaHostingTests.cs # SPA fallback vs /api 404, /mcp, /health (temp web root)
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

Queue and bus tests: `TestIngestQueue.CreateAsync(connectionString)` gives a `SqlIngestQueue` on a migrated database whose bus is built but not started (events stay in the outbox). `BusHarness.CreateAsync(...)` + `StartAsync()` runs the real consumers, and `RecordingJobServices` (in `BusHarness.cs`) records what each subscription received. `InMemoryIngestQueue` follows the SQL semantics and records the same `JobStatusChanged` events in `.Events` for tests that need no SQL.

Ingester tests go through `PipelineHarness.IngestAsync(path)` (logical path = path relative to the content dir; creates a new document, or pass `documentId:` to re-ingest into one) or `Ingester.IngestAsync(new IngestSource(local, logical, documentId))` after `h.NewDocumentAsync(logical)` when the logical path matters.

## Aspire / Azure

- `AppHost.cs` branches on `builder.ExecutionContext.IsPublishMode`: run mode uses `AddProject` (debuggable, model from the `model-path` parameter); publish mode uses `AddDockerfile` (the root `Dockerfile`, so ffmpeg is there) with a `/data` volume and `Rag__ModelPath=/data/models/embeddinggemma-2-onnx`. Shared wiring (`WithReference(db)`, `Rag__ConnectionString`, `WaitFor`, health check) is in `WithRagDefaults`.
- The database resource is `ragdb` (a resource cannot share the app's name `rag`). `WithReference(db)` is what grants the app identity `db_owner` in Azure; keep it.
- Keep **one** volume on the container: with two, Aspire 13.6 gives both environment storages the same truncated name. The volume's mount options set uid/gid 1654 (the image's `app` user) and `nobrl`.
- Azure SQL uses `Authentication="Active Directory Default"`; SqlClient 7 needs `Microsoft.Data.SqlClient.Extensions.Azure` for that (`Tests/Host/SqlAuthenticationTests` guards it).
- Check infrastructure changes with `aspire publish -o <dir>` and read the generated Bicep (`rag/rag.bicep`, `env/env.bicep`, `sql/sql.bicep`).
- Container Apps starts a new revision before stopping the old one, so a deploy briefly breaks the single-instance assumption (see Key decisions).
- Scale to zero with the consumers in-process: `IngestJobService` calls `IIngestKeepAlive.MarkBusyAsync` before a job runs (and from a heartbeat every `LeaseSeconds / 3` while it runs), `VectorIndexService` calls `MarkIdleAsync` once no job is active (after the vector index check), and `DocumentService` marks idle after a delete leaves nothing active. `QueueIngestKeepAlive` keeps one visible marker message (TTL 2 h, refreshed every 30 min) in the `ingest-active` queue and clears the queue when idle; the ACA scale rules are `http` + `azureQueue` (`QueueLength = 1`, the app identity). It never throws (logs once per outage; the next busy or idle call retries). Registered by `Program.cs` only when `ConnectionStrings:ingest-active` is set, otherwise `NoIngestKeepAlive`. The queue name is duplicated as a constant in `AppHost.cs` (the AppHost does not reference the app assembly).
- Tests do not use the AppHost: SQL-backed tests start their own SQL Server via Testcontainers (Docker is the only requirement, as for Aspire locally).

## CI/CD

### GitHub Actions Workflows

| Workflow | Trigger | Purpose |
|----------|---------|---------|
| `pr-validation.yml` | PR to master | UI lint/test/build (Node 22), .NET build/test, build the container image (no push), validate the version bump |
| `publish-container.yml` | Push to master (and manual, master only) | UI lint/test/build, .NET build/test, then push the image to GHCR and tag `v<Version>` |

Both use `actions/setup-dotnet` with `10.0.x`. `ubuntu-latest` has Docker, so Testcontainers works. Tests run without `continue-on-error`: a failing test fails the job (and blocks publishing).

The image is `ghcr.io/pixelbadger/pixelbadger.toolkit.rag` with tags `<Version from the csproj>`, `latest` and `sha-<short>`. The registry login uses the built-in `GITHUB_TOKEN` (job permissions `packages: write` and `contents: write` for the tag); there are no repository secrets and nothing is published to NuGet. The package may need its visibility set to public in GitHub once, after the first push.

### Version Requirements

**IMPORTANT:** PRs that modify code in `Pixelbadger.Toolkit.Rag/` (including `ClientApp/`) or `Pixelbadger.Toolkit.Rag.ServiceDefaults/` (both ship in the image; excluding tests) MUST increment the version. AppHost-only changes do not.

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

Vector only. Algorithm:
1. Embed the query (EmbeddingGemma 2, 256-d)
2. Fetch the `maxResults` nearest *documents* from SQL (`IDocumentStore.SearchAsync`), each as its single nearest chunk (one result per document, so a long text document cannot crowd out image/audio results), optionally filtered by `documentIds`. Exact search ranks chunks per document in SQL (`ROW_NUMBER() ... PARTITION BY DocumentId`); approximate search over-fetches candidate chunks, keeps the first per document, and falls back to exact when fewer than `maxResults` documents survive
3. Hydrate the chunks from SQL and return them in vector-hit order; hits whose chunk vanished meanwhile (document deleted) are skipped
4. `SearchResult.Score` is the cosine similarity, `1 - cosine distance`; higher is more similar. Text, image and audio chunks compete on the same metric.

Vector search is approximate (`VECTOR_SEARCH`, DiskANN) when the index exists and `VectorSearchMode.Auto` allows it, otherwise exact `VECTOR_DISTANCE` (`Rag:ExactVectorSearch` forces exact).

## Configuration

Bound from the `Rag` section (appsettings, env `Rag__*`, command line) by `RagConfiguration.Bind`; the `PBRAG_*` variables are fallbacks.

| Key | Fallback env var | Notes |
|---|---|---|
| `Rag:ConnectionString` | `PBRAG_CONNECTION_STRING` | Required. SQL Server 2025 / Azure SQL. |
| `Rag:ModelPath` | `PBRAG_MODEL_PATH` | Required. Local `onnx-community/embeddinggemma-2-ONNX` snapshot; must exist. |
| `Rag:ExactVectorSearch` | | Default false. |
| `Rag:ApplyMigrationsOnStartup` | | Default true. |
| `Rag:Ingest:MaxFileSizeBytes` / `MaxFilesPerRequest` / `MaxChunkCharacters` | | Default 10 MiB / 100 / 20000. Also drive the request body limits. |
| `Rag:Ingest:MaxAttempts` / `LeaseSeconds` / `PollIntervalSeconds` / `CancelTimeoutSeconds` | | Default 3 / 600 / 2 / 30. `MaxAttempts` also sets the bus delivery limit (+2, backstop). `LeaseSeconds` = bus message lock (not renewed; must exceed the longest ingest). `PollIntervalSeconds` = bus idle poll, outbox idle sleep and the infrastructure-error backoff base. |

Prerequisites outside the repo: SQL Server 2025 (e.g. `docker run -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e 'MSSQL_SA_PASSWORD=...' -p 1433:1433 mcr.microsoft.com/mssql/server:2025-latest`; the DiskANN index is a preview feature on SQL Server 2025 and needs `PREVIEW_FEATURES = ON`), the model files (`huggingface-cli download onnx-community/embeddinggemma-2-ONNX` with `tokenizer.json`, `config.json`, `processor_config.json` and the q8 `onnx/model_quantized.onnx`, `onnx/vision_encoder_quantized.onnx`, `onnx/audio_encoder_quantized.onnx`, fetched with `--include "onnx/model_quantized.onnx*"` etc. so any `.onnx_data` sidecar comes along; a sidecar is optional but must sit beside its `.onnx`), and `ffmpeg` on PATH for audio (included in the container image).

## Storage

- **SQL:** `dbo.Documents`, `dbo.Chunks_EG2Q8_256` (text, metadata, `vector(256)`), `dbo.IngestJobs` (queue and job history); schema managed by EF Core migrations applied at startup (`Rag:ApplyMigrationsOnStartup`). `BusMessages*` / `BusOutbox*` (job events) are created and managed by SlimMessageBus, not by EF Core.

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
| Microsoft.EntityFrameworkCore.SqlServer 10 / Microsoft.Data.SqlClient | SQL persistence, `vector` type, ingest queue |
| SlimMessageBus.Host.Sql / SlimMessageBus.Host.Outbox.Sql / SlimMessageBus.Host.Serialization.SystemTextJson 3.5 | Job events: SQL transport and transactional outbox on the app database, JSON serializer |
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

The chunk table name (`Chunks_EG2Q8_256`) and `EmbeddingModelOptions.ModelId` encode the model and dimension. Changing either requires a new table, a migration and a full re-embed (5.0 did this for fp32 -> q8: its migration deletes all documents; the version rule above is unaffected, any change under `Pixelbadger.Toolkit.Rag/` needs a bump).

## Troubleshooting

**Startup exits with "Configuration error: Missing ..."**: set `Rag:ConnectionString` / `Rag:ModelPath` (or `PBRAG_CONNECTION_STRING` / `PBRAG_MODEL_PATH`); the model directory must exist.

**Ingest job stays `Queued`**: the job is started by the bus consumer for the `ingest` subscription. Check the log: "Starting message bus consumers" should follow startup; "SQL is not reachable yet; starting the message bus in ..." means it is still waiting for SQL; "The message bus could not start; stopping the application" means the app stopped. Also check that migrations ran (`Rag:ApplyMigrationsOnStartup`).

**Job stuck in `Processing`**: a job whose process died (crash, restart) is delivered again when its bus message lock expires (`Rag:Ingest:LeaseSeconds`, default 600 s), so it can take that long to resume. A redelivery past `MaxAttempts` marks it `Failed`.

**Re-ingest or delete answers `409`**: the document's job is running (re-ingest: wait and upload again) or did not stop within `Rag:Ingest:CancelTimeoutSeconds` (delete: retry).

**Upgrading from 2.x**: the 3.0 migration drops and recreates the document, chunk (then `Chunks_EG2_256`) and job tables; re-upload.

**Upgrading from 3.x**: 4.0 removed BM25/hybrid search and `Rag:IndexPath` / `PBRAG_INDEX_PATH` (ignored if still set; the old index directory can be deleted). No migration, no re-upload.

**Empty search results**: check that content was chunked (non-empty paragraphs), that the ingest job completed, and that the query is run against the right database (and `documentIds`, if given, exist).

**Audio ingestion fails**: install `ffmpeg` and put it on `PATH` (the container image includes it).

**Upgrading from 4.x**: 5.0 switches to the q8 graphs and its `QuantizedEmbeddingsQ8` migration deletes ALL documents, chunks and job history (no conversion; fp32 vectors are incompatible). Download the q8 files (for Azure re-run `scripts/upload-model.sh` / `azd up` postprovision), then re-upload content.

**Upgrading from 5.x**: 6.0 is event-driven (SlimMessageBus). The `EventDrivenIngest` migration deletes jobs that were `Queued` or `Processing` (their events were never published) and documents that were never indexed; other documents are kept and marked `Indexed`. Re-upload the files that were in flight. It also turns on READ_COMMITTED_SNAPSHOT where it is off, drops the lease columns, and SlimMessageBus creates its `BusMessages*` / `BusOutbox*` tables when the bus is first used.

**Vector index problems on local SQL Server**: the DiskANN index is a preview on SQL Server 2025; set `Rag:ExactVectorSearch=true`.

**Model fails to load**: confirm the `*_quantized.onnx` files exist under `onnx/` (fp32 `model.onnx` is not used) and that any `.onnx_data` sits beside its `.onnx` with its original filename.
