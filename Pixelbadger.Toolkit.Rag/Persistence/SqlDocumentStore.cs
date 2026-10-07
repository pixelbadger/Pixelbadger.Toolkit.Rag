using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Pixelbadger.Toolkit.Rag.Domain;

namespace Pixelbadger.Toolkit.Rag.Persistence;

/// <summary>
/// SQL Server 2025 / Azure SQL implementation of <see cref="IDocumentStore"/> (EF Core 10 for the
/// domain model, raw <see cref="SqlCommand"/> for vector search and index management).
///
/// Empirical findings on SQL Server 2025 RTM-CU9 (17.0.5005.3, Linux container, 2026-10) that shaped this class:
/// <list type="bullet">
/// <item>CREATE VECTOR INDEX (DiskANN, cosine) works only with <c>PREVIEW_FEATURES = ON</c>. Without it the statement
/// does not even parse (error 343), and neither does <c>VECTOR_SEARCH</c> (error 156). The setting is per database,
/// so it must be applied in a batch before the dependent statement. The session also needs QUOTED_IDENTIFIER ON
/// (SqlClient's default).</item>
/// <item>The index reports NO <c>Version</c> in <c>sys.vector_indexes.build_parameters</c> (only
/// <c>{"StartId","L","M","R"}</c>); this is the original (pre-v3) preview index. A table with such an index is
/// READ-ONLY: INSERT/UPDATE/DELETE fail with error 42231. Therefore <see cref="ReplaceDocumentAsync"/> drops a
/// non-v3 index before writing (search is exact until <see cref="EnsureVectorIndexAsync"/> recreates it).
/// With a v3 index (Azure SQL, newer builds) DML is supported and the index is left alone.</item>
/// <item><c>SELECT TOP (n) WITH APPROXIMATE</c> is a syntax error on this build (error 102). The syntax on this build is
/// <c>VECTOR_SEARCH(..., TOP_N = n)</c>. <c>TOP_N</c> accepts a variable/parameter. Both forms are implemented; the
/// store tries <c>WITH APPROXIMATE</c> only when a v3 index is reported and falls back automatically
/// (<c>WITH APPROXIMATE</c> is therefore [verify] against Azure SQL / a newer build).</item>
/// <item>Joins and <c>WHERE</c> filters in the same statement as legacy <c>VECTOR_SEARCH ... TOP_N</c> are POST-filters
/// over the TOP_N candidates (e.g. TOP_N = 100 filtered to two documents' sources returned 5 rows of the 100).
/// Approximate searches therefore over-fetch candidate chunks (also because only the nearest chunk of each document
/// is kept) and fall back to an exact scan when fewer than <c>maxResults</c> documents survive.</item>
/// <item><c>SqlVector&lt;float&gt;</c> binds as a parameter (<c>SqlDbType.Vector</c> or inferred), reads back via
/// <c>GetFieldValue&lt;SqlVector&lt;float&gt;&gt;</c>, and <c>SqlBulkCopy</c> works with vector columns (both
/// DataTable and DataReader sources). Writes still go through EF Core batched INSERTs because chunk ids must be
/// returned and ingestion is dominated by embedding time, not insert time.</item>
/// </list>
/// A fresh <see cref="RagDbContext"/> is created per operation; the instance only caches the vector index state.
/// </summary>
public sealed class SqlDocumentStore : IDocumentStore
{
    /// <summary>Minimum rows required by SQL Server before CREATE VECTOR INDEX succeeds (error 42266 otherwise).</summary>
    public const int MinRowsForVectorIndex = 100;

    public const string VectorIndexName = "VIX_Chunks_EG2Q8_256_Embedding";

    /// <summary>Index versions from this value upward support INSERT/UPDATE/DELETE and WITH APPROXIMATE.</summary>
    private const int WritableIndexVersion = 3;

    /// <summary>Error raised for DML against a table with a read-only (pre-v3) vector index.</summary>
    private const int VectorIndexReadOnlyError = 42231;

    private const int CandidateMultiplier = 20;
    private const int MinCandidates = 200;
    private const int MaxCandidates = 5000;

    private const string ChunksTableSql = "dbo." + RagDbContext.ChunksTable;

    private readonly SqlStoreOptions _options;
    private readonly DbContextOptions<RagDbContext> _dbOptions;

