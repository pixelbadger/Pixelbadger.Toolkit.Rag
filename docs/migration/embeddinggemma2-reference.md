# EmbeddingGemma 2 on C# / ONNX Runtime — implementation reference

Condensed reference for porting a RAG pipeline to `google/embeddinggemma-2` (released 2026-10-06) using the
`onnx-community/embeddinggemma-2-ONNX` export and `Microsoft.ML.OnnxRuntime`.

Legend: **[verified]** = checked against the graphs/configs or run locally. **[verify]** = inferred; pin with golden tests (section 9).

---

## 1. Model facts

| | |
|---|---|
| Licence | Apache 2.0 |
| Params | 740M total: text 270M (130M transformer + 140M embedding tables), vision +170M, audio +300M |
| Output | 768-d, single shared space across text / image / video / audio |
| Matryoshka dims | 768, 512, 256, 128 — truncate then **re-normalise** |
| Context | 8,192 tokens shared across all modalities in one input |
| Token cost | text 1/subword · image 280 (default budget) · video frame 140 · audio 25/s (40 ms/token) |
| Recommended dim | 256 (near-lossless for text/code; ~95% on image/audio retrieval). 128 hurts multimodal badly |
| Precision | never run activations in float16 (overflow → NaN or silently degraded vectors). fp32 or bf16 |

Breaking change for a port: the vector space is new. **Re-embed the whole corpus**; never mix with vectors from a previous model. Store a model-version column.

---

## 2. Artefacts

Repo: `onnx-community/embeddinggemma-2-ONNX` [verified]

| File (in `onnx/`) | Size | Use |
|---|---|---|
| `model.onnx` + `model.onnx_data` | 1.08 GB | **text model, fp32 — use this** |
| `model_quantized.onnx` + data | 314 MB | text int8 — slow for short inputs (see §8) |
| `model_q4.onnx` + data | 174 MB | text int4 — min cosine 0.988 vs fp32 |
| `vision_encoder.onnx` + data | 671 MB | fp32 (`_quantized` 195 MB, `_q4` 109 MB) |
| `audio_encoder.onnx` + data | 1.17 GB | fp32 (`_quantized` 340 MB, `_q4` 189 MB). Avoid q4 — audio is most quantisation-sensitive |
| `fp16` / `q4f16` variants | — | don't use on CPU |

Root: `tokenizer.json`, `tokenizer_config.json`, `config.json`, `processor_config.json`.

`.onnx_data` must sit beside its `.onnx` with the original filename (external-data reference is by name).

---

## 3. Graph I/O [verified]

**Text model** (`model.onnx`) — also performs the multimodal merge internally:

| Input | Type | Shape |
|---|---|---|
| `input_ids` | int64 | [batch, seq] |
| `attention_mask` | int64 | [batch, seq] |
| `image_features` | float32 | [num_image_tokens, 512] |
| `video_features` | float32 | [num_video_tokens, 512] |
| `audio_features` | float32 | [num_audio_tokens, 512] |

| Output | Shape | Notes |
|---|---|---|
| `sentence_embedding` | [batch, 768] | mean-pooled + projected, **already L2-normalised** [verified] |
| `last_hidden_state` | [batch, seq, 768] | don't request it |

**Vision encoder** (`vision_encoder.onnx`)

| | Type | Shape |
|---|---|---|
| in `pixel_values` | float32 | [images, patches, 768] (768 = 16×16×3) |
| in `pixel_position_ids` | int64 | [images, patches, 2] |
| out `image_features` | float32 | [soft_tokens, 512] (flattened across images) |

**Audio encoder** (`audio_encoder.onnx`)

| | Type | Shape |
|---|---|---|
| in `input_features` | float32 | [clips, frames, 128] |
| in `input_features_mask` | bool | [clips, frames] |
| out `audio_features` | float32 | [tokens, 512] (flattened across clips) |

**Gotcha:** all five text-model inputs are required. Text-only calls pass `image_features`, `video_features`, `audio_features` as **empty `[0, 512]` tensors** [verified].

### Data flow

```
image ─ prep ─▶ vision_encoder ─▶ image_features ─┐
audio ─ prep ─▶ audio_encoder  ─▶ audio_features ─┼─▶ model.onnx ─▶ sentence_embedding
text  ─ tokenise + expand placeholders ─▶ ids ────┘
```

Feature rows are consumed in the order placeholder tokens appear in `input_ids` (row-major across the batch) [verify]. Keep feature concatenation order identical to placeholder order.

---

