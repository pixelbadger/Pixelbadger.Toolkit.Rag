# Plan: CLI + stdio MCP → ASP.NET Core app (v3.0.0)

Follows `PLAN.md` (EmbeddingGemma 2 + SQL Server + hybrid-only). Everything in that plan's decisions still holds except the host surface.

## Decisions (agreed with the owner)

| Topic | Decision |
|---|---|
| Host | One ASP.NET Core app (`Microsoft.NET.Sdk.Web`, minimal APIs). The `pbrag` CLI, System.CommandLine and the stdio MCP transport are **removed**. |
| Packaging | No longer a dotnet tool (`PackAsTool` removed). Ship as a container (Dockerfile). Version **3.0.0** (breaking). |
| Ingest | **Upload only** (multipart). The server never reads caller-named paths from its own disk. `IngestFolderAsync`, symlink handling, `MaxFiles`/`AllowSymlinks` go. |
| Ingest execution | **Background job, persistent queue in SQL.** `POST` returns `202` + job id; a hosted worker processes jobs; status via `GET`. Survives restarts. |
| Query | REST endpoint. |
| MCP | Streamable HTTP at `/mcp`, **Search tool only** (same contract as today: `Search(query, maxResults = 5, sourceIds = null)`). Ingest is REST-only. |
| Auth | Out of scope. |
| Search | Unchanged (hybrid RRF). |

## Configuration

Bound from the `Rag` section (appsettings / env `Rag__*` / command-line), validated at startup (`ValidateOnStart`; check presence and that the model directory exists. Do **not** load the model or open SQL during validation).