    // Cached vector index state (per store instance). Null = not yet detected.
    private VectorIndexState? _indexState;
    private bool _approximateSyntaxDisabled;
    private bool _topNSyntaxDisabled;

    public SqlDocumentStore(SqlStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentException("A SQL Server connection string is required.", nameof(options));

        _options = options;
        _dbOptions = new DbContextOptionsBuilder<RagDbContext>()
            .UseSqlServer(options.ConnectionString)
            .Options;
    }

    /// <summary>Which search path served the most recent <see cref="SearchAsync"/> (diagnostics/tests).</summary>
    public SearchPath LastSearchPath { get; private set; } = SearchPath.None;

    /// <summary>Message of the last failure to create the vector index, or null. Index creation is an optimisation, so failures do not throw.</summary>
    public string? LastVectorIndexError { get; private set; }

    /// <summary>How a search was executed.</summary>
    public enum SearchPath
    {
        None,
        Exact,
        ApproximateTopN,
        ApproximateWithApproximate
    }

    private enum VectorIndexKind
    {
        None,
        /// <summary>Pre-v3 index: table is read-only while it exists.</summary>
        Legacy,
        /// <summary>v3+ index: DML supported.</summary>
        Writable
    }

    private sealed record VectorIndexState(VectorIndexKind Kind);

    private RagDbContext CreateContext() => new(_dbOptions);

    private SqlConnection CreateConnection() => new(_options.ConnectionString);

    /// <inheritdoc />
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        // EF's Migrate creates the database when it does not exist.
        await using var db = CreateContext();
        await db.Database.MigrateAsync(cancellationToken);
        _indexState = null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChunkRecord>> ReplaceDocumentAsync(
        Guid documentId,
        DocumentDraft document,
        IReadOnlyList<ChunkDraft> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var chunk in chunks)
        {
            if (chunk.Embedding is null || chunk.Embedding.Length != RagDbContext.EmbeddingDimensions)
                throw new ArgumentException(
                    $"Chunk {chunk.Ordinal} embedding must have {RagDbContext.EmbeddingDimensions} dimensions.", nameof(chunks));
        }

        return await WriteAsync(ct => ReplaceCoreAsync(documentId, document, chunks, ct), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DocumentContent?> GetContentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();
        var row = await db.Documents.AsNoTracking()
            .Where(d => d.GlobalId == documentId && d.SourceContent != null)
            .Select(d => new { d.SourceContent, d.ContentType, d.SourcePath })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null
            ? null
            : new DocumentContent(
                row.SourceContent!,
                row.ContentType ?? MediaTypes.GetContentType(row.SourcePath) ?? "application/octet-stream",
                Path.GetFileName(row.SourcePath));
    }

    /// <inheritdoc />
    public async Task<bool> DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
        => await WriteAsync(ct => DeleteCoreAsync(documentId, ct), cancellationToken);

    /// <summary>
    /// Runs a write against the chunk table. A pre-v3 vector index makes the table read-only (inserts and deletes
    /// alike), so it is dropped up front (recreated by <see cref="EnsureVectorIndexAsync"/>) and, should one appear
    /// concurrently, dropped on the read-only error and the write retried once.
    /// </summary>
    private async Task<T> WriteAsync<T>(Func<CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        if (_indexState is not { Kind: not VectorIndexKind.Legacy })
        {
            var state = await DetectIndexStateAsync(cancellationToken);
            if (state.Kind == VectorIndexKind.Legacy)
                await DropVectorIndexAsync(cancellationToken);
        }

        try
        {
            return await write(cancellationToken);
        }
        catch (SqlException ex) when (HasError(ex, VectorIndexReadOnlyError) || ex.InnerException is SqlException { Number: VectorIndexReadOnlyError })
        {
            // Index appeared concurrently (or detection was stale): drop it and retry once.
            await DropVectorIndexAsync(cancellationToken);
            return await write(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.GetBaseException() is SqlException sql && HasError(sql, VectorIndexReadOnlyError))
        {
            await DropVectorIndexAsync(cancellationToken);
            return await write(cancellationToken);
        }
    }

    private async Task<bool> DeleteCoreAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Jobs first, then the document (which cascades to chunks): the ingest queue's claim locks a job row and then
        // updates its document, so taking the locks in the same order avoids a deadlock with a concurrent claim.
        await db.IngestJobs.Where(j => j.Document!.GlobalId == documentId).ExecuteDeleteAsync(cancellationToken);
        var deleted = await db.Documents.Where(d => d.GlobalId == documentId).ExecuteDeleteAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return deleted > 0;
    }