## 4. Special tokens [verified ids]

| Token | Id | Role |
|---|---|---|
| `<pad>` | 0 | padding (right) |
| `<eos>` | 1 | appended by tokenizer template |
| `<bos>` | 2 | prepended by tokenizer template |
| `<\|image>` | 255999 | begin image |
| `<\|image\|>` | 258880 | image soft-token placeholder |
| `<image\|>` | 258882 | end image |
| `<\|audio>` | 256000 | begin audio |
| `<\|audio\|>` | 258881 | audio soft-token placeholder |
| `<audio\|>` | 258883 | end audio |
| `<\|video\|>` | 258884 | video soft-token placeholder |

Expansion per media item [verify exact framing]:

```
image: 255999, 258880 × N_image_tokens, 258882
audio: 256000, 258881 × N_audio_tokens, 258883
```

`N` must equal that item's row count in the encoder output.

---

## 5. Text path

1. **Prefix** (text only; never prefix media):

   | Use | Query | Document |
   |---|---|---|
   | Search / RAG | `task: search result \| query: {q}` | `title: {title} \| text: {chunk}` (`title: none` if absent) |
   | QA | `task: question answering \| query: {q}` | same doc format |
   | Fact check | `task: fact checking \| query: {claim}` | same |
   | Code | `task: code retrieval \| query: {q}` | `title: {filename} \| text: {code}` |
   | Classification / clustering / similarity | `task: classification \| query: {x}` (etc.), same prefix for all inputs | — |

2. **Tokenise** with `tokenizer.json` (Gemma BPE, 262,144 vocab, ~32 MB). Template wraps as `<bos> … <eos>` [verified]. Pad right with 0, mask 0.
3. **Run** `model.onnx` with empty feature tensors; fetch `sentence_embedding` only.
4. **Truncate** to 256, **re-normalise**. Store.

C# tokeniser [verify]: try `Microsoft.ML.Tokenizers` against `tokenizer.json`; if it can't load it, use a binding to HF `tokenizers` (Rust). Either way, add a parity test: token ids for ~1k varied strings (multilingual, code, emoji, whitespace runs) must equal Python `tokenizers` output exactly.

Sanity check from the model card [verified locally, fp32]: query "Which planet is known as the Red Planet?" vs Venus/Mars/Jupiter/Saturn docs → `0.675 0.853 0.747 0.778` (Mars first).

---

## 6. Image path

From `processor_config.json` [verified values]:

| Setting | Value |
|---|---|
| colour | convert to RGB |
| rescale | × 1/255 |
| normalise | **none** (mean 0, std 1) |
| resize | bicubic (`resample=3`), dims divisible by 48 |
| patch | 16 × 16 |
| pooling | 3 × 3 → soft tokens = (H/48)·(W/48) |
| budget | `max_soft_tokens` 280 default; allowed 70 / 140 / 280 / 560 / 1120 |

Recommended: pre-size every image to **768 × 768** → 48×48 = 2,304 patches → 256 soft tokens. Uniform shape means no in-batch padding and no resize in C#.

Build tensors:
- `pixel_values[i, p, :]` = the 16×16×3 patch flattened.
- `pixel_position_ids[i, p, :]` = patch grid coordinates.

[verify] patch scan order (row-major assumed), within-patch flatten order (assumed y, x, channel), position id order (x, y vs y, x), padding scheme for mixed sizes in one batch. All four are settled in minutes by diffing against golden tensors (§9).

If you must resize in C#: SkiaSharp bicubic won't match PIL bit-for-bit. Accept a cosine tolerance on the final embedding (≥ 0.999) rather than tensor equality, or port PIL's separable bicubic.

Video (out of scope here): frames → vision encoder at 140 tokens/frame, 1 fps, max 32 frames (uniform subsample), into `video_features` with token 258884.

---

## 7. Audio path

From `processor_config.json` [verified values]:

| Setting | Value |
|---|---|
| input | 16 kHz mono float32 |
| features | log-mel, 128 bins |
| window / hop | 320 samples (20 ms) / 160 (10 ms) |
| FFT | 512 |
| mel range | 0 – 8,000 Hz |
| mel floor | 0.001 |
| pre-emphasis / dither | 0 / 0 |
| per-bin mean/std | none |
| padding | right, value 0; mask true for real frames |
| output rate | 40 ms per token (≈ 4 frames per token) |
| `audio_seq_length` | 280 tokens (≈ 11.2 s) |

