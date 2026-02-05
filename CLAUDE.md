# CLAUDE.md - Agent Guidelines for Pixelbadger.Toolkit.Rag

## Project Overview

A .NET 9.0 CLI tool (`pbrag`) for Retrieval-Augmented Generation (RAG) that provides BM25, vector, and hybrid search over document indices. Designed for use as an MCP server for AI assistants like Claude.

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

## Quick Reference

```bash
# Build and test
dotnet build
dotnet test

# CLI commands
pbrag ingest --index-path ./index --content-path ./docs
pbrag query --index-path ./index --query "search term" --search-mode hybrid
pbrag serve --index-path ./index  # MCP server mode
```

## Architecture

### Project Structure

```
Pixelbadger.Toolkit.Rag/
├── Commands/                    # CLI command handlers (System.CommandLine)
│   ├── IngestCommand.cs        # File/folder ingestion
│   ├── QueryCommand.cs         # Search operations
│   └── ServeCommand.cs         # MCP server
├── Components/                  # Core business logic
│   ├── FileReaders/            # File reading abstraction (.txt, .md)
│   ├── ContentIngester.cs      # Ingestion orchestration
│   ├── SearchService.cs        # Search orchestration
│   ├── LuceneRepository.cs     # BM25 indexing/search
│   ├── VectorRepository.cs     # Vector embedding storage
│   ├── McpRagServer.cs         # MCP tool endpoint
│   ├── ChunkerFactory.cs       # Text chunking strategy selection
│   ├── RrfReranker.cs          # Hybrid search fusion
│   └── DependencyInjection.cs  # DI registration
├── Dtos/                       # Data models
│   ├── SearchResult.cs
│   ├── SearchMode.cs
│   └── IngestOptions.cs
└── Program.cs                  # Entry point
```

### Key Abstractions

| Interface | Purpose | Implementations |
|-----------|---------|-----------------|
| `IContentIngester` | Ingestion pipeline | `ContentIngester` |
| `ISearchService` | Search orchestration | `SearchService` |
| `ILuceneRepository` | BM25 index operations | `LuceneRepository` |
| `IVectorRepository` | Vector storage/search | `VectorRepository` |
| `ITextChunker` | Content chunking | `ParagraphTextChunker`, `MarkdownTextChunker` |
| `IFileReader` | File reading | `PlainTextFileReader`, `MarkdownFileReader` |
| `IReranker` | Result fusion | `RrfReranker` |
| `IEmbeddingService` | Embedding generation | `OpenAIEmbeddingService` |

### Data Flow

**Ingestion:**
```
File → FileReader → Chunker → [LuceneRepository + VectorRepository]
```

**Search:**
```
Query → SearchService → [BM25 | Vector | Hybrid(RRF)] → SearchResults
```

## Code Conventions

### Dependency Injection

All services registered in `DependencyInjection.cs`:
- Use constructor injection
- Register interfaces to implementations
- Transient lifetime for stateless services
- Singleton for expensive resources (embedding generator)

### Factory Pattern

Used for extension-based routing:
- `ChunkerFactory.GetChunker(filePath)` - `.md` → Markdown, others → Paragraph
- `FileReaderFactory.GetReader(filePath)` - Extension-based reader selection

### Async/Await

All I/O operations are async:
- Repository methods return `Task<T>`
- Use `await` consistently, avoid `.Result` or `.Wait()`

### Error Handling

- Throw specific exceptions: `FileNotFoundException`, `DirectoryNotFoundException`
- Commands catch exceptions and print to console with `Environment.Exit(1)`

## Testing

### Test Project Structure

```
Pixelbadger.Toolkit.Rag.Tests/
├── SearchIndexerTests.cs               # Primary integration tests
├── SearchSimilarityConsistencyTests.cs # BM25 behavior tests
└── MockEmbeddingService.cs             # Deterministic embeddings for tests
```

### Test Patterns

```csharp
// Setup: Build full component graph
var luceneRepo = new LuceneRepository();
var vectorRepo = new VectorRepository(new MockEmbeddingService());
var ingester = new ContentIngester(luceneRepo, vectorRepo, chunkerFactory, fileReaderFactory);
var searchService = new SearchService(luceneRepo, vectorRepo, reranker);

// Tests use IDisposable for temp directory cleanup
public class Tests : IDisposable {
    private readonly string _testDirectory;
    public void Dispose() => Directory.Delete(_testDirectory, true);
}

// FluentAssertions for assertions
results.Should().HaveCount(1);
results[0].Score.Should().BeInRange(0.1f, 1.0f);
await act.Should().ThrowAsync<FileNotFoundException>();
```

