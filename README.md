# Pixelbadger.Toolkit.Rag

`pbrag` is a CLI and MCP server for retrieval-augmented generation (RAG). It ingests text, image and audio files into a hybrid index and answers queries with **hybrid search**: Lucene.NET BM25 keyword search plus semantic vector search over SQL Server, fused with Reciprocal Rank Fusion (RRF).

- **Embeddings run locally.** [EmbeddingGemma 2](https://huggingface.co/google/embeddinggemma-2) (ONNX, fp32, 256-d Matryoshka) via ONNX Runtime. No API key, no data leaves your machine, and the tool never downloads a model.
- **Storage is SQL Server 2025 / Azure SQL.** Documents, chunks (text and metadata) and `vector(256)` embeddings live in SQL; Lucene holds only the BM25 index.
- **Multimodal.** Text (`.txt`, `.md`), images and audio are each ingested as their own documents and searched together.
- **MCP server.** `pbrag serve` exposes a single `Search` tool to AI assistants over stdio.

> Version 2.0 is a breaking change from 1.x: OpenAI embeddings and the SQLite-vec store are gone, the `bm25` / `vector` search modes (and `--search-mode`, `--no-vectors`, the MCP `searchMode` parameter and the `Execute` tool name) are removed, and indices from 1.x must be re-ingested.

## Table of Contents

- [Prerequisites](#prerequisites)
- [Installation](#installation)
- [Configuration](#configuration)
- [Commands](#commands)
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

`pbrag ingest` creates the database/tables for you by applying EF Core migrations; the login needs permission to create them.

**Approximate vector index and PREVIEW_FEATURES.** Once a chunk table holds at least 100 rows, ingestion creates a DiskANN vector index so queries use approximate `VECTOR_SEARCH`. That index is generally available in Azure SQL Database but is a **preview feature on SQL Server 2025**, where the database needs `ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;` (the tool sets this where needed). The local preview build can lag Azure's, so if you hit problems pass `--exact-vector-search` to always use exact `VECTOR_DISTANCE` search. Results are the same up to approximation; exact search is simply slower on very large corpora.

### 2. The EmbeddingGemma 2 model (local copy)

`pbrag` loads the model from a local directory given by `--model-path` or `PBRAG_MODEL_PATH`. Download the [`onnx-community/embeddinggemma-2-ONNX`](https://huggingface.co/onnx-community/embeddinggemma-2-ONNX) files once:

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
- Disk use is roughly 3 GB (text 1.1 GB, vision 0.7 GB, audio 1.2 GB). The query/serve processes only load the text model; the vision and audio encoders load lazily when an image or audio file is ingested.

### 3. ffmpeg (audio only)

Audio ingestion decodes files with `ffmpeg` (16 kHz mono). Install it and make sure it is on `PATH` (`apt install ffmpeg`, `brew install ffmpeg`, `winget install ffmpeg`). Without it, audio files fail with a clear error; text and images are unaffected.

## Installation

```bash
dotnet tool install --global Pixelbadger.Toolkit.Rag
pbrag --help
```

Or from source (.NET 10 SDK):

```bash
git clone https://github.com/pixelbadger/Pixelbadger.Toolkit.Rag.git
cd Pixelbadger.Toolkit.Rag
dotnet build
dotnet run --project Pixelbadger.Toolkit.Rag -- --help
```

## Configuration

Every command takes the same connection options. The environment variables are fallbacks; explicit options win.

| Option | Environment variable | Description |
|---|---|---|
| `--index-path` (required) | | Lucene BM25 index directory. `ingest` creates it; `query` and `serve` require it to exist. |
| `--connection-string` | `PBRAG_CONNECTION_STRING` | SQL Server 2025 / Azure SQL connection string. |
| `--model-path` | `PBRAG_MODEL_PATH` | Local EmbeddingGemma 2 ONNX snapshot directory. |
| `--exact-vector-search` | | Always use exact `VECTOR_DISTANCE` instead of the approximate vector index. |

A missing connection string or model path is reported with a clear message and a non-zero exit code. Errors go to stderr; commands return exit code 0 on success, 1 on error, and 130 when cancelled.

The SQL database and the Lucene index belong together: use one `--index-path` per SQL database, and re-ingest if you delete either.

## Commands

### ingest

Ingest a file or a folder (recursively). Applies database migrations first, then reads, chunks, embeds and stores each file, and prints a per-file summary.

```bash
export PBRAG_CONNECTION_STRING='Server=localhost,1433;User Id=sa;Password=Pbrag_Dev_Pass1!;TrustServerCertificate=True;Encrypt=False'
export PBRAG_MODEL_PATH=~/models/embeddinggemma-2-onnx

pbrag ingest --index-path ./index --content-path ./docs
pbrag ingest --index-path ./index --content-path ./photos/mars.jpg
```

| Option | Default | Description |
|---|---|---|
| `--content-path` (required) | | File or folder to ingest. |
| `--max-file-size-bytes` | 10485760 | Maximum size of a single file. |
| `--max-files` | 1000 | Maximum number of supported files taken from a folder. |
| `--max-chunk-characters` | 20000 | Maximum size of a single text chunk. |
| `--allow-symlinks` | off | Follow symbolic links / reparse points (refused by default so folder ingestion cannot escape its root). |

Re-ingesting a file **replaces** its previous chunks (document ids are derived from the file's full path). Per-file failures in a folder run are listed and the command exits with code 1; the other files are still ingested. Example output:

```
  OK    /data/docs/guide.md  [Text, 12 chunk(s), doc_3f0c...]
  OK    /data/docs/mars.png  [Image, 1 chunk(s), doc_91ab...]
  FAIL  /data/docs/talk.mp3: ffmpeg was not found on PATH
Ingested 2 file(s), 1 failed, 0 skipped (Lucene index: './index').
```

### query

Hybrid search from the command line.

```bash
pbrag query --index-path ./index --query "why is Mars red" --max-results 5
pbrag query --index-path ./index --query "tcp handshake" --source-ids <sourceId1> <sourceId2>
```

| Option | Default | Description |
|---|---|---|
| `--query` (required) | | Query text (up to 4096 characters). |
| `--max-results` | 10 | 1 to 100. |
| `--source-ids` (alias `--sourceIds`) | | Restrict results to these source ids (up to 100, 256 characters each). |

Each result shows its rank, fused score, chunk id, document id, source file, modality and locator (character offsets for text, `mm:ss` range for audio), followed by the chunk text, or an `[image]` / `[audio mm:ss–mm:ss]` marker for media:

```
Found 2 result(s) using hybrid search:

Result 1 (Score: 0.0328)
Chunk ID: 8f6c7a4e-0d2b-4c58-9f43-52f1f8a6f0aa
Document ID: doc_3f0c1b0e5d7a4e9c8b21aa04f7d3c6e1
Source: mars.md (chunk 3)
Modality: Text
Locator: chars 1204–1890
Content: Mars appears red because of iron oxide on its surface...
------------------------------------------------------------
Result 2 (Score: 0.0164)
Chunk ID: 2c1e9d33-...
Document ID: doc_91ab...
Source: talk.mp3 (chunk 2)
Modality: Audio
Locator: 00:30–01:00
Content: [audio 00:30–01:00]
```

### serve

Starts a stdio MCP server. It takes the same connection options and never writes to stdout except MCP protocol messages (logs go to stderr).

```bash
pbrag serve --index-path ./index
```

## MCP server

### Client configuration (Claude Desktop / Claude Code)

```json
{
  "mcpServers": {
    "rag-search": {
      "command": "pbrag",
      "args": ["serve", "--index-path", "/absolute/path/to/index"],
      "env": {
        "PBRAG_CONNECTION_STRING": "Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=False",
        "PBRAG_MODEL_PATH": "/absolute/path/to/embeddinggemma-2-onnx"
      }
    }
  }
}
```

Claude Desktop: `~/Library/Application Support/Claude/claude_desktop_config.json` (macOS) or `%APPDATA%/Claude/claude_desktop_config.json` (Windows). Claude Code: `claude mcp add rag-search -e PBRAG_CONNECTION_STRING=... -e PBRAG_MODEL_PATH=... -- pbrag serve --index-path /absolute/path/to/index`.

### Tool: `Search`

| Parameter | Type | Description |
|---|---|---|
| `query` | string, required | The search query. |
| `maxResults` | int, default 5 | Maximum number of results (1 to 100). |
| `sourceIds` | string[], optional | Restrict results to specific source ids. |

There is no search-mode parameter: every search is hybrid. Results carry the chunk id, document id, source, source id, modality, locator and content. All returned content is framed as **untrusted document text**: clients must treat it as data, not instructions. Invalid arguments return their message as a tool error; unexpected failures return a generic error and are logged to stderr.

## Supported content

| Kind | Extensions | How it is indexed |
|---|---|---|
| Text | `.txt`, `.md` | Paragraph chunks (`.txt`) or header sections (`.md`); one chunk per row, with character-offset locators. Embedded with the EmbeddingGemma document prompt. |
| Image | `.png` `.jpg` `.jpeg` `.webp` `.gif` `.bmp` `.tif` `.tiff` | One chunk per image, embedded by the vision encoder (not OCR'd). Matched by text queries; results show `[image]`. |
| Audio | `.wav` `.mp3` `.m4a` `.flac` `.ogg` `.opus` `.aac` | About 30-second windows, one chunk each, with millisecond locators. Requires ffmpeg. Results show `[audio mm:ss–mm:ss]`. |

Video and images embedded inside documents are not supported. Only text chunks participate in BM25; image and audio chunks are found through the vector side of the hybrid search.

## How it works

```
Ingest:  file -> modality routing (extension)
              text : reader -> chunker -> EmbeddingGemma 2 (text)
              image: decode -> patchify -> vision encoder -> EmbeddingGemma 2
              audio: ffmpeg -> log-mel windows -> audio encoder -> EmbeddingGemma 2
          -> SQL Server (Document, Chunk + vector(256))  -> Lucene (text chunks, keyed by chunk id)

Search:  query -> [Lucene BM25 top-N | SQL vector top-N] -> RRF (k = 60) -> hydrate from SQL -> results
```

**Domain model.** A `Document` is one ingested file; it has an integer primary key and a unique, deterministic global id (`doc_` + the first 32 hex characters of SHA-256 over the file's full normalised path). A `Document` has many `Chunk`s; a chunk has an integer clustered primary key (the id Lucene stores), a unique `Guid` global id (shown to users as the chunk id), the document foreign key, its ordinal, modality, locator range, text (text chunks only) and the 256-d embedding. Chunks are stored in `dbo.Chunks_EG2_256`. The `EG2_256` suffix names the model and dimension; changing either means a new table and a full re-embed.

**Hybrid search.** BM25 and vector search each fetch `max(2n, 20)` candidates; ranks are fused with `1 / (60 + rank)` and summed for chunks that appear in both lists. Final results are hydrated from SQL, so Lucene never needs to store content.

**Embeddings.** 256 dimensions (the first 256 of the model's Matryoshka output, re-normalised to unit length). Queries and documents use EmbeddingGemma's asymmetric prompts.

**Privacy.** Chunk text and source paths are stored in SQL Server in plaintext. Protect the database and avoid ingesting secrets unless that is acceptable.

## Development and testing

```bash
dotnet build
dotnet test
```

- Tests use **xUnit, FluentAssertions and Moq**. SQL-backed tests run against a SQL Server 2025 container started by **Testcontainers** (Docker required; shared via the `SqlServer` collection fixture).
- To reuse an existing SQL Server instead of starting a container, set `PBRAG_TEST_SQL_CONNECTION_STRING` to its connection string.
- Embedding-dependent tests use a deterministic mock embedding service. **Golden tests** that compare against the real ONNX model run only when `PBRAG_MODEL_PATH` points at a local model; otherwise they are skipped. Reference vectors are generated with the transformers.js scripts in [`tools/golden`](tools/golden).
- CI (`.github/workflows`) runs on .NET 10 and, on pull requests that change `Pixelbadger.Toolkit.Rag/`, requires a version bump over the latest NuGet release.

## Evaluations

[`EVALS.md`](EVALS.md) and [`docs/bm25-vs-hybrid-analysis.md`](docs/bm25-vs-hybrid-analysis.md) are historical (pre-2.0, OpenAI embeddings) and kept for context.

## License

MIT