    private async Task<IReadOnlyList<ChunkRecord>> ReplaceCoreAsync(
        Guid documentId,
        DocumentDraft draft,
        IReadOnlyList<ChunkDraft> chunkDrafts,
        CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // The document must exist: it is created when the upload is accepted, so a missing row means it was deleted
        // while its job ran, and writing chunks now would resurrect it. Only the keys are read (not the stored source).
        var doc = await db.Documents.AsNoTracking()
            .Where(d => d.GlobalId == documentId)
            .Select(d => new { d.DocumentId, d.GlobalId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DocumentNotFoundException(documentId);

        await db.Chunks.Where(c => c.DocumentId == doc.DocumentId).ExecuteDeleteAsync(cancellationToken);

        // Same transaction as the chunk replacement below: the canonical source is always the indexed version.
        var updatedAt = DateTime.UtcNow;
        var update = db.Documents.Where(d => d.DocumentId == doc.DocumentId);
        if (draft.SourceContent is { } sourceContent)
        {
            await update.ExecuteUpdateAsync(s => s
                .SetProperty(d => d.SourcePath, draft.SourcePath)
                .SetProperty(d => d.Title, draft.Title)
                .SetProperty(d => d.Modality, draft.Modality)
                .SetProperty(d => d.ContentHash, draft.ContentHash)
                .SetProperty(d => d.SourceContent, sourceContent)
                .SetProperty(d => d.ContentType, draft.ContentType)
                .SetProperty(d => d.IndexStatus, IndexStatus.Indexed)
                .SetProperty(d => d.UpdatedAtUtc, updatedAt), cancellationToken);
        }
        else
        {
            await update.ExecuteUpdateAsync(s => s
                .SetProperty(d => d.SourcePath, draft.SourcePath)
                .SetProperty(d => d.Title, draft.Title)
                .SetProperty(d => d.Modality, draft.Modality)
                .SetProperty(d => d.ContentHash, draft.ContentHash)
                .SetProperty(d => d.IndexStatus, IndexStatus.Indexed)
                .SetProperty(d => d.UpdatedAtUtc, updatedAt), cancellationToken);
        }

        var entities = new List<Chunk>(chunkDrafts.Count);
        foreach (var c in chunkDrafts.OrderBy(c => c.Ordinal))
        {
            entities.Add(new Chunk
            {
                GlobalId = Guid.CreateVersion7(),
                DocumentId = doc.DocumentId,
                Ordinal = c.Ordinal,
                Modality = c.Modality,
                LocatorStart = c.LocatorStart,
                LocatorEnd = c.LocatorEnd,
                ChunkText = c.Text,
                EmbeddingModel = EmbeddingModelId,
                Embedding = new SqlVector<float>(c.Embedding)
            });
        }

        db.Chunks.AddRange(entities);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return entities
            .Select(c => new ChunkRecord(
                c.ChunkId, c.GlobalId, doc.DocumentId, doc.GlobalId, draft.SourcePath, draft.Title,
                c.Ordinal, c.Modality, c.LocatorStart, c.LocatorEnd, c.ChunkText))
            .ToList();
    }

    /// <summary>Value stored in Chunk.EmbeddingModel (the chunk table is model + dimension specific).</summary>
    private const string EmbeddingModelId = "embeddinggemma-2-q8@256";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChunkRecord>> GetChunksAsync(IReadOnlyCollection<int> chunkIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        if (chunkIds.Count == 0) return Array.Empty<ChunkRecord>();

        var ids = chunkIds.Distinct().ToList();
        await using var db = CreateContext();
        // Projection deliberately skips the Embedding column.
        return await db.Chunks
            .AsNoTracking()
            .Where(c => ids.Contains(c.ChunkId))
            .Select(c => new ChunkRecord(
                c.ChunkId, c.GlobalId, c.DocumentId, c.Document!.GlobalId, c.Document.SourcePath,
                c.Document.Title, c.Ordinal, c.Modality, c.LocatorStart, c.LocatorEnd, c.ChunkText))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        float[] queryEmbedding,
        int maxResults,
        IReadOnlyCollection<Guid>? documentIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        if (queryEmbedding.Length != RagDbContext.EmbeddingDimensions)
            throw new ArgumentException($"Query embedding must have {RagDbContext.EmbeddingDimensions} dimensions.", nameof(queryEmbedding));
        if (maxResults <= 0) return Array.Empty<VectorHit>();

        var filter = documentIds is { Count: > 0 } ? documentIds.Distinct().ToArray() : null;
        var vector = new SqlVector<float>(queryEmbedding);

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        if (_options.SearchMode == VectorSearchMode.Auto)
        {
            var state = _indexState ?? await DetectIndexStateAsync(conn, cancellationToken);
            if (state.Kind != VectorIndexKind.None)
            {
                var approximate = await TryApproximateSearchAsync(conn, state, vector, maxResults, filter, cancellationToken);
                if (approximate is not null) return approximate;
            }
        }

        LastSearchPath = SearchPath.Exact;
        return await ExactSearchAsync(conn, vector, maxResults, filter, cancellationToken);
    }

    private async Task<IReadOnlyList<VectorHit>?> TryApproximateSearchAsync(
        SqlConnection conn,
        VectorIndexState state,
        SqlVector<float> vector,
        int maxResults,
        Guid[]? filter,
        CancellationToken ct)
    {
        // One hit per document, so always over-fetch chunks: a long document can fill many of the nearest slots.
        var candidates = Math.Clamp(maxResults * CandidateMultiplier, MinCandidates, MaxCandidates);

        // Preferred (v3+): TOP (n) WITH APPROXIMATE, where filters are applied during the search.
        if (state.Kind == VectorIndexKind.Writable && !_approximateSyntaxDisabled)
        {
            try
            {
                var chunks = await RunApproximateAsync(conn, vector, filter, withApproximate: true, candidates, ct);
                var hits = BestPerDocument(chunks, maxResults);
                // Fewer chunks than asked for means none are left, so the per-document list is complete.
                if (hits.Count >= maxResults || chunks.Count < candidates)
                {
                    LastSearchPath = SearchPath.ApproximateWithApproximate;
                    return hits;
                }

                return null; // too few distinct documents among the candidates: exact scan below is authoritative
            }
            catch (SqlException) when (!ct.IsCancellationRequested)
            {
                _approximateSyntaxDisabled = true; // syntax not supported by this server build
            }
        }

        // Legacy: VECTOR_SEARCH(..., TOP_N = n). WHERE/JOIN filters are post-filters over the n candidates.
        if (!_topNSyntaxDisabled)
        {
            try
            {
                var chunks = await RunApproximateAsync(conn, vector, filter, withApproximate: false, candidates, ct);
                var hits = BestPerDocument(chunks, maxResults);
                // Without a filter, fewer than n candidates means the table is exhausted; with one, nothing is known.
                if (hits.Count >= maxResults || (filter is null && chunks.Count < candidates))
                {
                    LastSearchPath = SearchPath.ApproximateTopN;
                    return hits;
                }

                return null;
            }
            catch (SqlException) when (!ct.IsCancellationRequested)
            {
                _topNSyntaxDisabled = true; // e.g. PREVIEW_FEATURES off in this database; exact search still works
            }
        }

        return null;
    }

    /// <summary>The nearest chunk of each document, in ascending distance (the input is already in that order).</summary>
    private static List<VectorHit> BestPerDocument(IReadOnlyList<ChunkCandidate> chunks, int maxResults)
    {
        var seen = new HashSet<int>();
        var hits = new List<VectorHit>(maxResults);
        foreach (var chunk in chunks)
        {
            if (hits.Count == maxResults) break;
            if (seen.Add(chunk.DocumentId)) hits.Add(new VectorHit(chunk.ChunkId, chunk.Distance));
        }

        return hits;
    }

    private static async Task<IReadOnlyList<ChunkCandidate>> RunApproximateAsync(
        SqlConnection conn, SqlVector<float> vector, Guid[]? filter, bool withApproximate, int candidates, CancellationToken ct)
    {
        var sql = new StringBuilder();
        if (withApproximate)
        {
            sql.Append("SELECT TOP (@k) WITH APPROXIMATE c.ChunkId, c.DocumentId, s.distance FROM VECTOR_SEARCH(TABLE = ")
               .Append(ChunksTableSql)
               .Append(" AS c, COLUMN = Embedding, SIMILAR_TO = @q, METRIC = 'cosine') AS s");
        }
        else
        {
            sql.Append("SELECT TOP (@k) c.ChunkId, c.DocumentId, s.distance FROM VECTOR_SEARCH(TABLE = ")
               .Append(ChunksTableSql)
               .Append(" AS c, COLUMN = Embedding, SIMILAR_TO = @q, METRIC = 'cosine', TOP_N = @n) AS s");
        }

        using var cmd = new SqlCommand { Connection = conn };
        AppendFilter(sql, cmd, filter);
        sql.Append(" ORDER BY s.distance, c.ChunkId");
        cmd.CommandText = sql.ToString();
        AddVector(cmd, vector);
        cmd.Parameters.Add(new SqlParameter("@k", SqlDbType.Int) { Value = candidates });
        if (!withApproximate) cmd.Parameters.Add(new SqlParameter("@n", SqlDbType.Int) { Value = candidates });

        var chunks = new List<ChunkCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            chunks.Add(new ChunkCandidate(reader.GetInt32(0), reader.GetInt32(1), Convert.ToSingle(reader.GetValue(2))));
        return chunks;
    }

    private static async Task<IReadOnlyList<VectorHit>> ExactSearchAsync(
        SqlConnection conn, SqlVector<float> vector, int maxResults, Guid[]? filter, CancellationToken ct)
    {
        // Rank each document's chunks by distance and keep its nearest one.
        var sql = new StringBuilder("SELECT TOP (@k) r.ChunkId, r.distance FROM (SELECT x.ChunkId, x.distance, ")
            .Append("ROW_NUMBER() OVER (PARTITION BY x.DocumentId ORDER BY x.distance, x.ChunkId) AS rn FROM (")
            .Append("SELECT c.ChunkId, c.DocumentId, VECTOR_DISTANCE('cosine', c.Embedding, @q) AS distance FROM ")
            .Append(ChunksTableSql).Append(" AS c");
        using var cmd = new SqlCommand { Connection = conn };
        AppendFilter(sql, cmd, filter);
        sql.Append(") AS x) AS r WHERE r.rn = 1 ORDER BY r.distance, r.ChunkId");
        cmd.CommandText = sql.ToString();
        AddVector(cmd, vector);
        cmd.Parameters.Add(new SqlParameter("@k", SqlDbType.Int) { Value = maxResults });
        return await ReadHitsAsync(cmd, ct);
    }

    private sealed record ChunkCandidate(int ChunkId, int DocumentId, float Distance);

    private static void AppendFilter(StringBuilder sql, SqlCommand cmd, Guid[]? filter)
    {
        if (filter is null) return;
        sql.Append(" JOIN dbo.").Append(RagDbContext.DocumentsTable).Append(" AS d ON d.DocumentId = c.DocumentId WHERE d.GlobalId IN (");
        for (int i = 0; i < filter.Length; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append("@s").Append(i);
            cmd.Parameters.Add(new SqlParameter("@s" + i, SqlDbType.UniqueIdentifier) { Value = filter[i] });
        }

        sql.Append(')');
    }

    private static void AddVector(SqlCommand cmd, SqlVector<float> vector)
        => cmd.Parameters.Add(new SqlParameter("@q", SqlDbType.Vector) { Value = vector });

    private static async Task<IReadOnlyList<VectorHit>> ReadHitsAsync(SqlCommand cmd, CancellationToken ct)
    {
        var hits = new List<VectorHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            hits.Add(new VectorHit(reader.GetInt32(0), Convert.ToSingle(reader.GetValue(1))));
        return hits;
    }

    /// <inheritdoc />
    public async Task EnsureVectorIndexAsync(CancellationToken cancellationToken = default)
    {
        if (_options.SearchMode == VectorSearchMode.ExactOnly) return;

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        var state = await DetectIndexStateAsync(conn, cancellationToken);
        if (state.Kind != VectorIndexKind.None) return;

        // Cheap row count from partition metadata (accurate enough for a threshold check).
        long rows;
        await using (var count = new SqlCommand(
            "SELECT COALESCE(SUM(p.rows), 0) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(@t) AND p.index_id IN (0, 1)", conn))
        {
            count.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 256) { Value = ChunksTableSql });
            rows = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        if (rows < MinRowsForVectorIndex) return;

        try
        {
            await EnablePreviewFeaturesIfNeededAsync(conn, cancellationToken);

            // Separate batch: the statement does not parse while PREVIEW_FEATURES is off (SQL Server 2025).
            await using var create = new SqlCommand(
                $"CREATE VECTOR INDEX {VectorIndexName} ON {ChunksTableSql} (Embedding) " +
                "WITH (METRIC = 'cosine', TYPE = 'DiskANN', MAXDOP = 0)", conn)
            { CommandTimeout = 0 };
            await create.ExecuteNonQueryAsync(cancellationToken);
            LastVectorIndexError = null;
        }
        catch (SqlException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The index is an optimisation; exact search remains correct. (Also covers a concurrent creator.)
            LastVectorIndexError = ex.Message;
        }

        _indexState = null;
        await DetectIndexStateAsync(conn, cancellationToken);
        _approximateSyntaxDisabled = false;
        _topNSyntaxDisabled = false;
    }

    /// <summary>
    /// Sets PREVIEW_FEATURES = ON where the option exists and is off (SQL Server 2025). Azure SQL, where vector
    /// indexes are GA, has no such need; failure to set it is ignored.
    /// </summary>
    private static async Task EnablePreviewFeaturesIfNeededAsync(SqlConnection conn, CancellationToken ct)
    {
        try
        {
            await using var check = new SqlCommand(
                "SELECT CAST(value AS nvarchar(20)) FROM sys.database_scoped_configurations WHERE name = N'PREVIEW_FEATURES'", conn);
            var current = (await check.ExecuteScalarAsync(ct)) as string;
            if (current is null || current is "1" or "ON") return;

            await using var set = new SqlCommand("ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON", conn);
            await set.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException) when (!ct.IsCancellationRequested)
        {
            // Missing permission or option not available: attempt the index anyway.
        }
    }

    private async Task<VectorIndexState> DetectIndexStateAsync(CancellationToken ct)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        return await DetectIndexStateAsync(conn, ct);
    }

