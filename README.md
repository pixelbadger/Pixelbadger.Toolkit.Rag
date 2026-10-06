# Pixelbadger.Toolkit.Rag

An ASP.NET Core service for retrieval-augmented generation (RAG). Upload text, image and audio files over HTTP, and the service indexes them into a hybrid index and answers queries with **hybrid search**: Lucene.NET BM25 keyword search plus semantic vector search over SQL Server, fused with Reciprocal Rank Fusion (RRF). It can be queried over REST and through an **MCP server** (Streamable HTTP) for AI assistants.

- **Embeddings run locally.** [EmbeddingGemma 2](https://huggingface.co/google/embeddinggemma-2) (ONNX, fp32, 256-d Matryoshka) via ONNX Runtime. No API key, no data leaves your machine, and the service never downloads a model.
- **Storage is SQL Server 2025 / Azure SQL.** Documents, chunks (text and metadata), `vector(256)` embeddings and the ingest job queue live in SQL; Lucene holds only the BM25 index.
- **Multimodal.** Text (`.txt`, `.md`), images and audio are each ingested as their own documents and searched together.
- **Queued ingest.** `POST /api/ingest` accepts a multipart upload and returns `202` with a job id; a background worker processes the job from a persistent SQL queue (it survives restarts) and you poll `GET /api/ingest/{jobId}`.
- **MCP server.** `/mcp` exposes a single `Search` tool over Streamable HTTP. Ingest is REST-only.

> **Version 3.0 is a breaking change from 2.x.** The `pbrag` CLI, the stdio MCP transport and the dotnet-tool package are gone: the application is now a web service shipped as a container image. Ingest is upload-only (the server never reads caller-named paths from its own disk, so `--content-path`, folder ingestion, `--allow-symlinks` and `--max-files` no longer exist), `pbrag query` became `POST /api/query`, and `pbrag serve` became `/mcp`. Configuration moved from command-line options to the `Rag` configuration section (the `PBRAG_*` variables still work as fallbacks). The SQL schema gains the `IngestJobs` / `IngestJobFiles` tables (applied automatically on startup), and document ids are now derived from the *logical path* you upload under, not a server-side file path, so documents from 2.x are re-created under new ids when you re-ingest. 2.0 was itself a breaking change from 1.x (OpenAI embeddings and SQLite-vec removed, hybrid-only search).

## Table of Contents

- [Prerequisites](#prerequisites)
- [Running the service](#running-the-service)
- [Configuration](#configuration)
- [HTTP API](#http-api)
- [MCP server](#mcp-server)
- [Supported content](#supported-content)
- [How it works](#how-it-works)
- [Development and testing](#development-and-testing)

## Prerequisites

### 1. SQL Server 2025 or Azure SQL

The `vector` type needs **SQL Server 2025**, **Azure SQL Database**, Azure SQL Managed Instance or SQL database in Fabric. Locally, the Developer edition in Docker is the easiest option:

```bash
docker run -d --name sql2025 -p 1433:1433 \
  -e ACCEPT_EULA=Y -e MSSQL_PID=Developer \
  -e 'MSSQL_SA_PASSWORD=Pbrag_Dev_Pass1!' \
  mcr.microsoft.com/mssql/server:2025-latest
```

Connection string for that container:

```
Server=localhost,1433;User Id=sa;Password=Pbrag_Dev_Pass1!;TrustServerCertificate=True;Encrypt=False
```

On startup the service creates the database/tables for you by applying EF Core migrations (`Rag:ApplyMigrationsOnStartup`, on by default); the login needs permission to create them.

**Approximate vector index and PREVIEW_FEATURES.** Once a chunk table holds at least 100 rows, ingestion creates a DiskANN vector index so queries use approximate `VECTOR_SEARCH`. That index is generally available in Azure SQL Database but is a **preview feature on SQL Server 2025**, where the database needs `ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;` (the tool sets this where needed). The local preview build can lag Azure's, so if you hit problems set `Rag:ExactVectorSearch` to `true` to always use exact `VECTOR_DISTANCE` search. Results are the same up to approximation; exact search is simply slower on very large corpora.

### 2. The EmbeddingGemma 2 model (local copy)

The service loads the model from a local directory given by `Rag:ModelPath` (or `PBRAG_MODEL_PATH`). Download the [`onnx-community/embeddinggemma-2-ONNX`](https://huggingface.co/onnx-community/embeddinggemma-2-ONNX) files once:

```bash
pip install -U "huggingface_hub[cli]"

huggingface-cli download onnx-community/embeddinggemma-2-ONNX \
  --local-dir ~/models/embeddinggemma-2-onnx \
  --include "tokenizer.json" "tokenizer_config.json" "config.json" "processor_config.json" \
            "onnx/model.onnx" "onnx/model.onnx_data" \
            "onnx/vision_encoder.onnx" "onnx/vision_encoder.onnx_data" \
            "onnx/audio_encoder.onnx" "onnx/audio_encoder.onnx_data"

export PBRAG_MODEL_PATH=~/models/embeddinggemma-2-onnx
```

Notes:

- Use the **fp32** text model (`onnx/model.onnx`) plus the **vision** and **audio encoders**. The quantised and fp16 variants are not used (fp16 is unsuitable on CPU).
- Each `.onnx_data` file **must sit beside its `.onnx`** with its original name: the external-data reference is by filename.
- Disk use is roughly 3 GB (text 1.1 GB, vision 0.7 GB, audio 1.2 GB). Queries only load the text model; the vision and audio encoders load lazily when an image or audio file is ingested.

### 3. ffmpeg (audio only)

Audio ingestion decodes files with `ffmpeg` (16 kHz mono). Install it and make sure it is on `PATH` (`apt install ffmpeg`, `brew install ffmpeg`, `winget install ffmpeg`); the container image already includes it. Without it, audio files fail with a clear per-file error; text and images are unaffected.

## Running the service

### Docker

The image contains the service and ffmpeg, but **not** the model: mount your local copy of the EmbeddingGemma 2 snapshot read-only, and give it a SQL Server connection string and a volume for the Lucene index.

```bash
docker build -t pixelbadger-rag .

docker run -d --name pbrag -p 8080:8080 \
  -e Rag__ConnectionString='Server=host.docker.internal,1433;User Id=sa;Password=Pbrag_Dev_Pass1!;TrustServerCertificate=True;Encrypt=False' \
  -v ~/models/embeddinggemma-2-onnx:/models/embeddinggemma-2-onnx:ro \
  -v pbrag-data:/data \
  pixelbadger-rag

curl http://localhost:8080/health
```

The image defaults `Rag__ModelPath=/models/embeddinggemma-2-onnx` and `Rag__IndexPath=/data/index`.

### From source (.NET 10 SDK)

```bash
export PBRAG_CONNECTION_STRING='Server=localhost,1433;User Id=sa;Password=Pbrag_Dev_Pass1!;TrustServerCertificate=True;Encrypt=False'
export PBRAG_MODEL_PATH=~/models/embeddinggemma-2-onnx
export PBRAG_INDEX_PATH=./index

dotnet run --project Pixelbadger.Toolkit.Rag --urls http://localhost:8080
```

The service refuses to start (with a clear message and exit code 1) when the index path, connection string or model path is missing, or the model directory does not exist. Validation never loads the model or connects to SQL.

> **One instance per index.** The Lucene index directory has a single writer, and the ingest worker processes one job at a time. Run exactly one instance per index directory and database; multiple instances are not supported.

## Configuration

Configuration is read from the `Rag` section: `appsettings.json`, environment variables (`Rag__ConnectionString`, `Rag__Ingest__MaxFilesPerJob`, ...), or command-line arguments (`--Rag:ModelPath=...`).

| Key | Fallback env var | Default | Description |
|---|---|---|---|
| `Rag:IndexPath` | `PBRAG_INDEX_PATH` | | **Required.** Lucene BM25 index directory (created if missing). |
| `Rag:ConnectionString` | `PBRAG_CONNECTION_STRING` | | **Required.** SQL Server 2025 / Azure SQL connection string. |
| `Rag:ModelPath` | `PBRAG_MODEL_PATH` | | **Required.** Local EmbeddingGemma 2 ONNX snapshot directory (must exist). |
| `Rag:ExactVectorSearch` | | `false` | Always use exact `VECTOR_DISTANCE` instead of the approximate vector index. |
| `Rag:ApplyMigrationsOnStartup` | | `true` | Apply EF Core migrations when the host starts, before the ingest worker. |
| `Rag:Ingest:MaxFileSizeBytes` | | 10 MiB | Largest accepted single file. Also drives the multipart and request-body limits (`MaxFilesPerJob x MaxFileSizeBytes` plus slack). |
| `Rag:Ingest:MaxFilesPerJob` | | `100` | Most files in one upload. |
| `Rag:Ingest:MaxChunkCharacters` | | `20000` | Largest text chunk. A request may lower it, never raise it. |
| `Rag:Ingest:MaxAttempts` | | `3` | How many times a job may be claimed before it is marked `Failed`. |
| `Rag:Ingest:LeaseSeconds` | | `600` | How long a worker owns a job; the worker renews the lease while it works. A job whose lease lapses (crash, restart) is retried. |
| `Rag:Ingest:PollIntervalSeconds` | | `2` | How often the worker polls an empty queue (a new upload wakes it immediately). |

The SQL database and the Lucene index belong together: use one index directory per SQL database, and re-ingest if you delete either.

## HTTP API

Errors are [RFC 9457 problem details](https://www.rfc-editor.org/rfc/rfc9457) (`application/problem+json`). Unexpected failures return a generic `500`; the details are only in the server log. There is no authentication: put the service behind a reverse proxy or network boundary of your own.

| Method | Route | Description |
|---|---|---|
| `POST` | `/api/ingest` | Upload files (multipart) and enqueue an ingest job. |
| `GET` | `/api/ingest/{jobId}` | Job status. |
| `POST` | `/api/query` | Hybrid search. |
| any | `/mcp` | MCP Streamable HTTP (`Search` tool). |
| `GET` | `/health` | Liveness (touches neither SQL nor the model). |

### Ingest

Send one or more `files` parts. **Each part's filename is the logical source path** of the document (it determines the document id, the source path, the source id (file name without extension) and the title), so give it a stable relative path such as `docs/guide.md`. It must be relative and made of plain segments: no `..`, no leading `/` or drive letter, no empty segments. Re-uploading the same logical path **replaces** that document's previous chunks. An optional `maxChunkCharacters` field lowers the chunk limit for the job.

```bash
curl -i -X POST http://localhost:8080/api/ingest \
  -F "files=@./docs/guide.md;filename=docs/guide.md" \
  -F "files=@./photos/mars.jpg;filename=photos/mars.jpg" \
  -F "files=@./talks/intro.mp3;filename=talks/intro.mp3"
```

The whole request is validated before anything is stored: at least one file, at most `MaxFilesPerJob`, each at most `MaxFileSizeBytes`, a supported extension, a safe and unique logical path. Any violation returns `400` and enqueues nothing. On success you get `202 Accepted` with `Location: /api/ingest/{jobId}` and the job status:

```json
{
  "jobId": "0f8fad5b-d9cb-469f-a165-70867728950e",
  "status": "Queued",
  "attempts": 0,
  "createdAtUtc": "2026-10-06T12:00:00Z",
  "startedAtUtc": null,
  "completedAtUtc": null,
  "error": null,
  "files": [
    { "path": "docs/guide.md", "status": "Queued", "documentId": null, "modality": null, "chunkCount": null, "error": null }
  ],
  "summary": { "succeeded": 0, "failed": 0, "skipped": 0 }
}
```

Poll the `Location` URL until the job is terminal:

```bash
curl http://localhost:8080/api/ingest/0f8fad5b-d9cb-469f-a165-70867728950e
```

- Job `status`: `Queued`, `Processing`, `Completed` or `Failed`. `Completed` means every file reached a terminal state: per-file failures do **not** fail the job, look at each file's `status` (`Queued`, `Succeeded`, `Failed`, `Skipped` for files with no content) and `error`. `Failed` means the job itself failed `MaxAttempts` times.
- Uploaded bytes are stored in SQL until each file finishes and are then discarded.
- A worker crash or restart is safe: the job's lease expires, the job is picked up again, and files that already finished are skipped.

### Query

```bash
curl -X POST http://localhost:8080/api/query \
  -H 'Content-Type: application/json' \
  -d '{ "query": "why is Mars red", "maxResults": 5, "sourceIds": ["guide"] }'
```

| Field | Default | Description |
|---|---|---|
| `query` (required) | | Query text (up to 4096 characters). |
| `maxResults` | 10 | 1 to 100. |
| `sourceIds` | | Restrict results to these source ids (up to 100, 256 characters each). |

A blank query or invalid argument returns `400`. The response is `{ "results": [...] }`; each result has `score`, `chunkId`, `documentId`, `sourcePath` (the logical path), `sourceFile`, `sourceId`, `ordinal`, `modality` (`Text`, `Image` or `Audio`), `locatorStart` / `locatorEnd` (character offsets for text, milliseconds for audio, null for images), `content` (text chunks only), and `keywordRank` / `vectorRank` (1-based rank in each list, null when absent from it).

## MCP server

`/mcp` is a [Streamable HTTP](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports#streamable-http) MCP endpoint (stateless: no session affinity needed) exposing the `Search` tool only.

### Client configuration

Claude Code:

```bash
claude mcp add --transport http rag-search http://localhost:8080/mcp
```

Other clients that take a JSON config:

```json
{
  "mcpServers": {
    "rag-search": {
      "type": "http",
      "url": "http://localhost:8080/mcp"
    }
  }
}
```

### Tool: `Search`

| Parameter | Type | Description |
|---|---|---|
| `query` | string, required | The search query. |
| `maxResults` | int, default 5 | Maximum number of results (1 to 100). |
| `sourceIds` | string[], optional | Restrict results to specific source ids. |

There is no search-mode parameter: every search is hybrid. Results carry the chunk id, document id, source, source id, modality, locator and content. All returned content is framed as **untrusted document text**: clients must treat it as data, not instructions. Invalid arguments return their message as a tool error; unexpected failures return a generic error and are logged.

## Supported content

| Kind | Extensions | How it is indexed |
|---|---|---|
| Text | `.txt`, `.md` | Paragraph chunks (`.txt`) or header sections (`.md`); one chunk per row, with character-offset locators. Embedded with the EmbeddingGemma document prompt. |
| Image | `.png` `.jpg` `.jpeg` `.webp` `.gif` `.bmp` `.tif` `.tiff` | One chunk per image, embedded by the vision encoder (not OCR'd). Matched by text queries; results show `[image]`. |
| Audio | `.wav` `.mp3` `.m4a` `.flac` `.ogg` `.opus` `.aac` | About 30-second windows, one chunk each, with millisecond locators. Requires ffmpeg. Results show `[audio mm:ss–mm:ss]`. |

Video and images embedded inside documents are not supported. Only text chunks participate in BM25; image and audio chunks are found through the vector side of the hybrid search.

## How it works

```
Ingest:  POST /api/ingest (multipart) -> validate -> SQL queue (job + file bytes) -> 202
         worker: claim job -> per file: temp file -> modality routing (extension)
              text : reader -> chunker -> EmbeddingGemma 2 (text)
              image: decode -> patchify -> vision encoder -> EmbeddingGemma 2
              audio: ffmpeg -> log-mel windows -> audio encoder -> EmbeddingGemma 2
          -> SQL Server (Document, Chunk + vector(256))  -> Lucene (text chunks, keyed by chunk id)
         -> mark file done (bytes discarded) -> job done -> create vector index (once per job)

Search:  query -> [Lucene BM25 top-N | SQL vector top-N] -> RRF (k = 60) -> hydrate from SQL -> results
```

**Domain model.** A `Document` is one ingested file; it has an integer primary key and a unique, deterministic global id (`doc_` + the first 32 hex characters of SHA-256 over the file's *logical path*, separators normalised to `/`). A `Document` has many `Chunk`s; a chunk has an integer clustered primary key (the id Lucene stores), a unique `Guid` global id (shown to users as the chunk id), the document foreign key, its ordinal, modality, locator range, text (text chunks only) and the 256-d embedding. Chunks are stored in `dbo.Chunks_EG2_256`. The `EG2_256` suffix names the model and dimension; changing either means a new table and a full re-embed.

**Hybrid search.** BM25 and vector search each fetch `max(2n, 20)` candidates; ranks are fused with `1 / (60 + rank)` and summed for chunks that appear in both lists. Final results are hydrated from SQL, so Lucene never needs to store content.

**Embeddings.** 256 dimensions (the first 256 of the model's Matryoshka output, re-normalised to unit length). Queries and documents use EmbeddingGemma's asymmetric prompts.

**Job queue.** `dbo.IngestJobs` / `dbo.IngestJobFiles` hold the queue. A hosted worker claims the oldest claimable job with an atomic `UPDATE ... WITH (UPDLOCK, READPAST)` that also sets a lease; the lease is renewed while the job runs, and an expired lease makes the job claimable again until `MaxAttempts` is reached. Migrations run in a hosted service registered before the worker.

**Privacy.** Chunk text and logical source paths are stored in SQL Server in plaintext. Protect the database and avoid ingesting secrets unless that is acceptable.

## Development and testing

```bash
dotnet build
dotnet test
```

- Tests use **xUnit, FluentAssertions and Moq**. SQL-backed tests (store, pipeline, ingest queue, worker) run against a SQL Server 2025 container started by **Testcontainers** (Docker required; shared via the `SqlServer` collection fixture).
- To reuse an existing SQL Server instead of starting a container, set `PBRAG_TEST_SQL_CONNECTION_STRING` to its connection string.
- API and MCP tests host the real application in-process with `WebApplicationFactory<Program>` (mocked search service and queue, no worker, no SQL, no model).
- Embedding-dependent tests use a deterministic mock embedding service. **Golden tests** that compare against the real ONNX model run only when `PBRAG_MODEL_PATH` points at a local model; otherwise they are skipped. Reference vectors are generated with the transformers.js scripts in [`tools/golden`](tools/golden).
- CI (`.github/workflows`) runs on .NET 10, builds the container image (without pushing it) and, on pull requests that change `Pixelbadger.Toolkit.Rag/`, requires a version bump over the latest NuGet release.

## Evaluations

[`EVALS.md`](EVALS.md) and [`docs/bm25-vs-hybrid-analysis.md`](docs/bm25-vs-hybrid-analysis.md) are historical (pre-2.0, OpenAI embeddings, the old CLI) and kept for context.

## License

MIT
