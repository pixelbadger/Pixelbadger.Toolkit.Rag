using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>
/// Processes queued ingest jobs, one at a time (one job is one file for one document; a single instance
/// owns the queue). Never lets an exception escape the loop: SQL errors are
/// logged and retried with a growing delay, so a database outage cannot crash the host.
/// </summary>
public sealed class IngestWorker(
    IServiceScopeFactory scopes,
    IngestSettings settings,
    IngestWorkerSignal signal,
    IngestJobRegistry registry,
    InFlightJobRecovery recovery,
    IIngestKeepAlive keepAlive,
    ILogger<IngestWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    // Set after a job indexed something; the vector index is (re)built once the queue is idle, not after every job.
    private bool _vectorIndexStale;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessNextAsync(stoppingToken);
                consecutiveFailures = 0;
                if (!processed)
                {
                    await IdleAsync(stoppingToken);
                    await signal.WaitAsync(settings.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var delay = Backoff(settings.PollInterval, consecutiveFailures);
                logger.LogError(ex, "Ingest worker could not process the queue; retrying in {Delay}", delay);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Claims and processes at most one job. Returns false when nothing was claimable.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        // Jobs left Processing by a previous process must be requeued before anything is claimed (no-op once done).
        await recovery.EnsureAsync(cancellationToken);

        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IIngestQueue>();

        var claim = await queue.TryClaimNextAsync(_owner, settings.Lease, cancellationToken);
        if (claim is null)
            return false;

        // Before the work starts: a host that scales to zero must not stop while this job runs.
        await keepAlive.MarkBusyAsync(cancellationToken);

        await RunJobAsync(scope.ServiceProvider, queue, claim, cancellationToken);
        return true;
    }

    /// <summary>
    /// The queue is empty: build the vector index if needed, then let the host scale down (the worker loop calls this
    /// whenever nothing was claimable).
    /// </summary>
    public async Task IdleAsync(CancellationToken cancellationToken)
    {
        await EnsureVectorIndexIfStaleAsync(cancellationToken);
        await keepAlive.MarkIdleAsync(cancellationToken);
    }

    /// <summary>Builds the vector index if jobs indexed something since the last time (the worker loop calls this when the queue is idle).</summary>
    public async Task EnsureVectorIndexIfStaleAsync(CancellationToken cancellationToken)
    {
        if (!_vectorIndexStale)
            return;

        await using var scope = scopes.CreateAsyncScope();
        // Idempotent; creates the vector index when enough rows exist.
        await scope.ServiceProvider.GetRequiredService<IDocumentStore>().EnsureVectorIndexAsync(cancellationToken);
        _vectorIndexStale = false;
    }

    private async Task RunJobAsync(IServiceProvider services, IIngestQueue queue, IngestJobClaim claim, CancellationToken stoppingToken)
    {
        logger.LogInformation("Processing ingest job {JobId} of document {DocumentId} (attempt {Attempt})", claim.JobId, claim.DocumentId, claim.Attempts);

        var jobDirectory = Path.Combine(Path.GetTempPath(), "pbrag-ingest", claim.JobId.ToString("N"));
        // Cancelled by shutdown or by a document delete (the registry tells the two apart).
        using var active = registry.Begin(claim.JobId, claim.DocumentId, stoppingToken);
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
        var leaseLost = false;
        var heartbeat = Task.Run(async () =>
        {
            leaseLost = await HeartbeatAsync(queue, claim.JobId, jobCts.Token);
            if (leaseLost)
                jobCts.Cancel();
        }, CancellationToken.None);

        try
        {
            var ct = jobCts.Token;
            var ingester = services.GetRequiredService<IContentIngester>();
            var options = new IngestOptions { MaxFileSizeBytes = settings.MaxFileSizeBytes, MaxChunkCharacters = claim.MaxChunkCharacters };

            var outcome = await IngestAsync(queue, ingester, options, jobDirectory, claim, ct);
            await queue.CompleteAsync(claim.JobId, outcome, ct);
            _vectorIndexStale |= outcome.Status != IngestJobStatus.Failed;
            logger.LogInformation("Completed ingest job {JobId}: {Status}", claim.JobId, outcome.Status);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: leave the job Processing; its lease expires (or the startup reset requeues it) and it is retried.
            logger.LogInformation("Ingest job {JobId} interrupted by shutdown", claim.JobId);
        }
        catch (Exception) when (active.CancelRequested)
        {
            // The document is being deleted: no requeue, no failure (whatever the cancelled call surfaced as).
            // Deleting the document removes the job.
            logger.LogInformation("Ingest job {JobId} cancelled because document {DocumentId} is being deleted", claim.JobId, claim.DocumentId);
        }
        catch (OperationCanceledException) when (leaseLost)
        {
            logger.LogWarning("Ingest job {JobId} lost its lease and was abandoned", claim.JobId);
        }
        catch (DocumentNotFoundException)
        {
            // Deleted between the claim and the write: nothing to record, the cascade removed the job.
            logger.LogInformation("Ingest job {JobId} stopped: document {DocumentId} no longer exists", claim.JobId, claim.DocumentId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ingest job {JobId} failed on attempt {Attempt}", claim.JobId, claim.Attempts);
            try
            {
                var status = await queue.FailAsync(claim.JobId, ex.Message, CancellationToken.None);
                logger.LogInformation("Ingest job {JobId} is now {Status}", claim.JobId, status);
            }
            catch (Exception failEx)
            {
                logger.LogError(failEx, "Could not record the failure of ingest job {JobId}; its lease will expire", claim.JobId);
            }
        }
        finally
        {
            jobCts.Cancel();
            await heartbeat;
            TryDeleteDirectory(jobDirectory);
            // Last: a delete waiting on this job may proceed (and expects the temp files to be gone).
            active.Dispose();
        }
    }

    private async Task<IngestJobOutcome> IngestAsync(
        IIngestQueue queue,
        IContentIngester ingester,
        IngestOptions options,
        string jobDirectory,
        IngestJobClaim claim,
        CancellationToken cancellationToken)
    {
        // The temp file keeps the original extension (modality and reader selection are extension based).
        Directory.CreateDirectory(jobDirectory);
        var tempPath = Path.Combine(jobDirectory, $"content{Path.GetExtension(claim.LogicalPath)}");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                // Errors reading the queue propagate: they fail the job (retried), not the file.
                await queue.ReadContentAsync(claim.JobId, stream, cancellationToken);
            }

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
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort; the job directory is removed at the end */ }
        }
    }

    /// <summary>Extends the lease until cancelled. Returns true when the lease was lost to someone else.</summary>
    private async Task<bool> HeartbeatAsync(IIngestQueue queue, Guid jobId, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromTicks(Math.Max(settings.Lease.Ticks / 3, TimeSpan.TicksPerSecond)));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    if (!await queue.ExtendLeaseAsync(jobId, _owner, settings.Lease, cancellationToken))
                        return true;
                    // A long job outlives the keep-alive marker's time-to-live: refresh it (a no-op until it is due).
                    await keepAlive.MarkBusyAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not extend the lease of ingest job {JobId}", jobId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Job finished or shutting down.
        }

        return false;
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

    private static TimeSpan Backoff(TimeSpan baseDelay, int failures)
    {
        var factor = Math.Pow(2, Math.Min(failures, 10));
        var delay = TimeSpan.FromTicks((long)Math.Min(baseDelay.Ticks * factor, MaxBackoff.Ticks));
        return delay < baseDelay ? baseDelay : delay;
    }
}
