# CLAUDE.md - Agent Guidelines for Pixelbadger.Toolkit.Rag

## Project Overview

A .NET 10 CLI tool (`pbrag`, v2.x) for Retrieval-Augmented Generation (RAG): **hybrid-only** search (Lucene.NET BM25 + SQL Server vector, fused with RRF) over text, image and audio documents, embedded locally with EmbeddingGemma 2 (ONNX Runtime). Designed for use as an MCP server for AI assistants like Claude.

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

### Key decisions (see `docs/migration/PLAN.md` and `docs/migration/embeddinggemma2-reference.md`)

- Embeddings: local `onnx-community/embeddinggemma-2-ONNX`, fp32, 256-d Matryoshka (truncate + re-normalise). **The tool never downloads models**; `--model-path` / `PBRAG_MODEL_PATH` points at a local copy. No OpenAI.
- Persistence: SQL Server 2025 / Azure SQL (EF Core 10, `vector(256)`). Lucene is only the BM25 index; everything shown to users is hydrated from SQL.
- Search: hybrid only. There is no `bm25` / `vector` mode, no `--search-mode`, no MCP `searchMode`.
- Multimodal: each image / audio file is its own document. Video and images inside documents are out of scope.

## Quick Reference

```bash
# Build and test (tests need Docker for SQL Server via Testcontainers)
dotnet build
dotnet test

# Config (or pass --connection-string / --model-path)
export PBRAG_CONNECTION_STRING='Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=False'
export PBRAG_MODEL_PATH=~/models/embeddinggemma-2-onnx

# CLI commands
pbrag ingest --index-path ./index --content-path ./docs
pbrag query  --index-path ./index --query "search term" --max-results 5
pbrag serve  --index-path ./index   # MCP stdio server, tool: Search
```

## Architecture

### Project Structure

```
Pixelbadger.Toolkit.Rag/
├── Program.cs                   # Entry: RagCli.RunAsync(args)
├── RagOptions.cs                # Per-invocation config (IndexPath, Sql, Model)
├── Commands/                    # Host surface (System.CommandLine 2.0 GA)
│   ├── RagCli.cs               # Builds the root command; RunAsync wires output/error writers
│   ├── CliContext.cs           # Seams: env lookup, service-provider factory, MCP runner, writers
│   ├── CommonOptions.cs        # --index-path, --connection-string, --model-path, --exact-vector-search
│   ├── RagOptionsResolver.cs   # CLI values + env fallbacks -> RagOptions; CliConfigurationException
│   ├── IngestCommand.cs        # MigrateAsync, then file/folder ingest + per-file summary
│   ├── QueryCommand.cs         # Hybrid search from the CLI
│   ├── ServeCommand.cs         # Validates config, runs the MCP host
│   └── SearchResultFormatter.cs# CLI and MCP text rendering (locators, [image]/[audio] markers)
├── Components/                  # Core pipeline
│   ├── ContentIngester.cs      # Routes by modality, embeds, stores in SQL + Lucene
│   ├── SearchService.cs        # BM25 + vector in parallel, RRF, hydrate from SQL
│   ├── LuceneRepository.cs     # BM25 index (chunk_id, document_id, source_id, content)
│   ├── RrfReranker.cs          # RRF fusion (k = 60)
│   ├── McpRagServer.cs         # MCP tool class (DI-resolved per call): Search
│   ├── ChunkerFactory.cs, FileReaders/  # Extension-based text chunking / reading
│   └── DependencyInjection.cs  # AddRagServices(RagOptions)
├── Domain/                      # Document, Chunk, Modality, IndexStatus, DocumentIds, MediaTypes
├── Persistence/                 # IDocumentStore + EF Core SQL implementation, SqlStoreOptions, migrations
├── Embeddings/                  # IEmbeddingService, Text/ (Gemma), Vision/, Audio/, Onnx/ (sessions, options)
├── Dtos/                        # SearchResult, IngestResult/IngestSummary, IngestOptions
└── Migrations/                  # EF Core migrations
tools/golden/                    # transformers.js scripts that generate golden fixtures from the real model
```

### Key Abstractions

| Interface | Purpose | Implementations |
|-----------|---------|-----------------|
| `IContentIngester` | Ingestion pipeline (file or folder) | `ContentIngester` |
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

