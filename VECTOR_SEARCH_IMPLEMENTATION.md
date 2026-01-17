# Vector Similarity Search Implementation

## Overview
Adding vector similarity search using sqlite-vec to store embeddings generated during ingest. This complements the existing Lucene BM25 inverted index with semantic vector search.

## Status: Build Errors to Fix

### Current Build Error
```
VectorStore.cs(91,86): error CS1061: 'Task' does not contain a definition for 'WithCancellation'
```

The `UpsertAsync` with `IEnumerable` returns `IAsyncEnumerable<string>`, not `Task`. Fix needed in `UpsertChunksBatchAsync`.

## Completed Tasks

### 1. NuGet Packages Added (`.csproj`)
```xml
<PackageReference Include="Microsoft.Extensions.VectorData.Abstractions" Version="9.7.0" />
<PackageReference Include="Microsoft.SemanticKernel.Connectors.SqliteVec" Version="1.68.0-preview" />
```

### 2. `Components/IEmbeddingService.cs` - Created
- `IEmbeddingService` interface with `GenerateEmbeddingAsync()` and `GenerateEmbeddingsAsync()`
- `OpenAIEmbeddingService` using text-embedding-3-large (3072 dimensions)
- Uses `Microsoft.Extensions.AI` abstractions
- Key: Use `GenerateAsync()` extension method, not `GenerateEmbeddingAsync()`

### 3. `Components/VectorStore.cs` - Created
- `ChunkVectorRecord` with correct attributes:
  - `[VectorStoreKey]` (not VectorStoreRecordKey)
  - `[VectorStoreData]` (not VectorStoreRecordData)
  - `[VectorStoreVector(3072, DistanceFunction = DistanceFunction.CosineSimilarity)]`
- Uses `SqliteCollection<string, ChunkVectorRecord>` and `SqliteVectorStore`
- Storage at `{indexPath}/vectors.db`

**Fix needed in `UpsertChunksBatchAsync`:**
```csharp
// Current (broken):
await foreach (var _ in _collection!.UpsertAsync(records, cancellationToken).WithCancellation(cancellationToken))

// Should be (UpsertAsync with IEnumerable already returns IAsyncEnumerable):
await foreach (var _ in _collection!.UpsertAsync(records, cancellationToken))
{
}
```

### 4. `Components/SearchIndexer.cs` - Modified
Added:
- `IngestOptions` class with `EnableVectorStorage` flag
- `SearchMode` enum: `Bm25`, `Vector`, `Hybrid`
- `SetEmbeddingService()` method
- Overloaded `IngestContentAsync()` that accepts `IngestOptions`
- `IndexWithLuceneAsync()` - extracted Lucene indexing
- `StoreVectorsAsync()` - generates embeddings and stores in VectorStore
- `VectorQueryAsync()` - vector-only search
- `SearchAsync()` - unified search with mode parameter
- `HybridQueryAsync()` - combines BM25 + vector with Reciprocal Rank Fusion (RRF)

### 5. `Commands/IngestCommand.cs` - Modified
- Added `--enable-vectors` flag (default: false)
- Creates `OpenAIEmbeddingService` when enabled
- Passes `IngestOptions` to `IngestContentAsync()`

### 6. `Commands/QueryCommand.cs` - Modified
- Added `--search-mode` option: `bm25` (default), `vector`, `hybrid`
- Creates `OpenAIEmbeddingService` for vector/hybrid modes
- Uses `SearchAsync()` with mode parameter

### 7. `Components/McpRagServer.cs` - Modified
- Added `searchMode` parameter to `Execute()` tool
- Lazy initialization of `IEmbeddingService`
- Uses `SearchAsync()` with mode parameter

## API Notes (Important for Future Reference)

### Microsoft.Extensions.VectorData.Abstractions (9.7.0)
- Attributes: `VectorStoreKey`, `VectorStoreData`, `VectorStoreVector`
- `VectorStoreVector(int dimensions, DistanceFunction = ...)` - DistanceFunction is a named parameter
- `VectorSearchOptions<T>` - no `Top` property, pass count to `SearchAsync()`
- Score is `double`, not `float` - cast needed

### Microsoft.SemanticKernel.Connectors.SqliteVec (1.68.0-preview)
- `SqliteVectorStore(connectionString)`
- `SqliteCollection<TKey, TRecord>(connectionString, collectionName)`
- `EnsureCollectionExistsAsync()` - creates collection
- `UpsertAsync(record)` - single record, returns `Task`
- `UpsertAsync(records)` - IEnumerable, returns `IAsyncEnumerable<TKey>`
- `SearchAsync(embedding, top, options?, ct)` - returns `IAsyncEnumerable<VectorSearchResult<T>>`

### Microsoft.Extensions.AI
- `IEmbeddingGenerator<string, Embedding<float>>`
- Use extension `GenerateAsync(text)` for single, `GenerateAsync(texts)` for batch
- Returns `GeneratedEmbedding<float>` with `.Vector` property

## Files Changed
1. `Pixelbadger.Toolkit.Rag.csproj` - NuGet packages
2. `Components/IEmbeddingService.cs` - NEW
3. `Components/VectorStore.cs` - NEW
4. `Components/SearchIndexer.cs` - Modified
5. `Commands/IngestCommand.cs` - Modified
6. `Commands/QueryCommand.cs` - Modified
7. `Components/McpRagServer.cs` - Modified

## Next Steps
1. Fix the `UpsertChunksBatchAsync` method - remove `.WithCancellation()` call
2. Build and verify: `dotnet build`
3. Run tests: `dotnet test`
4. Manual test with `--enable-vectors` and `--search-mode vector`
