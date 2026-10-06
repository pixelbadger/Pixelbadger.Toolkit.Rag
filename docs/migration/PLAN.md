# Migration plan: EmbeddingGemma 2 (ONNX) + SQL Server + multimodal

Technical reference: [`embeddinggemma2-reference.md`](embeddinggemma2-reference.md) ("the reference", cited as §N).

## Decisions (agreed with the owner)

| Topic | Decision |
|---|---|
| Embeddings | Local `onnx-community/embeddinggemma-2-ONNX`, fp32, via `Microsoft.ML.OnnxRuntime`. 256-d Matryoshka (truncate + re-normalise). OpenAI removed. |
| Model files | **Local path only**: `--model-path` or `PBRAG_MODEL_PATH`. The tool never downloads anything. |
| Persistence | **SQL Server 2025 / Azure SQL** for documents, chunks (text + metadata) and vectors (EF Core 10, `vector(256)`). |
| BM25 | **Lucene.NET stays**, but only as the BM25 index. Lucene docs carry `chunk_id`, `document_id`, `source_id` and the indexed text. All content shown to users is hydrated from SQL. |
| Search modes | **Hybrid only** (BM25 + vector, RRF). The standalone `bm25` / `vector` modes and the `--search-mode` / `searchMode` parameters are removed. |
| Multimodal | Image files and audio files, each ingested as its own document. Video and images embedded inside documents are out of scope. |
| Domain | `Document` (int PK + unique `GlobalId`) 1-* `Chunk` (int clustered PK + unique `GlobalId` Guid + `DocumentId` FK). |
| Upgrades | net10.0, EF Core 10, System.CommandLine 2.0 GA, ModelContextProtocol 2.x, Microsoft.Extensions.Hosting 10. Removed: Microsoft.Extensions.AI(.OpenAI), OpenAI, SemanticKernel SqliteVec, VectorData.Abstractions. |
| Version | 2.0.0 (breaking). |

## Phase 0 (done by the orchestrator, already on the branch)

Contracts and stubs; the solution builds and all tests pass. **Contracts are frozen.** If you believe one must change, make the
smallest *additive* change you can, and call it out explicitly in your final report. Never rename or remove a member.

| Contract | File |
|---|---|
| Domain entities | `Domain/Document.cs`, `Domain/Chunk.cs`, `Domain/Modality.cs`, `Domain/IndexStatus.cs`, `Domain/DocumentIds.cs`, `Domain/MediaTypes.cs` |
| SQL store | `Persistence/IDocumentStore.cs` (+ `DocumentDraft`, `ChunkDraft`, `ChunkRecord`, `VectorHit`), `Persistence/SqlStoreOptions.cs` |
| Embeddings | `Embeddings/IEmbeddingService.cs`, `Embeddings/EmbeddingMath.cs`, `Embeddings/EncodedFeatures.cs` |
| Vision | `Embeddings/Vision/{PreprocessedImage,IImagePreprocessor,IVisionEncoder}.cs` |
| Audio | `Embeddings/Audio/{PreprocessedAudio,IAudioPreprocessor,IAudioEncoder}.cs` |
| ONNX sessions | `Embeddings/Onnx/{EmbeddingModelOptions,OnnxSessionProvider}.cs` (implemented) |
| Lucene / RRF / search / ingest | `Components/{ILuceneRepository,IReranker,ISearchService,IContentIngester}.cs`, `Dtos/{SearchResult,IngestResult,IngestOptions}.cs` |
| Per-invocation config | `RagOptions.cs`, `Components/DependencyInjection.cs` (`AddRagServices(RagOptions)`) |
| Test support | `Tests/Support/MockEmbeddingService.cs` (deterministic 256-d), `Tests/Support/SqlServerFixture.cs` (Testcontainers SQL 2025, `[Collection("SqlServer")]`), `Tests/Support/TestModelPaths.cs` |

## Workstreams (run in parallel)

Each workstream owns the files listed; don't edit files owned by another workstream. Stubs are marked `// STUB (Phase 0)`.

