using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>
/// Runs one ingest job end to end (see <see cref="IIngestJobService"/>): begins it, stages the stored bytes in a temp
/// file, ingests that file and records the terminal result. Problems with the file complete the job Failed (no retry).
/// Infrastructure errors propagate after a backoff so the bus redelivers the event; once the job's attempts are used up
/// they fail it instead. Shutdown leaves the job Processing (the bus redelivers after the message lock expires) and a
/// document delete (<see cref="IngestJobRegistry"/>) stops it quietly.
/// </summary>
/// <param name="timeProvider">Clock for the retry backoff; tests substitute it. Defaults to the system clock.</param>
public sealed class IngestJobService(
    IIngestQueue queue,
    IContentIngester ingester,
    IngestSettings settings,
    IngestJobRegistry registry,
    IIngestKeepAlive keepAlive,
    ILogger<IngestJobService> logger,
    TimeProvider? timeProvider = null) : IIngestJobService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var claim = await queue.BeginProcessingAsync(jobId, cancellationToken);
        if (claim is null)
        {
            logger.LogDebug("Ingest job {JobId} is gone or already finished; nothing to do", jobId);
            return;
        }

        if (claim.Attempts > settings.MaxAttempts)
        {
            // Redelivered after crashes too often: the process keeps dying on this job.
            var made = claim.Attempts - 1;
            logger.LogError("Ingest job {JobId} abandoned after {Attempts} attempt(s)", claim.JobId, made);
            await queue.CompleteAsync(
                claim.JobId,
                new IngestJobOutcome(IngestJobStatus.Failed, Error: $"The job was abandoned after {made} attempt(s)."),
                cancellationToken);
            return;
        }

        logger.LogInformation("Processing ingest job {JobId} of document {DocumentId} (attempt {Attempt})", claim.JobId, claim.DocumentId, claim.Attempts);

        var jobDirectory = Path.Combine(Path.GetTempPath(), "pbrag-ingest", claim.JobId.ToString("N"));
        // Cancelled by shutdown or by a document delete (the registry tells the two apart). Disposed last.
        using var active = registry.Begin(claim.JobId, claim.DocumentId, cancellationToken);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
        Task? heartbeat = null;

        try
        {
            var ct = active.Token;

            // Before the work starts: a host that scales to zero must not stop while this job runs.
            await MarkBusySafelyAsync(ct);
            heartbeat = Task.Run(() => HeartbeatAsync(heartbeatCts.Token), CancellationToken.None);

            var outcome = await IngestAsync(claim, jobDirectory, ct);
            await queue.CompleteAsync(claim.JobId, outcome, ct);
            logger.LogInformation("Completed ingest job {JobId}: {Status}", claim.JobId, outcome.Status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown: leave the job Processing; the bus redelivers once the message lock expires.
            logger.LogInformation("Ingest job {JobId} interrupted by shutdown", claim.JobId);
            throw;
        }
        catch (Exception) when (active.CancelRequested)
        {
            // The document is being deleted: nothing to record (whatever the cancelled call surfaced as).
            logger.LogInformation("Ingest job {JobId} cancelled because document {DocumentId} is being deleted", claim.JobId, claim.DocumentId);
        }
        catch (DocumentNotFoundException)
        {
            // Deleted between the begin and the write: nothing to record, the cascade removed the job.
            logger.LogInformation("Ingest job {JobId} stopped: document {DocumentId} no longer exists", claim.JobId, claim.DocumentId);
        }
        catch (Exception ex)
        {
            await HandleInfrastructureFailureAsync(claim, ex, active);
        }
        finally
        {
            heartbeatCts.Cancel();
            if (heartbeat is not null)
                await heartbeat;
            TryDeleteDirectory(jobDirectory);
        }
    }

    private async Task<IngestJobOutcome> IngestAsync(IngestJobClaim claim, string jobDirectory, CancellationToken cancellationToken)
    {
        // The temp file keeps the original extension (modality and reader selection are extension based).
        Directory.CreateDirectory(jobDirectory);
        var tempPath = Path.Combine(jobDirectory, $"content{Path.GetExtension(claim.LogicalPath)}");

        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            // Errors reading the queue propagate: they are infrastructure problems (retried), not the file's.
            await queue.ReadContentAsync(claim.JobId, stream, cancellationToken);
        }

        var options = new IngestOptions { MaxFileSizeBytes = settings.MaxFileSizeBytes, MaxChunkCharacters = claim.MaxChunkCharacters };
        try
        {
            var result = await ingester.IngestAsync(new IngestSource(tempPath, claim.LogicalPath, claim.DocumentId), options, cancellationToken);
            return new IngestJobOutcome(result.ChunkCount == 0 ? IngestJobStatus.Skipped : IngestJobStatus.Succeeded, result.ChunkCount);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or DocumentNotFoundException))
        {
            // A problem with this file (unreadable, too large, ...): recorded, not retried.
            logger.LogWarning(ex, "Ingesting {LogicalPath} failed", claim.LogicalPath);
            return new IngestJobOutcome(IngestJobStatus.Failed, Error: ex.Message);
        }
    }

    /// <summary>
    /// Fails the job on its last attempt, otherwise waits a growing backoff and rethrows so the bus redelivers
    /// (the next begin increments the attempts).
    /// </summary>
    private async Task HandleInfrastructureFailureAsync(IngestJobClaim claim, Exception ex, ActiveIngestJob active)
    {
        logger.LogError(ex, "Ingest job {JobId} failed on attempt {Attempt}", claim.JobId, claim.Attempts);

        if (claim.Attempts >= settings.MaxAttempts)
        {
            try
            {
                await queue.CompleteAsync(claim.JobId, new IngestJobOutcome(IngestJobStatus.Failed, Error: ex.Message), CancellationToken.None);
                logger.LogInformation("Ingest job {JobId} is now Failed", claim.JobId);
            }
            catch (Exception failEx)
            {
                logger.LogError(failEx, "Could not record the failure of ingest job {JobId}", claim.JobId);
                throw;
            }

            return;
        }

        try
        {
            await Task.Delay(Backoff(settings.PollInterval, claim.Attempts), _time, active.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutdown propagates (the job stays Processing); a delete during the backoff stops the job quietly.
            if (!active.CancelRequested)
                throw;
            return;
        }

        // Redeliver: rethrow the original failure with its stack.
        ExceptionDispatchInfo.Capture(ex).Throw();
    }

    /// <summary>Refreshes the keep-alive marker while the job runs (a no-op until it is due). Never throws.</summary>
    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromTicks(Math.Max(settings.Lease.Ticks / 3, TimeSpan.TicksPerSecond)));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await MarkBusySafelyAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Job finished or shutting down.
        }
    }

    private async Task MarkBusySafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await keepAlive.MarkBusyAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not refresh the ingest keep-alive");
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete temp directory {Path}", path);
        }
    }

    private static TimeSpan Backoff(TimeSpan baseDelay, int attempts)
    {
        var factor = Math.Pow(2, Math.Min(Math.Max(attempts - 1, 0), 10));
        return TimeSpan.FromTicks((long)Math.Min(baseDelay.Ticks * factor, MaxBackoff.Ticks));
    }
}