- `Document`: int PK + unique `GlobalId` (`doc_` + 32 hex of SHA-256 over the full normalised path; see `DocumentIds`). One per ingested file; re-ingesting replaces its chunks.
- `Chunk` (table `dbo.Chunks_EG2_256`): int clustered PK (stored in Lucene as `chunk_id`), unique `GlobalId` Guid (shown to users as chunk id), `DocumentId` FK (cascade delete), ordinal, modality, locator range (chars for text, ms for audio, none for images), text (text chunks only), `vector(256)` embedding.

### Data Flow

**Ingestion:**
```
File -> MediaTypes (extension) -> text: reader -> chunker | image: preprocess+encode | audio: ffmpeg windows+encode
     -> IEmbeddingService -> IDocumentStore.ReplaceDocumentAsync (one transaction)
     -> LuceneRepository.ReplaceDocumentAsync (text chunks) -> EnsureVectorIndexAsync (>= 100 rows)
```

**Search:**
```
Query -> SearchService -> [Lucene BM25 || SQL vector] (max(2n, 20) each) -> RRF -> hydrate via IDocumentStore -> SearchResults
```

## Code Conventions

### Dependency Injection

`AddRagServices(this IServiceCollection, RagOptions)` is called once per invocation (CLI command or MCP host):
- Constructor injection; register interfaces to implementations.
- Singletons for expensive resources (ONNX sessions, embedding service); transient for stateless services.
- **Constructors must be lazy**: nothing may load a model or open a DB connection at construction (`HostDependencyInjectionTests` enforces this with a nonexistent model path and server).
- Logging is registered by `AddRagServices`; hosts add the stderr console provider.

### Commands

- Commands are thin: parse -> `RagOptionsResolver.Resolve` (env fallbacks, validation) -> `CliContext.BuildServices(options)` -> run -> dispose the provider.
- Return exit codes (0 ok, 1 error, 130 cancelled); write errors to stderr via `CliContext.Err`. Don't call `Environment.Exit`.
- **stdout belongs to the MCP protocol in `serve`**: nothing but protocol messages may be written to it.
- System.CommandLine 2.0 GA API: `Option<T>("--name") { Required, DefaultValueFactory }`, `command.SetAction((parseResult, ct) => ...)`, `parseResult.GetValue(option)`, `root.Parse(args).InvokeAsync(InvocationConfiguration)`.

### MCP

- ModelContextProtocol 2.x. `McpRagServer` is a `[McpServerToolType]` with constructor-injected `ISearchService`; the host creates it per call (no static state).
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
├── Support/               # MockEmbeddingService (deterministic 256-d), SqlServerFixture, TestModelPaths
├── Host/                  # Command parsing/binding, env fallbacks, DI graph, MCP/CLI output formatting
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
// Host tests: mock ISearchService / IContentIngester / IDocumentStore (Moq) and run the real command tree
var h = new CliTestHarness();                     // fake env, StringWriter out/err, mocked services
var exit = await h.RunAsync("query", "--index-path", h.IndexDir, "--query", "q",
    "--connection-string", "Server=x", "--model-path", h.ModelDir);