### A: Text embedding + multimodal merge
Owns: `Embeddings/Text/**`, `tools/golden/**`, `Tests/Embeddings/Text/**`, `Tests/Golden/**`, `Tests/test-assets/golden/**`.
- `GemmaEmbeddingService`: prompts (§5), tokenisation, `input_ids`/`attention_mask` (right pad 0), empty `[0,512]` feature tensors for absent modalities, fetch only `sentence_embedding`, `EmbeddingMath.TruncateAndNormalize`.
- Media calls: the encoder gives `EncodedFeatures`. Build `input_ids` as `<bos>` + `<|image>` + `<|image|>`×Rows + `<image|>` + `<eos>` (audio: 256000 / 258881 / 258883), pass the features as `image_features` / `audio_features`. Keep the placeholder construction in one small testable class.
- Tokeniser: load `tokenizer.json` (Gemma BPE, 262,144 vocab). Try `Microsoft.ML.Tokenizers`; fall back to a HF `tokenizers` binding (e.g. `Tokenizers.DotNet`). Hugging Face is blocked in the dev sandbox, but npm and PyPI are reachable: look there for a Gemma-3-family `tokenizer.json` to develop against, and use Python `tokenizers` (PyPI) as the parity oracle.
- Batching for document chunks: sensible batch size, ordered output, `CancellationToken` honoured.
- Golden tooling (§9): `tools/golden/` Node script (transformers.js) + README to generate fixtures locally. Add C# golden tests that **skip** when `PBRAG_MODEL_PATH` or the fixtures are missing (`[SkippableFact]`). They cover text, image and audio end to end, plus the model-card Red Planet sanity check (§5).

### B: Vision preprocessing + encoder
Owns: `Embeddings/Vision/ImagePreprocessor.cs`, `Embeddings/Vision/VisionEncoder.cs`, other new files under `Embeddings/Vision/`, `Tests/Embeddings/Vision/**`.
- Decode with SkiaSharp (already referenced), convert to RGB, rescale ×1/255, no mean/std normalisation.
- Resize: port PIL's separable bicubic (`resample=3`) in C# for parity (don't use Skia's resampler). Target dims divisible by 48 within the soft-token budget (default 280 → choose H,W ≤ budget with (H/48)(W/48) ≤ 280, preserving aspect ratio as the HF processor does; read `processor_config.json` semantics in §6). 768×768 → 256 tokens.
- Patchify 16×16×3 → `[1, patches, 768]`, `pixel_position_ids [1, patches, 2]`. Make patch scan order, within-patch flatten order and (x,y) vs (y,x) single named constants so golden tests can flip them.
- `VisionEncoder.Encode`: run `vision_encoder.onnx`, return `EncodedFeatures` `[soft_tokens, 512]`.
- Unit tests on synthetic images: shapes, divisibility, token counts, resize vs known PIL outputs (generate reference values with Pillow from PyPI and commit them as small fixtures).

### C: Audio preprocessing + encoder
Owns: `Embeddings/Audio/AudioPreprocessor.cs`, `Embeddings/Audio/AudioEncoder.cs`, other new files under `Embeddings/Audio/`, `Tests/Embeddings/Audio/**`.
- Decode via `ffmpeg -i in -ac 1 -ar 16000 -f f32le -` (process, stdout). Clear error if ffmpeg is missing.
- Log-mel (§7): 128 bins, window 320, hop 160, FFT 512, 0–8000 Hz, mel floor 0.001, no pre-emphasis/dither/normalisation. Window function, log variant, mel scale and filterbank norm should be configurable constants (defaults: Hann periodic, natural log of max(mel, floor), HTK vs Slaney: pick the HF Gemma feature-extractor default; check PyPI `transformers` source).
- Windows: ~30 s per `AudioWindow` (one chunk each), with ms locators. Inside a window, split into clips of `audio_seq_length` 280 tokens (≈11.2 s) along the clips axis, right-padded with mask false.
- `AudioEncoder.Encode`: run `audio_encoder.onnx` (`input_features`, `input_features_mask` bool), return `EncodedFeatures`.
- Unit tests: synthetic sine (peak in the right mel bin), shapes, padding/mask, windowing of 5 s / 40 s / 75 s inputs. Parity vs a Python (numpy) reference you generate from the HF feature extractor and commit as small fixtures.

