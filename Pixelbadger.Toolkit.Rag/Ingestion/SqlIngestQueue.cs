using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>
/// <see cref="IIngestQueue"/> on SQL Server (EF Core for the simple operations, raw SQL for the atomic claim and
/// for streaming file bytes). The claim uses <c>UPDLOCK, READPAST</c> so concurrent claimers skip each other's
/// rows instead of blocking or double-claiming.
/// </summary>
public sealed class SqlIngestQueue : IIngestQueue
{
    private const string JobsTableSql = "dbo." + RagDbContext.IngestJobsTable;
    private const string FilesTableSql = "dbo." + RagDbContext.IngestJobFilesTable;

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
    public async Task<Guid> EnqueueAsync(IReadOnlyList<IngestUpload> files, int maxChunkCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        await using var db = CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var job = new IngestJob
        {
            Id = Guid.CreateVersion7(),
            Status = IngestJobStatus.Queued,
            MaxChunkCharacters = maxChunkCharacters,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.IngestJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        // One file at a time, detached after each insert, so only one file's bytes are in memory at once.
        for (var i = 0; i < files.Count; i++)
        {
            byte[] content;
            await using (var source = files[i].OpenRead())
            using (var buffer = new MemoryStream())
            {
                await source.CopyToAsync(buffer, cancellationToken);
                content = buffer.ToArray();
            }

            var file = new IngestJobFile
            {
                JobId = job.Id,
                Ordinal = i,
                LogicalPath = files[i].LogicalPath,
                Content = content,
                SizeBytes = content.Length,
                Status = IngestFileStatus.Queued
            };
            db.IngestJobFiles.Add(file);
            await db.SaveChangesAsync(cancellationToken);
            db.Entry(file).State = EntityState.Detached;
        }

        await transaction.CommitAsync(cancellationToken);
        return job.Id;
    }

    /// <inheritdoc />
    public async Task<IngestJobClaim?> TryClaimNextAsync(string owner, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        // 1. Jobs abandoned with no attempts left become Failed (and drop their stored bytes).
        // 2. Claim the oldest claimable job (CTE so ORDER BY applies to the single updated row).
        const string sql = $"""
            SET NOCOUNT ON;
            DECLARE @dead TABLE (Id uniqueidentifier NOT NULL);

            UPDATE {JobsTableSql}
            SET Status = @failed, CompletedAtUtc = @now, LeaseOwner = NULL, LeaseExpiresAtUtc = NULL,
                Error = COALESCE(Error, N'The job was abandoned after ' + CAST(Attempts AS nvarchar(11)) + N' attempt(s).')
            OUTPUT inserted.Id INTO @dead
            WHERE Status = @processing AND LeaseExpiresAtUtc < @now AND Attempts >= @maxAttempts;

            UPDATE {FilesTableSql}
            SET Status = @fileFailed, Content = NULL, Error = N'The job failed before this file was processed.'
            WHERE JobId IN (SELECT Id FROM @dead) AND Status = @fileQueued;

            ;WITH next AS (
                SELECT TOP (1) *
                FROM {JobsTableSql} WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Status = @queued
                   OR (Status = @processing AND LeaseExpiresAtUtc < @now AND Attempts < @maxAttempts)
                ORDER BY CreatedAtUtc, Id
            )
            UPDATE next
            SET Status = @processing, Attempts = Attempts + 1, LeaseOwner = @owner, LeaseExpiresAtUtc = @leaseUntil,
                StartedAtUtc = COALESCE(StartedAtUtc, @now), Error = NULL
            OUTPUT inserted.Id, inserted.Attempts, inserted.MaxChunkCharacters;
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
        command.Parameters.AddWithValue("@fileQueued", (int)IngestFileStatus.Queued);
        command.Parameters.AddWithValue("@fileFailed", (int)IngestFileStatus.Failed);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new IngestJobClaim(reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingIngestFile>> GetPendingFilesAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();
        return await db.IngestJobFiles.AsNoTracking()
            .Where(f => f.JobId == jobId && f.Status == IngestFileStatus.Queued)
            .OrderBy(f => f.Ordinal)
            .Select(f => new PendingIngestFile(f.Id, f.Ordinal, f.LogicalPath, f.SizeBytes))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReadFileContentAsync(int fileId, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand($"SELECT Content FROM {FilesTableSql} WHERE Id = @id", connection);
        command.Parameters.AddWithValue("@id", fileId);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || await reader.IsDBNullAsync(0, cancellationToken))
            throw new InvalidOperationException($"Stored content for ingest file {fileId} is not available.");

        await using var content = reader.GetStream(0);
        await content.CopyToAsync(destination, cancellationToken);
    }

    /// <inheritdoc />
    public async Task CompleteFileAsync(int fileId, IngestFileOutcome outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        await using var db = CreateContext();
        await db.IngestJobFiles.Where(f => f.Id == fileId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.Status, outcome.Status)
                .SetProperty(f => f.DocumentGlobalId, outcome.DocumentGlobalId)
                .SetProperty(f => f.Modality, outcome.Modality)
                .SetProperty(f => f.ChunkCount, outcome.ChunkCount)
                .SetProperty(f => f.Error, outcome.Error)
                .SetProperty(f => f.Content, (byte[]?)null),
                cancellationToken);
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
    public async Task CompleteJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var db = CreateContext();
        await db.IngestJobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, IngestJobStatus.Completed)
                .SetProperty(j => j.CompletedAtUtc, (DateTime?)now)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IngestJobStatus> FailJobAsync(Guid jobId, string error, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var maxAttempts = _settings.MaxAttempts;
        await using var db = CreateContext();

        await db.IngestJobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, j => j.Attempts >= maxAttempts ? IngestJobStatus.Failed : IngestJobStatus.Queued)
                .SetProperty(j => j.CompletedAtUtc, j => j.Attempts >= maxAttempts ? now : (DateTime?)null)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null),
                cancellationToken);

        var status = await db.IngestJobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => (IngestJobStatus?)j.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (status is null)
            throw new InvalidOperationException($"Ingest job {jobId} not found.");

        if (status == IngestJobStatus.Failed)
        {
            // Terminal: drop the bytes of files that were never processed.
            await db.IngestJobFiles.Where(f => f.JobId == jobId && f.Status == IngestFileStatus.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.Status, IngestFileStatus.Failed)
                    .SetProperty(f => f.Error, "The job failed before this file was processed.")
                    .SetProperty(f => f.Content, (byte[]?)null),
                    cancellationToken);
        }

        return status.Value;
    }

    /// <inheritdoc />
    public async Task<IngestJobStatusDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext();

        var job = await db.IngestJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null)
            return null;

        // Projection leaves Content out of the SELECT.
        var files = await db.IngestJobFiles.AsNoTracking()
            .Where(f => f.JobId == jobId)
            .OrderBy(f => f.Ordinal)
            .Select(f => new IngestFileStatusDto(f.LogicalPath, f.Status, f.DocumentGlobalId, f.Modality, f.ChunkCount, f.Error))
            .ToListAsync(cancellationToken);

        var summary = new IngestJobSummaryDto(
            files.Count(f => f.Status == IngestFileStatus.Succeeded),
            files.Count(f => f.Status == IngestFileStatus.Failed),
            files.Count(f => f.Status == IngestFileStatus.Skipped));

        return new IngestJobStatusDto(
            job.Id, job.Status, job.Attempts, job.CreatedAtUtc, job.StartedAtUtc, job.CompletedAtUtc, job.Error, files, summary);
    }
}