// Tests use IDisposable for temp directory cleanup; FluentAssertions for assertions
results.Should().HaveCount(1);
await act.Should().ThrowAsync<FileNotFoundException>();
```

## CI/CD

### GitHub Actions Workflows

| Workflow | Trigger | Purpose |
|----------|---------|---------|
| `pr-validation.yml` | PR to master | Build, test, validate version bump |
| `publish-to-nuget.yml` | Push to master | Build, test, publish to NuGet.org |

Both use `actions/setup-dotnet` with `10.0.x`. `ubuntu-latest` has Docker, so Testcontainers works. Tests run without `continue-on-error`: a failing test fails the job (and blocks publishing).

### Version Requirements

**IMPORTANT:** PRs that modify code in `Pixelbadger.Toolkit.Rag/` (excluding tests) MUST increment the version.

The CI runs `.github/scripts/check-version-increment.ps1` which:
1. Reads `<Version>` from the `.csproj`
2. Queries NuGet.org for the latest published version
3. Fails if current version <= published version

Edit `Pixelbadger.Toolkit.Rag/Pixelbadger.Toolkit.Rag.csproj` (`<Version>X.Y.Z</Version>`) using semantic versioning: major = breaking, minor = backward-compatible feature, patch = fix.

### Secrets Required

| Secret | Purpose |
|--------|---------|
| `NUGET_API_KEY` | API key for publishing to NuGet.org |

### CI Behavior Notes

- Path filters exclude `.github/` and test project changes from triggering version checks
- Publish uses `--skip-duplicate` to handle re-runs gracefully

## Search

Hybrid only. Algorithm (RRF):
1. Fetch `max(2n, 20)` candidates from BM25 (Lucene) and vector search (SQL) in parallel
2. Score each chunk `1 / (60 + rank)` per list
3. Sum scores for chunks appearing in both
4. Return the top N by fused score, hydrated from SQL (`SearchResult` carries `KeywordRank` / `VectorRank`)

Vector search is approximate (`VECTOR_SEARCH`, DiskANN) when the index exists and `VectorSearchMode.Auto` allows it, otherwise exact `VECTOR_DISTANCE` (`--exact-vector-search` forces exact). Only text chunks are in Lucene; image/audio chunks are found by the vector side.

## Configuration

| Setting | CLI option | Env var | Notes |
|---------|-----------|---------|-------|
| SQL connection | `--connection-string` | `PBRAG_CONNECTION_STRING` | Required. SQL Server 2025 / Azure SQL. |
| Model directory | `--model-path` | `PBRAG_MODEL_PATH` | Required. Local `onnx-community/embeddinggemma-2-ONNX` snapshot. |
| Lucene index | `--index-path` | | Required. `ingest` creates it; `query`/`serve` need it to exist. |
| Exact vector search | `--exact-vector-search` | | Optional. |

Prerequisites outside the repo: SQL Server 2025 (e.g. `docker run -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e 'MSSQL_SA_PASSWORD=...' -p 1433:1433 mcr.microsoft.com/mssql/server:2025-latest`; the DiskANN index is a preview feature on SQL Server 2025 and needs `PREVIEW_FEATURES = ON`), the model files (`huggingface-cli download onnx-community/embeddinggemma-2-ONNX` with `tokenizer.json`, `config.json`, `processor_config.json` and fp32 `onnx/model`, `onnx/vision_encoder`, `onnx/audio_encoder`; each `.onnx_data` must sit beside its `.onnx`), and `ffmpeg` on PATH for audio.

## Index Storage

- **Lucene index:** `{index-path}/` (BM25 only: `chunk_id`, `document_id`, `source_id`, indexed-but-not-stored `content`)
- **SQL:** `dbo.Documents`, `dbo.Chunks_EG2_256` (text, metadata, `vector(256)`); schema managed by EF Core migrations applied by `pbrag ingest`

## MCP Server Integration

`serve` exposes a single MCP tool over stdio:

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
| Microsoft.EntityFrameworkCore.SqlServer 10 / Microsoft.Data.SqlClient | SQL persistence, `vector` type |
| Microsoft.ML.OnnxRuntime | Local embedding inference |
| SkiaSharp | Image decoding |
| System.CommandLine 2.0 | CLI parsing |
| ModelContextProtocol 2.x | MCP server |
| Microsoft.Extensions.Hosting 10 | MCP host, logging, DI |

## Common Tasks

### Adding a New File Type

1. Add the extension to `Domain/MediaTypes.cs` (and, for text, create an `IFileReader` in `Components/FileReaders/`, register it in `DependencyInjection.cs`, and update `ChunkerFactory` if it needs a new chunker).
2. Update the extension tables in `README.md`.

### Adding a Command Option

1. Add it to `CommonOptions` (shared) or the specific command in `Commands/`.
2. Extend `RagOptionsResolver` / `RagOptions` if it feeds configuration; add tests in `Tests/Host/`.

### Changing the Embedding Model or Dimension

The chunk table name (`Chunks_EG2_256`) and `EmbeddingModelOptions.ModelId` encode the model and dimension. Changing either requires a new table, a migration and a full re-embed.

## Troubleshooting

**"Missing SQL Server connection string / embedding model path"**: pass `--connection-string` / `--model-path` or set `PBRAG_CONNECTION_STRING` / `PBRAG_MODEL_PATH`.

**"Index directory not found"**: run `ingest` before `query`/`serve`.

**Empty search results**: check that content was chunked (non-empty paragraphs) and that the Lucene index and SQL database belong together.

**Audio ingestion fails**: install `ffmpeg` and put it on `PATH`.

**Vector index problems on local SQL Server**: the DiskANN index is a preview on SQL Server 2025; use `--exact-vector-search`.

**Model fails to load**: confirm each `.onnx_data` sits beside its `.onnx` with its original filename.