    private async Task<VectorIndexState> DetectIndexStateAsync(SqlConnection conn, CancellationToken ct)
    {
        VectorIndexState state;
        try
        {
            await using var cmd = new SqlCommand(
                "SELECT TOP (1) JSON_VALUE(v.build_parameters, '$.Version') FROM sys.vector_indexes v WHERE v.object_id = OBJECT_ID(@t)", conn);
            cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 256) { Value = ChunksTableSql });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                state = new VectorIndexState(VectorIndexKind.None);
            }
            else
            {
                var version = reader.IsDBNull(0) ? null : reader.GetString(0);
                state = new VectorIndexState(
                    int.TryParse(version, out var v) && v >= WritableIndexVersion ? VectorIndexKind.Writable : VectorIndexKind.Legacy);
            }
        }
        catch (SqlException) when (!ct.IsCancellationRequested)
        {
            // sys.vector_indexes unavailable (older engine): no vector index possible.
            state = new VectorIndexState(VectorIndexKind.None);
        }

        _indexState = state;
        return state;
    }

    private async Task DropVectorIndexAsync(CancellationToken ct)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await using var drop = new SqlCommand(
            $"IF EXISTS (SELECT 1 FROM sys.vector_indexes WHERE object_id = OBJECT_ID(N'{ChunksTableSql}')) " +
            $"DROP INDEX {VectorIndexName} ON {ChunksTableSql}", conn);
        try
        {
            await drop.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Number == 3701 && !ct.IsCancellationRequested)
        {
            // Index has another name or vanished concurrently; re-detection below decides.
        }

        _indexState = new VectorIndexState(VectorIndexKind.None);
    }

    private static bool HasError(SqlException ex, int number) => ex.Errors.Cast<SqlError>().Any(e => e.Number == number);
}
