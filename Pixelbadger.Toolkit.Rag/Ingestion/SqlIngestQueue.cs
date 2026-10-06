using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>
/// <see cref="IIngestQueue"/> on SQL Server (EF Core for the simple operations, raw SQL where locking or atomicity
/// matters). The claim uses <c>UPDLOCK, READPAST</c> so concurrent claimers skip each other's rows instead of
/// blocking or double-claiming.
/// </summary>
/// <remarks>
/// Lock order everywhere is job row first, then document row (complete, fail, reset, re-ingest, and the document
/// delete in <see cref="SqlDocumentStore"/>; the claim touches the two in separate statements), so none of them can
/// deadlock with another.
/// </remarks>
public sealed class SqlIngestQueue : IIngestQueue
{
    private const string JobsTableSql = "dbo." + RagDbContext.IngestJobsTable;
    private const string DocumentsTableSql = "dbo." + RagDbContext.DocumentsTable;

    private readonly string _connectionString;
    private readonly IngestSettings _settings;
    private readonly DbContextOptions<RagDbContext> _dbOptions;

    public SqlIngestQueue(SqlStoreOptions sql, IngestSettings settings)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(sql.ConnectionString))
            throw new ArgumentException("A SQL Server connection string is required.", nameof(sql));

        _connectionString = sql.ConnectionString;
        _settings = settings;
        _dbOptions = new DbContextOptionsBuilder<RagDbContext>().UseSqlServer(sql.ConnectionString).Options;
    }

    private RagDbContext CreateContext() => new(_dbOptions);

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnqueuedDocument>> EnqueueNewDocumentsAsync(
        IReadOnlyList<IngestUpload> uploads, int maxChunkCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uploads);

        await using var db = CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var created = new List<EnqueuedDocument>(uploads.Count);
        // One file at a time, detached after each insert, so only one file's bytes are in memory at once.
        foreach (var upload in uploads)
        {
            var content = await ReadAllAsync(upload, cancellationToken);
            var now = DateTime.UtcNow;

            var document = new Document
            {
                GlobalId = Guid.CreateVersion7(),
                SourcePath = upload.LogicalPath,
                Title = Path.GetFileName(upload.LogicalPath),
                Modality = MediaTypes.GetModality(upload.LogicalPath)
                    ?? throw new NotSupportedException($"Unsupported file type: {Path.GetExtension(upload.LogicalPath)}"),
                ContentHash = string.Empty,
                IndexStatus = IndexStatus.Queued,
                UpdatedAtUtc = now
            };
            var job = new IngestJob
            {
                Id = Guid.CreateVersion7(),
                Document = document,
                Status = IngestJobStatus.Queued,
                MaxChunkCharacters = maxChunkCharacters,
                LogicalPath = upload.LogicalPath,
                Content = content,
                SizeBytes = content.Length,
                CreatedAtUtc = now
            };
            db.IngestJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            db.Entry(job).State = EntityState.Detached;
            db.Entry(document).State = EntityState.Detached;

            created.Add(new EnqueuedDocument(document.GlobalId, job.Id));
        }

        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    /// <inheritdoc />
    public async Task<ReingestResult> EnqueueReingestAsync(
        Guid documentId, IngestUpload upload, int maxChunkCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var content = await ReadAllAsync(upload, cancellationToken);
        var modality = MediaTypes.GetModality(upload.LogicalPath)
            ?? throw new NotSupportedException($"Unsupported file type: {Path.GetExtension(upload.LogicalPath)}");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var result = await ReingestCoreAsync(connection, transaction, documentId, upload.LogicalPath, modality, content, maxChunkCharacters, cancellationToken);
            if (result.Outcome is ReingestOutcome.Created or ReingestOutcome.ReplacedQueued)
                await transaction.CommitAsync(cancellationToken);
            else
                await transaction.RollbackAsync(cancellationToken);
            return result;
        }
        catch (SqlException ex) when (ex.Number == 547)
        {
            // The document was deleted between the lookup and the insert (foreign key violation).
            return new ReingestResult(ReingestOutcome.NotFound);
        }
    }

    private async Task<ReingestResult> ReingestCoreAsync(
        SqlConnection connection,
        DbTransaction transaction,
        Guid documentId,
        string logicalPath,
        Modality modality,
        byte[] content,
        int maxChunkCharacters,
        CancellationToken ct)
    {
        var tx = (SqlTransaction)transaction;

        // Serialises concurrent re-ingests of the SAME document (an application lock, so no row-lock order to worry
        // about): without it two of them could both see "no active job" and both insert one. Released on commit/rollback.
        await using (var applock = new SqlCommand("EXEC @result = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000", connection, tx))
        {
            applock.Parameters.AddWithValue("@resource", "pbrag:reingest:" + documentId.ToString("N"));
            var result = applock.Parameters.Add(new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output });
            await applock.ExecuteNonQueryAsync(ct);
            if ((int)result.Value < 0)
                throw new TimeoutException($"Could not lock document {documentId} for re-ingest (sp_getapplock returned {result.Value}).");
        }

        int documentKey;
        await using (var find = new SqlCommand($"SELECT DocumentId FROM {DocumentsTableSql} WHERE GlobalId = @g", connection, tx))
        {
            find.Parameters.AddWithValue("@g", documentId);
            if (await find.ExecuteScalarAsync(ct) is not int key)
                return new ReingestResult(ReingestOutcome.NotFound);
            documentKey = key;
        }

        // Lock the document's non-terminal job (or, when there is none, the key range where one would be inserted)
        // so a claim, a concurrent re-ingest or a delete cannot slip in between this look and the write below. A
        // claim in flight holds the row, so this waits for it and then sees Processing.
        Guid? activeJobId = null;
        IngestJobStatus? activeStatus = null;
        await using (var look = new SqlCommand(
            $"SELECT TOP (1) Id, Status FROM {JobsTableSql} WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE DocumentId = @d AND Status IN (@queued, @processing) ORDER BY Status DESC, CreatedAtUtc DESC, Id DESC", connection, tx))
        {
            look.Parameters.AddWithValue("@d", documentKey);
            look.Parameters.AddWithValue("@queued", (int)IngestJobStatus.Queued);
            look.Parameters.AddWithValue("@processing", (int)IngestJobStatus.Processing);
            await using var reader = await look.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                activeJobId = reader.GetGuid(0);
                activeStatus = (IngestJobStatus)reader.GetInt32(1);
            }
        }

        if (activeStatus == IngestJobStatus.Processing)
            return new ReingestResult(ReingestOutcome.Conflict);

        var now = DateTime.UtcNow;
        Guid jobId;
        ReingestOutcome outcome;
        if (activeJobId is { } queuedId)
        {
            // New bytes mean a fresh start: attempts and the last error belonged to the content being replaced.
            await using var replace = new SqlCommand(
                $"UPDATE {JobsTableSql} SET Content = @c, LogicalPath = @p, SizeBytes = @s, MaxChunkCharacters = @m, Attempts = 0, Error = NULL " +
                "WHERE Id = @id AND Status = @queued", connection, tx);
            AddContentParameters(replace, content, logicalPath, maxChunkCharacters);
            replace.Parameters.AddWithValue("@id", queuedId);
            replace.Parameters.AddWithValue("@queued", (int)IngestJobStatus.Queued);
            if (await replace.ExecuteNonQueryAsync(ct) == 0)
                return new ReingestResult(ReingestOutcome.Conflict); // claimed in the meantime

            jobId = queuedId;
            outcome = ReingestOutcome.ReplacedQueued;
        }
        else
        {
            jobId = Guid.CreateVersion7();
            await using var insert = new SqlCommand(
                $"INSERT INTO {JobsTableSql} (Id, DocumentId, Status, Attempts, MaxChunkCharacters, LogicalPath, Content, SizeBytes, CreatedAtUtc) " +
                "VALUES (@id, @d, @queued, 0, @m, @p, @c, @s, @now)", connection, tx);
            AddContentParameters(insert, content, logicalPath, maxChunkCharacters);
            insert.Parameters.AddWithValue("@id", jobId);
            insert.Parameters.AddWithValue("@d", documentKey);
            insert.Parameters.AddWithValue("@queued", (int)IngestJobStatus.Queued);
            insert.Parameters.AddWithValue("@now", now).SqlDbType = SqlDbType.DateTime2;
            await insert.ExecuteNonQueryAsync(ct);
            outcome = ReingestOutcome.Created;
        }

        await using (var touch = new SqlCommand(
            $"UPDATE {DocumentsTableSql} SET SourcePath = @p, Title = @t, Modality = @mod, IndexStatus = @status, UpdatedAtUtc = @now WHERE DocumentId = @d",
            connection, tx))
        {
            touch.Parameters.AddWithValue("@p", logicalPath);
            touch.Parameters.AddWithValue("@t", Path.GetFileName(logicalPath));
            touch.Parameters.Add(new SqlParameter("@mod", SqlDbType.TinyInt) { Value = (byte)modality });
            touch.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)IndexStatus.Queued });
            touch.Parameters.AddWithValue("@now", now).SqlDbType = SqlDbType.DateTime2;
            touch.Parameters.AddWithValue("@d", documentKey);
            if (await touch.ExecuteNonQueryAsync(ct) == 0)
                return new ReingestResult(ReingestOutcome.NotFound);
        }

        return new ReingestResult(outcome, jobId);
    }

    private static void AddContentParameters(SqlCommand command, byte[] content, string logicalPath, int maxChunkCharacters)
    {
        command.Parameters.Add(new SqlParameter("@c", SqlDbType.VarBinary, -1) { Value = content });
        command.Parameters.Add(new SqlParameter("@p", SqlDbType.NVarChar, 1024) { Value = logicalPath });
        command.Parameters.Add(new SqlParameter("@s", SqlDbType.BigInt) { Value = (long)content.Length });
        command.Parameters.Add(new SqlParameter("@m", SqlDbType.Int) { Value = maxChunkCharacters });
    }

    private static async Task<byte[]> ReadAllAsync(IngestUpload upload, CancellationToken cancellationToken)
    {
        await using var source = upload.OpenRead();
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    /// <inheritdoc />
    public async Task<IngestJobClaim?> TryClaimNextAsync(string owner, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        // 1. Jobs abandoned with no attempts left become Failed (dropping their stored bytes), and so do their documents.
        // 2. Claim the oldest claimable job (CTE so ORDER BY applies to the single updated row), then mark its document
        //    Processing. No transaction around the batch: each statement is atomic, the job row is claimed (and its
        //    lock released) before the document row is touched, and READPAST keeps claimers from queueing behind each other.
        const string sql = $"""
            SET NOCOUNT ON;

            DECLARE @dead TABLE (DocumentId int NOT NULL);
            DECLARE @claimed TABLE (
                Id uniqueidentifier NOT NULL, DocumentId int NOT NULL, Attempts int NOT NULL,
                MaxChunkCharacters int NOT NULL, LogicalPath nvarchar(1024) NOT NULL, SizeBytes bigint NOT NULL);

            UPDATE {JobsTableSql} WITH (READPAST)
            SET Status = @failed, CompletedAtUtc = @now, LeaseOwner = NULL, LeaseExpiresAtUtc = NULL, Content = NULL,
                Error = COALESCE(Error, N'The job was abandoned after ' + CAST(Attempts AS nvarchar(11)) + N' attempt(s).')
            OUTPUT inserted.DocumentId INTO @dead
            WHERE Status = @processing AND LeaseExpiresAtUtc < @now AND Attempts >= @maxAttempts;

            UPDATE d SET IndexStatus = @docFailed, UpdatedAtUtc = @now
            FROM {DocumentsTableSql} d WHERE d.DocumentId IN (SELECT DocumentId FROM @dead);

            ;WITH claimable AS (
                SELECT TOP (1) *
                FROM {JobsTableSql} WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Status = @queued
                   OR (Status = @processing AND LeaseExpiresAtUtc < @now AND Attempts < @maxAttempts)
                ORDER BY CreatedAtUtc, Id
            )
            UPDATE claimable
            SET Status = @processing, Attempts = Attempts + 1, LeaseOwner = @owner, LeaseExpiresAtUtc = @leaseUntil,
                StartedAtUtc = COALESCE(StartedAtUtc, @now), Error = NULL
            OUTPUT inserted.Id, inserted.DocumentId, inserted.Attempts, inserted.MaxChunkCharacters, inserted.LogicalPath, inserted.SizeBytes
            INTO @claimed;

            UPDATE d SET IndexStatus = @docProcessing, UpdatedAtUtc = @now
            FROM {DocumentsTableSql} d WHERE d.DocumentId IN (SELECT DocumentId FROM @claimed);

            SELECT c.Id, d.GlobalId, c.Attempts, c.MaxChunkCharacters, c.LogicalPath, c.SizeBytes
            FROM @claimed c JOIN {DocumentsTableSql} d ON d.DocumentId = c.DocumentId;
            """;

        var now = DateTime.UtcNow;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@now", now).SqlDbType = SqlDbType.DateTime2;
        command.Parameters.AddWithValue("@leaseUntil", now + lease).SqlDbType = SqlDbType.DateTime2;
        command.Parameters.AddWithValue("@owner", owner);
        command.Parameters.AddWithValue("@maxAttempts", _settings.MaxAttempts);
        command.Parameters.AddWithValue("@queued", (int)IngestJobStatus.Queued);
        command.Parameters.AddWithValue("@processing", (int)IngestJobStatus.Processing);
        command.Parameters.AddWithValue("@failed", (int)IngestJobStatus.Failed);
        command.Parameters.Add(new SqlParameter("@docFailed", SqlDbType.TinyInt) { Value = (byte)IndexStatus.Failed });
        command.Parameters.Add(new SqlParameter("@docProcessing", SqlDbType.TinyInt) { Value = (byte)IndexStatus.Processing });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new IngestJobClaim(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4), reader.GetInt64(5));
    }

    /// <inheritdoc />
    public async Task ReadContentAsync(Guid jobId, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand($"SELECT Content FROM {JobsTableSql} WHERE Id = @id", connection);
        command.Parameters.AddWithValue("@id", jobId);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || await reader.IsDBNullAsync(0, cancellationToken))
            throw new InvalidOperationException($"Stored content for ingest job {jobId} is not available.");

        await using var content = reader.GetStream(0);
        await content.CopyToAsync(destination, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ExtendLeaseAsync(Guid jobId, string owner, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var until = DateTime.UtcNow + lease;
        await using var db = CreateContext();
        var updated = await db.IngestJobs
            .Where(j => j.Id == jobId && j.LeaseOwner == owner && j.Status == IngestJobStatus.Processing)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)until), cancellationToken);
        return updated > 0;
    }

    /// <inheritdoc />
    public async Task CompleteAsync(Guid jobId, IngestJobOutcome outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!IngestJob.IsTerminal(outcome.Status))
            throw new ArgumentException("A completed job must end Succeeded, Skipped or Failed.", nameof(outcome));

        var now = DateTime.UtcNow;
        var documentStatus = outcome.Status == IngestJobStatus.Failed ? IndexStatus.Failed : IndexStatus.Indexed;

        await using var db = CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // A job deleted with its document (cascade) updates nothing here, which is the intended quiet outcome.
        var updated = await db.IngestJobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, outcome.Status)
                .SetProperty(j => j.ChunkCount, outcome.ChunkCount)
                .SetProperty(j => j.Error, outcome.Error)
                .SetProperty(j => j.Content, (byte[]?)null)
                .SetProperty(j => j.CompletedAtUtc, (DateTime?)now)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null),
                cancellationToken);
        if (updated > 0)
        {
            await db.Documents.Where(d => d.Jobs.Any(j => j.Id == jobId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.IndexStatus, documentStatus)
                    .SetProperty(d => d.UpdatedAtUtc, now),
                    cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IngestJobStatus> FailAsync(Guid jobId, string error, CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();

        var attempts = await db.IngestJobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => (int?)j.Attempts)
            .SingleOrDefaultAsync(cancellationToken);
        if (attempts is null)
            return IngestJobStatus.Failed; // deleted together with its document: nothing to record

        // Only the worker holding the claim calls this, so reading then writing is not racy.
        var exhausted = attempts >= _settings.MaxAttempts;
        var status = exhausted ? IngestJobStatus.Failed : IngestJobStatus.Queued;
        var documentStatus = exhausted ? IndexStatus.Failed : IndexStatus.Queued;
        var now = DateTime.UtcNow;
        DateTime? completedAt = exhausted ? now : null;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = db.IngestJobs.Where(j => j.Id == jobId);
        if (exhausted)
        {
            // Terminal: drop the bytes too.
            await job.ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.CompletedAtUtc, completedAt)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.Content, (byte[]?)null)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null),
                cancellationToken);
        }
        else
        {
            await job.ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.CompletedAtUtc, completedAt)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null),
                cancellationToken);
        }

        await db.Documents.Where(d => d.Jobs.Any(j => j.Id == jobId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.IndexStatus, documentStatus)
                .SetProperty(d => d.UpdatedAtUtc, now),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return status;
    }

    /// <inheritdoc />
    public async Task<int> ResetInFlightJobsAsync(CancellationToken cancellationToken = default)
    {
        // Single-process assumption: this instance is the only worker, so anything still Processing belonged to a
        // previous process that died. If the app is ever scaled out (shared Lucene index / multiple workers), remove
        // this and rely on lease expiry in TryClaimNextAsync instead.
        const string sql = $"""
            SET NOCOUNT ON;
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            DECLARE @reset TABLE (DocumentId int NOT NULL);

            UPDATE {JobsTableSql}
            SET Status = @queued, LeaseOwner = NULL, LeaseExpiresAtUtc = NULL
            OUTPUT inserted.DocumentId INTO @reset
            WHERE Status = @processing;

            UPDATE d SET IndexStatus = @docQueued, UpdatedAtUtc = @now
            FROM {DocumentsTableSql} d WHERE d.DocumentId IN (SELECT DocumentId FROM @reset);

            SELECT COUNT(*) FROM @reset;

            COMMIT TRANSACTION;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@queued", (int)IngestJobStatus.Queued);
        command.Parameters.AddWithValue("@processing", (int)IngestJobStatus.Processing);
        command.Parameters.Add(new SqlParameter("@docQueued", SqlDbType.TinyInt) { Value = (byte)IndexStatus.Queued });
        command.Parameters.AddWithValue("@now", DateTime.UtcNow).SqlDbType = SqlDbType.DateTime2;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <inheritdoc />
    public async Task<DocumentDto?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();

        // Projection leaves Content (and the embeddings) out of the SELECT.
        var row = await db.Documents.AsNoTracking()
            .Where(d => d.GlobalId == documentId)
            .Select(d => new
            {
                d.GlobalId,
                d.SourcePath,
                d.Title,
                d.Modality,
                d.IndexStatus,
                d.UpdatedAtUtc,
                ChunkCount = d.Chunks.Count(),
                Job = d.Jobs
                    .OrderByDescending(j => j.CreatedAtUtc).ThenByDescending(j => j.Id)
                    .Select(j => new { j.Id, j.Status, j.Attempts, j.CreatedAtUtc, j.StartedAtUtc, j.CompletedAtUtc, j.Error })
                    .FirstOrDefault()
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        var latest = row.Job is { } j
            ? new IngestJobDto(j.Id, j.Status, j.Attempts, j.CreatedAtUtc, j.StartedAtUtc, j.CompletedAtUtc, j.Error)
            : null;
        return new DocumentDto(row.GlobalId, row.SourcePath, row.Title, row.Modality, row.IndexStatus, row.ChunkCount, row.UpdatedAtUtc, latest);
    }
}