### MockEmbeddingService

Generates deterministic 3072-dimensional embeddings based on text hash. Use for all tests to avoid OpenAI API calls.

## CI/CD

### GitHub Actions Workflows

| Workflow | Trigger | Purpose |
|----------|---------|---------|
| `pr-validation.yml` | PR to master | Build, test, validate version bump |
| `publish-to-nuget.yml` | Push to master | Build, test, publish to NuGet.org |

### Version Requirements

**IMPORTANT:** PRs that modify code in `Pixelbadger.Toolkit.Rag/` (excluding tests) MUST increment the version.

The CI runs `.github/scripts/check-version-increment.ps1` which:
1. Reads `<Version>` from the `.csproj`
2. Queries NuGet.org for the latest published version
3. Fails if current version ≤ published version

### How to Bump Version

Edit `Pixelbadger.Toolkit.Rag/Pixelbadger.Toolkit.Rag.csproj`:
```xml
<Version>X.Y.Z</Version>
```

Use semantic versioning:
- **Major** (X): Breaking API changes
- **Minor** (Y): New features, backward compatible
- **Patch** (Z): Bug fixes, backward compatible

### Secrets Required

| Secret | Purpose |
|--------|---------|
| `NUGET_API_KEY` | API key for publishing to NuGet.org |

### CI Behavior Notes

- Test failures use `continue-on-error: true` (won't block publish)
- Path filters exclude `.github/` and test project changes from triggering version checks
- Publish uses `--skip-duplicate` to handle re-runs gracefully

## Search Modes

| Mode | Implementation | Best For |
|------|---------------|----------|
| `Bm25` | Lucene.NET keyword search | Technical docs, exact terms |
| `Vector` | SQLite-vec semantic search | Natural language queries |
| `Hybrid` | RRF fusion of both | General purpose |

**Hybrid Search Algorithm (RRF):**
1. Fetch 2x results from both BM25 and Vector
2. Score each result: `1 / (60 + rank)`
3. Sum scores for documents appearing in both
4. Return top N by fused score

## Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `OPENAI_API_KEY` | Yes (for vector ops) | OpenAI API key for embeddings |

## Index Storage

- **Lucene index:** `{index-path}/` directory with Lucene files
- **Vector database:** `{index-path}/vectors.db` SQLite file

## MCP Server Integration

The `serve` command exposes a single MCP tool:

```json
{
  "name": "Execute",
  "parameters": {
    "query": "required string",
    "maxResults": "optional int, default 5",
    "sourceIds": "optional string[]",
    "searchMode": "optional: bm25|vector|hybrid, default bm25"
  }
}
```

## Key Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Lucene.Net | 4.8.0-beta | BM25 search |
| Microsoft.SemanticKernel.Connectors.SqliteVec | 1.68.0-preview | Vector storage |
| OpenAI | 2.8.0 | Embeddings (text-embedding-3-large) |
| System.CommandLine | 2.0.0-beta | CLI parsing |
| ModelContextProtocol | 0.3.0-preview | MCP server |

## Common Tasks

### Adding a New File Type

1. Create `IFileReader` implementation in `Components/FileReaders/`
2. Register in `DependencyInjection.cs`
3. (Optional) Create custom `ITextChunker` if special chunking needed
4. Update `ChunkerFactory` if new chunker

### Adding a New Search Mode

1. Add enum value to `SearchMode.cs`
2. Implement search logic in `SearchService.cs`
3. Update CLI parser in `QueryCommand.cs` and `McpRagServer.cs`

### Modifying Ingestion Pipeline

Key file: `ContentIngester.cs`
- `IngestContentAsync()` - single file ingestion
- `IngestFolderAsync()` - recursive folder ingestion
- Both call: read → chunk → filter empty → store in Lucene + Vector

## Troubleshooting

**"Index directory not found"**: Run `ingest` before `query`/`serve`

**Empty search results**: Check that content was chunked (non-empty paragraphs)

**Vector search slow**: First query initializes SQLite-vec; subsequent queries faster

**OpenAI rate limits**: Built-in retry with exponential backoff (5 attempts, respects Retry-After)
