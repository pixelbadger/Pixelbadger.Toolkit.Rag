using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Messaging;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>
/// <see cref="IIngestQueue"/> on SQL Server (EF Core for the simple operations, raw SQL where locking or atomicity
/// matters). Every status change runs in a <see cref="JobEventTransaction"/>, so the <see cref="JobStatusChanged"/>
/// event that drives the work is committed (or rolled back) together with the write itself.
/// </summary>
/// <remarks>
/// Lock order everywhere is job row first, then document row (begin processing, complete, re-ingest, and the
/// document delete in <see cref="SqlDocumentStore"/>). Within the jobs table, though, the re-ingest lookup (via the
/// DocumentId index) and <see cref="BeginProcessingAsync"/> (via the primary key) can still take index locks in
/// opposite orders and deadlock; the re-ingest transaction is therefore retried when it is the victim.
/// </remarks>
public sealed class SqlIngestQueue : IIngestQueue
{
    private const string JobsTableSql = "dbo." + RagDbContext.IngestJobsTable;
    private const string DocumentsTableSql = "dbo." + RagDbContext.DocumentsTable;

    private readonly string _connectionString;
    private readonly IServiceScopeFactory _scopes;
    private readonly DbContextOptions<RagDbContext> _dbOptions;

    public SqlIngestQueue(SqlStoreOptions sql, IngestSettings settings, IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(scopes);
        if (string.IsNullOrWhiteSpace(sql.ConnectionString))
            throw new ArgumentException("A SQL Server connection string is required.", nameof(sql));

        _connectionString = sql.ConnectionString;
        _scopes = scopes;
        _dbOptions = new DbContextOptionsBuilder<RagDbContext>().UseSqlServer(sql.ConnectionString).Options;
    }

    // Reads (listing, content) use their own connections; every write goes through a JobEventTransaction.
    private RagDbContext CreateContext() => new(_dbOptions);

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnqueuedDocument>> EnqueueNewDocumentsAsync(
        IReadOnlyList<IngestUpload> uploads, int maxChunkCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uploads);

        await using var tx = await JobEventTransaction.BeginAsync(_scopes, cancellationToken);
        await using var db = tx.CreateDbContext();

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