[verify] window function, log variant (natural log of max(mel, floor) assumed), mel scale (HTK vs Slaney), filterbank normalisation, and clip segmentation: `audio_seq_length` 280 vs the card's ~327 s per input suggests long audio is split into 11.2 s clips along the `clips` axis, with features flattened back in order.

Decode anything to the right format with ffmpeg in the container:
`ffmpeg -i in -ac 1 -ar 16000 -f f32le -` → `float[]`.

Optional: Silero VAD (ONNX) to skip silence before windowing.

---

## 8. Runtime and performance

Measured: text model, 2-vCPU Xeon (AVX-512 VNNI), ONNX Runtime 1.29 CPU EP [verified]:

| Text model | 18-token query | 512-token chunk | min cos vs fp32 |
|---|---|---|---|
| fp32 | 80 ms | 1.05 s | 1.0 |
| int8 (`_quantized`) | 430 ms | 1.42 s | 0.9999 |
| int4 (`_q4`) | 86 ms | 1.18 s | 0.988 |

- int8 is slow because its 8-bit `MatMulNBits` kernels are 94% of runtime at short lengths. Re-check on your CPU, but default to fp32.
- Batching 8 × 512 gave no per-chunk gain on 2 cores (compute-bound). Throughput scales with cores: ≈ 0.5 chunks/s/core at 512 tokens.
- Encoders not benchmarked; measure fp32 vs `_quantized` for vision and audio on target hardware.
- Same code runs on GPU via the CUDA execution provider if ever needed.

Session setup:
- One `InferenceSession` per graph; `Run` is thread-safe, so share sessions across requests.
- `GraphOptimizationLevel.ORT_ENABLE_ALL`; set `IntraOpNumThreads` to cores available to the process.
- Load encoders lazily; text-only hosts (query side) never load them.
- Memory: fp32 text ≈ 1.1 GB; add ≈ 0.7 GB vision, ≈ 1.2 GB audio.

```csharp
using Microsoft.ML.OnnxRuntime;

var opts = new SessionOptions {
    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
    IntraOpNumThreads = Environment.ProcessorCount
};
var text = new InferenceSession("models/onnx/model.onnx", opts);

float[] EmbedText(long[][] batchIds, int dim = 256)
{
    int b = batchIds.Length, L = batchIds.Max(x => x.Length);
    var ids = new long[b * L]; var mask = new long[b * L];
    for (int i = 0; i < b; i++)
        for (int j = 0; j < batchIds[i].Length; j++) { ids[i * L + j] = batchIds[i][j]; mask[i * L + j] = 1; }

    using var idsV  = OrtValue.CreateTensorValueFromMemory(ids,  new long[] { b, L });
    using var maskV = OrtValue.CreateTensorValueFromMemory(mask, new long[] { b, L });
    using var none  = OrtValue.CreateTensorValueFromMemory(Array.Empty<float>(), new long[] { 0, 512 });

    var inputs = new Dictionary<string, OrtValue> {
        ["input_ids"] = idsV, ["attention_mask"] = maskV,
        ["image_features"] = none, ["video_features"] = none, ["audio_features"] = none
    };
    using var outs = text.Run(new RunOptions(), inputs, new[] { "sentence_embedding" });
    var full = outs[0].GetTensorDataAsSpan<float>();      // [b, 768], unit-norm

    var result = new float[b * dim];
    for (int i = 0; i < b; i++) {
        var v = full.Slice(i * 768, dim);
        float n = 0; foreach (var x in v) n += x * x; n = MathF.Sqrt(n);
        for (int k = 0; k < dim; k++) result[i * dim + k] = v[k] / n;   // truncate + re-normalise
    }
    return result;
}
```

Multimodal call: run the encoder(s), keep the output `OrtValue`s, build `input_ids` with expanded placeholders (§4), pass the real feature tensors in place of `none`.

---

## 9. Golden tests (do this first)

transformers.js runs the reference processor on the same ONNX files, so it is the oracle for every [verify] above.

Node script (once, outputs committed as test fixtures):