| Key | Fallback env var | Notes |
|---|---|---|
| `Rag:IndexPath` | `PBRAG_INDEX_PATH` | Required. Created if missing. |
| `Rag:ConnectionString` | `PBRAG_CONNECTION_STRING` | Required. |
| `Rag:ModelPath` | `PBRAG_MODEL_PATH` | Required. Must exist. |
| `Rag:ExactVectorSearch` | | Default false. |
| `Rag:ApplyMigrationsOnStartup` | | Default true (replaces `ingest`'s `MigrateAsync`). |
| `Rag:Ingest:MaxFileSizeBytes` | | Default 10 MiB. Also drives `FormOptions`/Kestrel body limits (`MaxFilesPerJob × MaxFileSizeBytes` + slack). |
| `Rag:Ingest:MaxFilesPerJob` | | Default 100. |
| `Rag:Ingest:MaxChunkCharacters` | | Default 20000 (request may override downwards). |
| `Rag:Ingest:MaxAttempts` | | Default 3. |
| `Rag:Ingest:LeaseSeconds` | | Default 600. Worker heartbeats/extends while processing. |
| `Rag:Ingest:PollIntervalSeconds` | | Default 2. |

`RagOptions` stays the object handed to `AddRagServices(RagOptions)`; build it from configuration in `Program.cs`. Replace `RagOptionsResolver`/`CliConfigurationException` with a small `RagConfiguration` binder + `RagConfigurationException` (keep the helpful messages).

## HTTP surface

| Method | Route | Behaviour |
|---|---|---|
| `POST` | `/api/ingest` | `multipart/form-data`, one or more `files` parts, optional `maxChunkCharacters`. Each part's **filename is the logical source path** (e.g. `curl -F "files=@a.md;filename=docs/a.md"`). Validate synchronously: ≥1 file, ≤ `MaxFilesPerJob`, each ≤ `MaxFileSizeBytes`, supported extension, safe logical path (relative, no `..`, no rooted/drive paths, normalised to `/`, non-empty). Any violation → `400` ProblemDetails (whole request rejected, nothing enqueued). Duplicate logical paths in one request → `400`. On success: store job + file bytes in SQL, return `202 Accepted`, `Location: /api/ingest/{jobId}`, body = job status DTO. |
| `GET` | `/api/ingest/{jobId}` | `200` job status DTO or `404`. |
| `POST` | `/api/query` | JSON `{ "query": string, "maxResults"?: int (default 10, 1–100), "sourceIds"?: string[] }` → `200` `{ "results": SearchResult[] }` (enums as strings). `ArgumentException` / blank query → `400` ProblemDetails. |
| * | `/mcp` | MCP Streamable HTTP (`ModelContextProtocol.AspNetCore` 2.x, `WithHttpTransport`, stateless if supported), `WithTools<McpRagServer>()`. |
| `GET` | `/health` | Liveness (no SQL/model touch). |

Job status DTO:
```json
{ "jobId": "guid", "status": "Queued|Processing|Completed|Failed", "attempts": 1,
  "createdAtUtc": "…", "startedAtUtc": "…", "completedAtUtc": "…", "error": null,
  "files": [ { "path": "docs/a.md", "status": "Queued|Succeeded|Failed|Skipped",
               "documentId": "doc_…", "modality": "Text", "chunkCount": 4, "error": null } ],
  "summary": { "succeeded": 1, "failed": 0, "skipped": 0 } }
```
`Completed` means all files reached a terminal state (per-file failures don't fail the job, matching today's `IngestSummary`). `Failed` means the job itself blew up `MaxAttempts` times.

Use ProblemDetails (`AddProblemDetails`) for errors; unexpected exceptions → 500 with a generic message, details logged only.

## Ingest queue (SQL)

New EF entities + migration (`AddIngestQueue`), in the existing `RagDbContext`:

- `IngestJob` (table `dbo.IngestJobs`): `Id` Guid PK, `Status` (int enum), `Attempts`, `MaxChunkCharacters`, `CreatedAtUtc`, `StartedAtUtc?`, `CompletedAtUtc?`, `LeaseExpiresAtUtc?`, `LeaseOwner?` (instance id), `Error?`. Index on `(Status, CreatedAtUtc)`.
- `IngestJobFile` (table `dbo.IngestJobFiles`): int PK, `JobId` FK (cascade), `Ordinal`, `LogicalPath` (nvarchar(1024)), `Content` `varbinary(max)` **nullable** (set to NULL once the file is terminal, so blobs don't accumulate), `SizeBytes`, `Status`, `DocumentGlobalId?`, `Modality?`, `ChunkCount?`, `Error?`.

`IIngestQueue` (in `Persistence/` or a new `Ingestion/` folder), implemented with EF + raw SQL where needed:
- `EnqueueAsync(files, maxChunkCharacters, ct)` → job id (one transaction; stream upload bytes into the entity; fine to buffer per file given the size cap).
- `TryClaimNextAsync(owner, lease, ct)`: atomically claim the oldest job that is `Queued`, or `Processing` with an expired lease and `Attempts < MaxAttempts`. Use `UPDATE TOP (1) … WITH (UPDLOCK, READPAST, ROWLOCK) … OUTPUT inserted.Id` ordered via a CTE; increment `Attempts`, set lease. Jobs whose lease expired with `Attempts >= MaxAttempts` → `Failed`.
- `GetPendingFilesAsync(jobId)` (files not yet terminal; loads `Content`), `CompleteFileAsync(…)` (records result, nulls `Content`), `ExtendLeaseAsync`, `CompleteJobAsync`, `FailJobAsync(error)` (if attempts remain → back to `Queued`, else `Failed`), `GetJobAsync(jobId)` (no blobs).

`IngestWorker : BackgroundService`:
- Loop: claim → process pending files **sequentially** → complete; when nothing claimed, wait `PollIntervalSeconds` (also signalled immediately by an in-process `Channel`/`SemaphoreSlim` nudge from the POST endpoint).
- Per file: write `Content` to a temp file **with the original extension** under `Path.GetTempPath()/pbrag-ingest/{jobId}/`, call the ingester with (temp path, logical path), record result, delete temp file. Per-file exceptions → file `Failed` with message; continue. `OperationCanceledException` on shutdown → leave job `Processing` (lease expiry recovers it; processed files are already terminal so they're skipped on retry).
- Extend the lease between files. After the job: `EnsureVectorIndexAsync` once.
- One worker per process, one job at a time: Lucene allows a single writer. A single instance owns a Lucene index directory (document this; multi-instance is unsupported).
- Resolve scoped/transient services via `IServiceScopeFactory` per job.
- Startup: if `ApplyMigrationsOnStartup`, run `IDocumentStore.MigrateAsync` in an `IHostedService` that runs **before** the worker starts (register it first), so the worker never polls a missing table.

## Ingester changes

- Document identity comes from the **logical path**, not the temp file path. Add `DocumentIds.FromLogicalPath(string)` (normalise separators to `/`, trim leading `/`, **no** `Path.GetFullPath`; same `doc_` + 32 hex SHA-256 format). Remove `FromSourcePath`, or keep it if anything else still needs it.
- `IContentIngester`: replace both methods with `Task<IngestResult> IngestAsync(IngestSource source, IngestOptions options, CancellationToken ct)` where `IngestSource(string LocalPath, string LogicalPath)`. `SourcePath` = logical path, `SourceId` = logical file name without extension, title = logical file name, modality by the logical path's extension. Keep the size/chunk validation; `IngestResult.FilePath` = logical path. The ingester no longer calls `EnsureVectorIndexAsync` (the worker does, once per job).
- Remove `IngestFolderAsync`, reparse-point/root checks, the `Console.WriteLine` progress output (use `ILogger<ContentIngester>`), `IngestOptions.MaxFiles` and `AllowSymlinks`.

## MCP

- Keep `McpRagServer` as-is (per-call DI, `CallToolResult` errors). Move it and `SearchResultFormatter.FormatForMcp` (+ locator helpers) into `Mcp/`. Delete `FormatForCli`.
- No stdout constraints any more (HTTP transport); keep logging to the console as normal ASP.NET Core logging.

## Removals

`Commands/` (all), `System.CommandLine`, `PackAsTool`/`ToolCommandName`, `CliContext`, `RagOptionsResolver`, `CliConfigurationException`, stdio transport, `IngestFolderAsync` and its tests, CLI host tests (`CliTestHarness`, `CommandTests`). Keep `Microsoft.Extensions.Hosting` only if still needed (the Web SDK brings hosting).

## Project / build

- `Pixelbadger.Toolkit.Rag.csproj`: `Sdk="Microsoft.NET.Sdk.Web"`, drop `OutputType`/tool props, add `ModelContextProtocol.AspNetCore` (same 2.x line as `ModelContextProtocol`), bump `<Version>3.0.0</Version>`, update description/tags. Regenerate both `packages.lock.json` files with `dotnet restore --force-evaluate`, never by hand.
- `appsettings.json` (empty-ish `Rag` section with defaults, no secrets), `appsettings.Development.json`, `Properties/launchSettings.json`.
- `Dockerfile` (multi-stage, `mcr.microsoft.com/dotnet/aspnet:10.0` runtime, `apt-get install ffmpeg`, expose 8080, model at a mounted volume such as `/models/embeddinggemma-2-onnx`, index at `/data/index`) and `.dockerignore`.
- `Program.cs` should stay small: bind/validate config → `AddRagServices` → queue/worker/migration hosted services → `AddMcpServer().WithHttpTransport().WithTools<McpRagServer>()` → map endpoints (endpoint mapping in `Api/IngestEndpoints.cs`, `Api/QueryEndpoints.cs`). Add `public partial class Program;` for `WebApplicationFactory`.
- CI: `pr-validation.yml`: replace the `dotnet pack` step with `docker build` (validation only, no push). `publish-to-nuget.yml`: stop packing/publishing the tool. Rename it to something like `master-build.yml` that builds, tests and builds the image without pushing; registry push is a follow-up. Leave the version-increment check as is (3.0.0 > published 2.x still passes).

## Tests

- **Keep** pipeline/persistence/embedding/golden tests; adapt ingester tests to `IngestAsync(IngestSource…)` and logical-path identity (same logical path → same document; different temp paths with the same logical path replace rather than duplicate).
- **DI laziness**: adapt `HostDependencyInjectionTests` to build the real web host's service collection (via `WebApplicationFactory` with in-memory config pointing at nonexistent server/model, migrations off, worker disabled or not started) and assert every registration constructs without touching model/DB. Add the new queue/worker types.
- **API tests** (`Tests/Api/`, `Microsoft.AspNetCore.Mvc.Testing`): `WebApplicationFactory<Program>` with mocked `ISearchService`, `IIngestQueue`, and the worker removed. Cover: query 200 shape and enums-as-strings, blank query 400, ArgumentException 400, unexpected exception 500 without leaking the message; ingest 202 + Location, 400 for no files / too many / too large / unsupported extension / `..` path / rooted path / duplicate paths; GET job 200/404.
- **MCP**: keep the `McpOutputTests` formatting tests; add one in-process test that calls `tools/list` and `tools/call Search` over `/mcp` against the test server (MCP client from the `ModelContextProtocol` package with an HTTP transport using the factory's `HttpClient`). Assert only `Search` is listed.
- **Queue** (`[Collection("SqlServer")]`): enqueue/claim/complete; two concurrent claimers never get the same job; expired-lease reclaim; `MaxAttempts` → Failed; `Content` nulled after file completion.
- **Worker**: with a real queue (SqlServerFixture) and a mocked `IContentIngester`: per-file failure doesn't fail the job, temp files are cleaned up, already-terminal files are skipped on retry.

## Docs

Rewrite `README.md` (run with Docker/`dotnet run`, configuration table, curl examples for ingest/status/query, MCP client config pointing at `http://host:8080/mcp`, 3.0 breaking-change note) and `CLAUDE.md` (project structure, conventions: replace the Commands section with Api/endpoint conventions, remove stdout rule, data flow for queued ingest, config table, quick reference).

## Acceptance

- `dotnet build` clean (no new warnings), all non-Docker tests pass locally; Docker-dependent tests compile and are expected to run in CI.
- `dotnet run` with config starts, `/health` returns 200, `/mcp` lists only `Search`.
- No references remain to `System.CommandLine`, `pbrag ingest|query|serve`, `--index-path` etc. outside the changelog note.