            await tx.PublishAsync(job.Id, IngestJobStatus.Queued, cancellationToken);
            created.Add(new EnqueuedDocument(document.GlobalId, job.Id));
        }

        await tx.CommitAsync();
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

        for (var attempt = 1; ; attempt++)
        {
            // A new transaction (and outbox scope) per attempt: a deadlock victim's transaction is gone.
            await using var tx = await JobEventTransaction.BeginAsync(_scopes, cancellationToken);

            try
            {
                var result = await ReingestCoreAsync(tx, documentId, upload.LogicalPath, modality, content, maxChunkCharacters, cancellationToken);
                if (result.Outcome is ReingestOutcome.Created or ReingestOutcome.ReplacedQueued)
                    await tx.CommitAsync();
                else
                    await tx.RollbackAsync();
                return result;
            }
            catch (SqlException ex) when (ex.Number == 547)
            {
                // The document was deleted between the lookup and the insert (foreign key violation).
                return new ReingestResult(ReingestOutcome.NotFound);
            }
            catch (SqlException ex) when (ex.Number == DeadlockVictimError && attempt < MaxDeadlockAttempts)
            {
                // Chosen as deadlock victim against BeginProcessingAsync (see remarks): SQL Server has rolled the whole
                // transaction back, so rerunning it is safe and then sees the job's real state (Conflict or ReplacedQueued).
                await Task.Delay(Random.Shared.Next(20, 100 * attempt), cancellationToken);
            }
        }
    }

    private const int DeadlockVictimError = 1205;
    private const int MaxDeadlockAttempts = 5;

    private static async Task<ReingestResult> ReingestCoreAsync(
        JobEventTransaction tx,
        Guid documentId,
        string logicalPath,
        Modality modality,
        byte[] content,
        int maxChunkCharacters,
        CancellationToken ct)
    {
        var connection = tx.Connection;
        var transaction = tx.Transaction;

        // Serialises concurrent re-ingests of the SAME document (an application lock, so no row-lock order to worry
        // about): without it two of them could both see "no active job" and both insert one. Released on commit/rollback.
        await using (var applock = new SqlCommand("EXEC @result = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000", connection, transaction))
        {
            applock.Parameters.AddWithValue("@resource", "pbrag:reingest:" + documentId.ToString("N"));
            var result = applock.Parameters.Add(new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output });
            await applock.ExecuteNonQueryAsync(ct);
            if ((int)result.Value < 0)
                throw new TimeoutException($"Could not lock document {documentId} for re-ingest (sp_getapplock returned {result.Value}).");
        }

        int documentKey;
        await using (var find = new SqlCommand($"SELECT DocumentId FROM {DocumentsTableSql} WHERE GlobalId = @g", connection, transaction))
        {
            find.Parameters.AddWithValue("@g", documentId);
            if (await find.ExecuteScalarAsync(ct) is not int key)
                return new ReingestResult(ReingestOutcome.NotFound);
            documentKey = key;
        }

        // Lock the document's non-terminal job (or, when there is none, the key range where one would be inserted)
        // so a begin-processing, a concurrent re-ingest or a delete cannot slip in between this look and the write
        // below. A begin-processing in flight holds the row, so this waits for it and then sees Processing.
        Guid? activeJobId = null;
        IngestJobStatus? activeStatus = null;
        await using (var look = new SqlCommand(
            $"SELECT TOP (1) Id, Status FROM {JobsTableSql} WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE DocumentId = @d AND Status IN (@queued, @processing) ORDER BY Status DESC, CreatedAtUtc DESC, Id DESC", connection, transaction))
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
                "WHERE Id = @id AND Status = @queued", connection, transaction);
            AddContentParameters(replace, content, logicalPath, maxChunkCharacters);
            replace.Parameters.AddWithValue("@id", queuedId);
            replace.Parameters.AddWithValue("@queued", (int)IngestJobStatus.Queued);
            if (await replace.ExecuteNonQueryAsync(ct) == 0)
                return new ReingestResult(ReingestOutcome.Conflict); // picked up in the meantime

            jobId = queuedId;
            outcome = ReingestOutcome.ReplacedQueued;
        }
        else
        {
            jobId = Guid.CreateVersion7();
            await using var insert = new SqlCommand(
                $"INSERT INTO {JobsTableSql} (Id, DocumentId, Status, Attempts, MaxChunkCharacters, LogicalPath, Content, SizeBytes, CreatedAtUtc) " +
                "VALUES (@id, @d, @queued, 0, @m, @p, @c, @s, @now)", connection, transaction);
            AddContentParameters(insert, content, logicalPath, maxChunkCharacters);
            insert.Parameters.AddWithValue("@id", jobId);
            insert.Parameters.AddWithValue("@d", documentKey);
            insert.Parameters.AddWithValue("@queued", (int)IngestJobStatus.Queued);
            insert.Parameters.AddWithValue("@now", now).SqlDbType = SqlDbType.DateTime2;
            await insert.ExecuteNonQueryAsync(ct);
            outcome = ReingestOutcome.Created;
        }

        await using (var touch = new SqlCommand(
            // Path, title, modality and the canonical source stay those of the indexed version until the job succeeds.
            $"UPDATE {DocumentsTableSql} SET IndexStatus = @status, UpdatedAtUtc = @now WHERE DocumentId = @d",
            connection, transaction))
        {
            touch.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)IndexStatus.Queued });
            touch.Parameters.AddWithValue("@now", now).SqlDbType = SqlDbType.DateTime2;
            touch.Parameters.AddWithValue("@d", documentKey);
            if (await touch.ExecuteNonQueryAsync(ct) == 0)
                return new ReingestResult(ReingestOutcome.NotFound);
        }

        // A replaced queued job keeps its status (its event is already out), so only a new job announces itself.
        if (outcome == ReingestOutcome.Created)
            await tx.PublishAsync(jobId, IngestJobStatus.Queued, ct);

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
    public async Task<IngestJobClaim?> BeginProcessingAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var tx = await JobEventTransaction.BeginAsync(_scopes, cancellationToken);
        var now = DateTime.UtcNow;

        // Job row first, then its document (lock order). A Processing job is taken again on purpose: that is the
        // redelivery after a crash, and it counts as another attempt.
        int documentKey, attempts, maxChunkCharacters;
        string logicalPath;
        long sizeBytes;
        await using (var job = new SqlCommand(
            $"""
            UPDATE {JobsTableSql}
            SET Status = @processing, Attempts = Attempts + 1, StartedAtUtc = COALESCE(StartedAtUtc, @now), Error = NULL
            OUTPUT inserted.DocumentId, inserted.Attempts, inserted.MaxChunkCharacters, inserted.LogicalPath, inserted.SizeBytes
            WHERE Id = @id AND Status IN (@queued, @processing)
            """, tx.Connection, tx.Transaction))
        {
            job.Parameters.AddWithValue("@id", jobId);
            job.Parameters.AddWithValue("@queued", (int)IngestJobStatus.Queued);
            job.Parameters.AddWithValue("@processing", (int)IngestJobStatus.Processing);
            job.Parameters.AddWithValue("@now", now).SqlDbType = SqlDbType.DateTime2;
            await using var reader = await job.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null; // missing or terminal: nothing changed, nothing published (disposal rolls back)

            documentKey = reader.GetInt32(0);
            attempts = reader.GetInt32(1);
            maxChunkCharacters = reader.GetInt32(2);
            logicalPath = reader.GetString(3);
            sizeBytes = reader.GetInt64(4);
        }

        Guid documentId;
        await using (var document = new SqlCommand(
            $"UPDATE {DocumentsTableSql} SET IndexStatus = @status, UpdatedAtUtc = @now OUTPUT inserted.GlobalId WHERE DocumentId = @d",
            tx.Connection, tx.Transaction))
        {
            document.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)IndexStatus.Processing });
            document.Parameters.AddWithValue("@now", now).SqlDbType = SqlDbType.DateTime2;
            document.Parameters.AddWithValue("@d", documentKey);
            // The job row we just locked keeps the document from being deleted underneath us (cascade needs it).
            documentId = (Guid)(await document.ExecuteScalarAsync(cancellationToken))!;
        }

        await tx.PublishAsync(jobId, IngestJobStatus.Processing, cancellationToken);
        await tx.CommitAsync();
        return new IngestJobClaim(jobId, documentId, attempts, maxChunkCharacters, logicalPath, sizeBytes);
    }

    /// <inheritdoc />
    public async Task<bool> HasActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();
        return await db.IngestJobs.AsNoTracking()
            .AnyAsync(j => j.Status == IngestJobStatus.Queued || j.Status == IngestJobStatus.Processing, cancellationToken);
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
    public async Task CompleteAsync(Guid jobId, IngestJobOutcome outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!IngestJob.IsTerminal(outcome.Status))
            throw new ArgumentException("A completed job must end Succeeded, Skipped or Failed.", nameof(outcome));

        var now = DateTime.UtcNow;
        var documentStatus = outcome.Status == IngestJobStatus.Failed ? IndexStatus.Failed : IndexStatus.Indexed;

        await using var tx = await JobEventTransaction.BeginAsync(_scopes, cancellationToken);
        await using var db = tx.CreateDbContext();
        // A job deleted with its document (cascade) updates nothing here, which is the intended quiet outcome.
        var updated = await db.IngestJobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, outcome.Status)
                .SetProperty(j => j.ChunkCount, outcome.ChunkCount)
                .SetProperty(j => j.Error, outcome.Error)
                .SetProperty(j => j.Content, (byte[]?)null)
                .SetProperty(j => j.CompletedAtUtc, (DateTime?)now),
                cancellationToken);
        if (updated > 0)
        {
            await db.Documents.Where(d => d.Jobs.Any(j => j.Id == jobId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.IndexStatus, documentStatus)
                    .SetProperty(d => d.UpdatedAtUtc, now),
                    cancellationToken);
            await tx.PublishAsync(jobId, outcome.Status, cancellationToken);
        }

        await tx.CommitAsync();
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

    /// <inheritdoc />
    public async Task<IngestJobPage> GetJobsAsync(
        int page, int pageSize, IngestJobStatus? status = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        await using var db = CreateContext();

        var jobs = db.IngestJobs.AsNoTracking();
        if (status is { } s)
            jobs = jobs.Where(j => j.Status == s);

        var total = await jobs.CountAsync(cancellationToken);

        // Projection leaves Content and the document's blobs/chunks out of the SELECT.
        var items = await jobs
            .OrderByDescending(j => j.CreatedAtUtc).ThenByDescending(j => j.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(j => new IngestJobListItemDto(
                j.Id, j.Document!.GlobalId, j.LogicalPath, j.Status, j.Attempts, j.MaxChunkCharacters, j.SizeBytes,
                j.ChunkCount, j.CreatedAtUtc, j.StartedAtUtc, j.CompletedAtUtc, j.Error))
            .ToListAsync(cancellationToken);

        return new IngestJobPage(items, page, pageSize, total);
    }
}