```js
// npm i @huggingface/transformers
import { AutoProcessor, AutoModel, load_image, RawAudio } from "@huggingface/transformers";
import fs from "node:fs";

const id = "onnx-community/embeddinggemma-2-ONNX";
const processor = await AutoProcessor.from_pretrained(id);
const model = await AutoModel.from_pretrained(id, { device: "cpu", dtype: "fp32" });

const dump = (name, t) => fs.writeFileSync(`fixtures/${name}.json`,
  JSON.stringify({ dims: t.dims, type: t.type, data: Array.from(t.data, Number) }));

// image
const img = await load_image("fixtures/cat_768.png");
const pi = await processor(null, img);
for (const [k, v] of Object.entries(pi)) dump(`img.${k}`, v);
dump("img.embedding", (await model(pi)).sentence_embedding);

// audio: decode to mono 16 kHz Float32Array yourself in Node (e.g. ffmpeg f32le)
const pcm = new Float32Array(fs.readFileSync("fixtures/speech_16k.f32").buffer);
const pa = await processor(null, null, pcm);
for (const [k, v] of Object.entries(pa)) dump(`aud.${k}`, v);
dump("aud.embedding", (await model(pa)).sentence_embedding);

// text
const pt = await processor(["task: search result | query: northern lights"]);
for (const [k, v] of Object.entries(pt)) dump(`txt.${k}`, v);
dump("txt.embedding", (await model(pt)).sentence_embedding);
```

C# assertions:
- `input_ids` (with expanded placeholders): exact equality.
- `pixel_values`, `pixel_position_ids`, `input_features`, mask: atol 1e-4.
- Final `sentence_embedding`: cosine ≥ 0.9999 (same dtype).

Fixtures: one 768×768 image, one non-square image, ~5 s speech, ~40 s speech (forces multi-clip), a multilingual text set.

---

## 10. Vector storage: Azure SQL and local SQL Server

### 10.1 Product status (as of 2026-10-06)

| Product | `VECTOR` type | DiskANN index + `VECTOR_SEARCH` |
|---|---|---|
| Azure SQL Database | GA | **GA** (29 Sept 2026) |
| Azure SQL Managed Instance (always-up-to-date policy) | GA | GA |
| SQL database in Microsoft Fabric | GA | GA |
| SQL Server 2025 (local / container) | GA | **Preview**: requires `ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;` |
| Azure SQL MI (SQL Server 2025 policy) | GA | Preview |

- `float16` storage (`VECTOR(256, float16)`) is still marked preview in the docs. Use the default float32 for now; switching later is a column migration, not a re-embed (float32 → float16 needs an explicit CAST).
- Storage at 256-d float32 ≈ 1 KB per chunk. 10M chunks ≈ 10 GB of vectors before index overhead.
- Storing float16 is safe even though inference must not run in fp16: rounding stored outputs is not the same as activations overflowing.

### 10.2 Hard constraints on an indexed table [verified, docs]

- **Clustered primary key on an `int` column.** A string or GUID global id cannot be the PK; keep it as a separate unique column. `int` caps the table at ~2.1 billion chunks.
- **At least 100 non-NULL vectors** before `CREATE VECTOR INDEX` succeeds (error 42266). Deploy migrations onto an empty database cannot create the index; create it later (§10.4).
- No partitioned tables, temp tables, or replication to subscribers.
- `TRUNCATE TABLE` is blocked while the index exists (drop index → truncate → repopulate ≥ 100 rows → recreate).
- Full INSERT/UPDATE/DELETE/MERGE is supported with asynchronous index maintenance, in index version 3 only. Earlier preview indexes made the table read-only.
- Deployment: BACPAC supported (DacFx 170.5.96+); **DACPAC not supported** for vector indexes.
- Creating the index needs `ALTER` on the table.

### 10.3 Schema

One chunk table per model + dimension, so a model upgrade is a side-by-side table with its own index, swapped when backfill completes.

```sql
CREATE TABLE dbo.Documents (
    DocumentId    int IDENTITY PRIMARY KEY,
    GlobalId      nvarchar(200) NOT NULL UNIQUE,      -- system-global id for actual-document lookup
    Title         nvarchar(1000) NULL,
    IndexStatus   tinyint NOT NULL,                   -- queued / processing / indexed / failed
    UpdatedAtUtc  datetime2 NOT NULL
);

CREATE TABLE dbo.Chunks_EG2_256 (
    ChunkId       int IDENTITY PRIMARY KEY CLUSTERED, -- int clustered PK required by the vector index
    DocumentId    int NOT NULL REFERENCES dbo.Documents(DocumentId) ON DELETE CASCADE,
    Ordinal       int NOT NULL,
    Modality      tinyint NOT NULL,                   -- 0 text, 1 image, 2 audio
    LocatorStart  bigint NULL,                        -- char offset (text) or ms (audio)
    LocatorEnd    bigint NULL,
    ChunkText     nvarchar(max) NULL,                 -- optional: snippets + full-text/hybrid
    Embedding     vector(256) NOT NULL,               -- truncated + re-normalised
    INDEX IX_Chunks_Doc (DocumentId, Ordinal)
);
```