### D: SQL persistence
Owns: `Persistence/**` (except the frozen contract types), `Migrations/**`, `Tests/Persistence/**`.
- EF Core 10 `RagDbContext`: `dbo.Documents`, `dbo.Chunks_EG2_256` per §10.3. `GlobalId` unique on both tables, `Embedding` mapped `SqlVector<float>` → `vector(256)`, cascade delete, `IX_Chunks_Doc (DocumentId, Ordinal)`. Index on `Documents.SourceId`.
- Initial migration under `Pixelbadger.Toolkit.Rag/Migrations/` (`dotnet ef`, with a design-time factory). `MigrateAsync` applies it.
- `ReplaceDocumentAsync`: one transaction (delete chunks, upsert doc, insert chunks), batched parameterised inserts (`SqlBulkCopy` only if you've verified it works with `vector` columns).
- `SearchAsync`: approximate `SELECT TOP (n) WITH APPROXIMATE … VECTOR_SEARCH` (§10.5) when the index exists and the mode is `Auto`; exact `VECTOR_DISTANCE` otherwise. `SqlVector<float>` parameters, never string literals. Settle the §10.5 [verify] items (variable TOP, joins) against the real container and document what you find in code comments.
- `EnsureVectorIndexAsync`: §10.4 (≥100 rows, idempotent, check index version, set `PREVIEW_FEATURES` where needed).
- Integration tests against `SqlServerFixture`: replace semantics, ids, cascade, sourceId filter, exact vs approximate (≥100 rows) recall agreement, hydration.

### E1: Core pipeline (Lucene, ingestion, search)
Owns: `Components/{LuceneRepository,RrfReranker,SearchService,ContentIngester}.cs`, `Components/FileReaders/**`, chunkers, `Dtos/IngestOptions.cs`, `Tests/Pipeline/**` plus existing chunker/file reader tests.
- Lucene: port the previous BM25 code (see `git show HEAD~1:Pixelbadger.Toolkit.Rag/Components/LuceneRepository.cs`). Fields `chunk_id` (stored int), `document_id`, `source_id` (StringField), `content` (TextField, **not stored**). `ReplaceDocumentAsync` deletes by `document_id` term. Keep query escaping.
- RRF on chunk ids (k = 60), recording ranks.
- `SearchService`: validate as before (query length, maxResults 1–100, sourceIds limits); fetch `max(2n, 20)` from both in parallel; fuse; hydrate via `IDocumentStore.GetChunksAsync`; map to `SearchResult` in fused order.
- `ContentIngester`: route by `MediaTypes`. Text → reader → chunker → filter empty → `EmbedDocumentTextAsync(title = file name)` → `ReplaceDocumentAsync` → Lucene replace (text chunks; ChunkIds from the store) → `EnsureVectorIndexAsync` once per batch. Image → 1 chunk. Audio → one chunk per `AudioWindow` (ms locators). Text chunks get char-offset locators. Carry over all path-safety checks and limits from the previous implementation. If the Lucene write fails, set `IndexStatus.Failed`.
- Tests: unit tests with `MockEmbeddingService` + fakes, and integration tests with `SqlServerFixture` + real Lucene temp dir + mocks for the image/audio preprocessors. Cover re-ingest replacement, sourceIds filter, unique chunk ids, and hybrid ranking.

### E2: Host surface (CLI, MCP, DI, CI, docs)
Owns: `Program.cs`, `Commands/**`, `Components/McpRagServer.cs`, `Components/DependencyInjection.cs`, `.github/**`, `README.md`, `CLAUDE.md`, `Tests/Host/**`.
- System.CommandLine 2.0 GA. Commands: `ingest --index-path --connection-string --model-path --content-path [limits…]`, `query --index-path --connection-string --model-path --query [--max-results] [--source-ids]`, `serve --index-path --connection-string --model-path`. Env fallbacks `PBRAG_CONNECTION_STRING` and `PBRAG_MODEL_PATH`. Optional `--exact-vector-search`. Build `RagOptions` → `ServiceCollection().AddRagServices(options)` per invocation. `ingest` runs `MigrateAsync` first.
- MCP: ModelContextProtocol 2.x stdio server, one tool `Search(query, maxResults=5, sourceIds?)`. Output keeps the "untrusted content" framing and shows chunk id, document id, source, modality and locator. Logs go to stderr.
- CI: `actions/setup-dotnet` 10.0.x. Ubuntu runners have Docker, so Testcontainers works.
- Docs: rewrite README and CLAUDE.md for the new architecture (SQL prerequisites with a docker one-liner, model download instructions, multimodal, hybrid-only, ffmpeg requirement).
- Tests: command parsing / option binding / env fallback, MCP output formatting.

## Rules for all agents
- Work only in your worktree; commit on your worktree branch with clear messages. Don't push. The orchestrator merges.
- `dotnet build` and `dotnet test` must pass before you finish (Docker is available; SQL tests use `SqlServerFixture`).
- No new NuGet packages unless necessary. If you add one, say so in the report. Don't hand-edit `packages.lock.json`; let restore update it.
- Match the existing code style (file-scoped namespaces, XML doc on public members, async all the way, FluentAssertions + xUnit).
- The real model is not available in the sandbox. Mark anything untested against it as `[verify]` in code comments and list it in your report.
- Final report: what you built, files touched, contract changes (if any), new packages, open `[verify]` items, test counts.