Re-ingesting a document: in one transaction, `DELETE` its chunks and `INSERT` the new set. DML is supported on indexed tables (v3).

### 10.4 Index creation

```sql
CREATE VECTOR INDEX VIX_Chunks_EG2_256_Embedding
    ON dbo.Chunks_EG2_256 (Embedding)
    WITH (METRIC = 'cosine', TYPE = 'DiskANN', MAXDOP = 0);
```

- Metric: vectors are unit-length, so `cosine` and `dot` rank identically. Use `cosine`; returned `distance` = 1 − similarity.
- Because of the 100-row minimum, run an idempotent "ensure vector index" step at app/worker startup and after each ingest batch: if no index exists and the row count is ≥ 100, create it.
- Below that, and whenever the optimizer judges it cheaper, exact search is used anyway.
- Check index version (needs v3 for writable tables and `WITH APPROXIMATE`):

```sql
SELECT i.name, JSON_VALUE(v.build_parameters, '$.Version') AS version
FROM sys.vector_indexes v
JOIN sys.indexes i ON v.object_id = i.object_id AND v.index_id = i.index_id;
```

Older indexes: drop and recreate.

### 10.5 Querying

**Approximate (indexed), current syntax.** `TOP_N` inside `VECTOR_SEARCH` was removed; use `SELECT TOP (n) WITH APPROXIMATE`:

```sql
DECLARE @q vector(256) = @queryVector;

SELECT TOP (@k) WITH APPROXIMATE
       d.GlobalId, c.Ordinal, c.Modality, c.LocatorStart, c.LocatorEnd, s.distance
FROM VECTOR_SEARCH(
        TABLE      = dbo.Chunks_EG2_256 AS c,
        COLUMN     = Embedding,
        SIMILAR_TO = @q,
        METRIC     = 'cosine'
     ) AS s
JOIN dbo.Documents d ON d.DocumentId = c.DocumentId
WHERE c.Modality IN (0, 1, 2)          -- filters are applied during the search (iterative filtering)
ORDER BY s.distance;
```

- Filters on the base table are applied during the search, so filtered queries no longer need over-fetching.
- **Per-document collapse still needs over-fetch.** Fetch `TOP (5 * n) WITH APPROXIMATE` into a CTE, then keep the best chunk(s) per document with `ROW_NUMBER() OVER (PARTITION BY DocumentId ORDER BY distance)` and take n.
- [verify] whether `WITH APPROXIMATE` accepts a variable `TOP (@k)` or needs a literal; and how joins in the same statement interact with the approximate plan. If in doubt, select chunk ids in an inner query and join outside it.

**Exact (no index needed):**

```sql
SELECT TOP (@k) c.ChunkId, VECTOR_DISTANCE('cosine', c.Embedding, @q) AS distance
FROM dbo.Chunks_EG2_256 c
ORDER BY distance;
```

Exact search is the fallback for local environments where the preview index lags, and the ground truth for recall tests of the approximate index.

**Hybrid (optional):** add a full-text index on `ChunkText` and merge the full-text and vector rankings with reciprocal rank fusion. It catches exact identifiers and reference numbers that embeddings blur. Full-text is available on Azure SQL Database; the SQL Server Linux container image may need the full-text package added [verify for the 2025 image].

### 10.6 .NET client side

- **Microsoft.Data.SqlClient** provides `SqlVector<float>` for `vector` parameters and columns. Pass the 256-float query as a typed parameter; never build vector literals as strings in hot paths.
- **EF Core 10** [verified, docs]: maps `SqlVector<float>` with `HasColumnType("vector(256)")`, and translates `EF.Functions.VectorDistance("cosine", col, vec)` (exact search).
- **EF Core 10 does not ship** `VectorSearch()` / `WithApproximate()` / `HasVectorIndex()`. Those appear in the EF provider docs alongside EF 11 notes; EF 11 is not GA until November 2026.

  Recommended split:
  - EF Core 10 for the domain entities and migrations (vector column mapped as `SqlVector<float>`).
  - Vector index created by the startup "ensure" step (§10.4), not by migrations (100-row minimum, DACPAC limits).
  - Approximate search via `FromSql` / raw `SqlCommand` (or Dapper) using the §10.5 statement.
  - Move to EF 11's `VectorSearch().Take(n).WithApproximate()` once it is GA. `WithApproximate()` must come after `Take()`.

  ```csharp
  modelBuilder.Entity<Chunk>()
      .Property(c => c.Embedding)
      .HasColumnType("vector(256)");

  public sealed class Chunk
  {
      public int ChunkId { get; set; }
      public int DocumentId { get; set; }
      public int Ordinal { get; set; }
      public byte Modality { get; set; }
      public long? LocatorStart { get; set; }
      public long? LocatorEnd { get; set; }
      public string? ChunkText { get; set; }
      public SqlVector<float> Embedding { get; set; }
  }
  ```

- Bulk ingest: batch inserts inside a transaction. [verify] `SqlBulkCopy` support for `vector` columns in your SqlClient version before relying on it; fall back to batched parameterised inserts.

### 10.7 Local development

- **Image:** SQL Server 2025 Developer edition in a Linux container (`mcr.microsoft.com/mssql/server:2025-latest` [verify tag]):

  ```bash
  docker run -d --name sql2025 -p 1433:1433 \
    -e ACCEPT_EULA=Y -e MSSQL_PID=Developer \
    -e 'MSSQL_SA_PASSWORD=<strong password>' \
    mcr.microsoft.com/mssql/server:2025-latest
  ```

- **Per database:** `ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;` before creating vector indexes.
- **Parity risk:** the local DiskANN preview can lag Azure's GA build. Check the index version (§10.4). If v3 isn't available locally, behind a config flag:
  - run exact `VECTOR_DISTANCE` locally and approximate search in Azure, or
  - develop against a small Azure SQL Database (serverless tier) instead.
- Apple Silicon: SQL Server Linux images are x64; run under emulation or use a remote/Azure dev database.
- Everything else (schema, `VECTOR`, `VECTOR_DISTANCE`, EF Core 10 mapping) behaves the same locally and in Azure.

### 10.8 Azure deployment notes

- Any vCore tier supports vector search; size for the vector index's build and query load and benchmark on real data. Microsoft has demonstrated search over one billion vectors on Hyperscale.
- Azure SQL MI must use the always-up-to-date update policy for GA indexes; the SQL Server 2025 policy is still preview.
- Index builds are heavy: run the initial `CREATE VECTOR INDEX` after the bulk backfill, off-peak, with `MAXDOP` set deliberately.
- Measure recall: sample real queries, compare approximate top-k against exact `VECTOR_DISTANCE` top-k, track recall@k per index rebuild.

---

## 11. Porting checklist

- [ ] Re-embed the full corpus; new chunk table `Chunks_EG2_256` with `vector(256)` and an int clustered PK.
- [ ] Vector index created by an idempotent startup step once ≥ 100 rows; verify index version 3.
- [ ] Approximate search via `TOP (n) WITH APPROXIMATE … VECTOR_SEARCH`; exact `VECTOR_DISTANCE` as fallback and recall baseline.
- [ ] Local SQL Server 2025 with `PREVIEW_FEATURES = ON`; config flag for exact-only mode.
- [ ] Query prefix on every query; document format `title: … | text: …` on every chunk.
- [ ] Truncate to 256 and re-normalise in one place, used by ingest and query.
- [ ] Empty `[0,512]` tensors for absent modalities.
- [ ] fp32 text model on CPU; never fp16 activations.
- [ ] Tokeniser parity test passes.
- [ ] Golden tests pass for text, image and audio before ingesting anything.
- [ ] Chunk text at ~512 tokens (≤ 2,048), audio in ~30 s windows; one 8,192-token budget per input.
- [ ] Ingest on a background worker; query host loads the text model only.

Sources: [model card](https://huggingface.co/google/embeddinggemma-2) · [ONNX export](https://huggingface.co/onnx-community/embeddinggemma-2-ONNX) · [processor_config.json](https://huggingface.co/onnx-community/embeddinggemma-2-ONNX/raw/main/processor_config.json) · [developer guide](https://developers.googleblog.com/embeddinggemma-2-the-developer-guide/) · [CREATE VECTOR INDEX](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-vector-index-transact-sql) · [DiskANN GA in Azure SQL](https://devblogs.microsoft.com/azure-sql/diskann-vector-index-search-are-now-generally-available-in-azure-sql/) · [DiskANN v3 improvements](https://devblogs.microsoft.com/azure-sql/diskann-vector-index-improvements/) · [Half-precision vectors](https://learn.microsoft.com/en-us/sql/t-sql/data-types/vector-data-type-half-precision-float) · [EF Core vector search](https://learn.microsoft.com/da-dk/ef/core/providers/sql-server/vector-search) · [EF Core 10 what's new](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)
