using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pixelbadger.Toolkit.Rag.Components;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Dtos;
using Pixelbadger.Toolkit.Rag.Persistence;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>
/// Processes queued ingest jobs, one at a time and one file at a time (Lucene allows a single writer, and a
/// single instance owns the index directory). Never lets an exception escape the loop: SQL errors are logged
/// and retried with a growing delay, so a database outage cannot crash the host.
/// </summary>
public sealed class IngestWorker(
    IServiceScopeFactory scopes,
    IngestSettings settings,
    IngestWorkerSignal signal,
    ILogger<IngestWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

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
                    await signal.WaitAsync(settings.PollInterval, stoppingToken);
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
        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IIngestQueue>();

        var claim = await queue.TryClaimNextAsync(_owner, settings.Lease, cancellationToken);
        if (claim is null)
            return false;

        await RunJobAsync(scope.ServiceProvider, queue, claim, cancellationToken);
        return true;
    }

    private async Task RunJobAsync(IServiceProvider services, IIngestQueue queue, IngestJobClaim claim, CancellationToken stoppingToken)
    {
        logger.LogInformation("Processing ingest job {JobId} (attempt {Attempt})", claim.JobId, claim.Attempts);

        var jobDirectory = Path.Combine(Path.GetTempPath(), "pbrag-ingest", claim.JobId.ToString("N"));
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
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

            // Files already terminal (from an earlier attempt) are not returned.
            foreach (var file in await queue.GetPendingFilesAsync(claim.JobId, ct))
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await IngestFileAsync(queue, ingester, options, jobDirectory, file, ct);
                await queue.CompleteFileAsync(file.FileId, outcome, ct);
            }

            // Once per job (idempotent; creates the vector index when enough rows exist).
            await services.GetRequiredService<IDocumentStore>().EnsureVectorIndexAsync(ct);
            await queue.CompleteJobAsync(claim.JobId, ct);
            logger.LogInformation("Completed ingest job {JobId}", claim.JobId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: leave the job Processing; its lease expires and it is retried (finished files stay finished).
            logger.LogInformation("Ingest job {JobId} interrupted by shutdown", claim.JobId);
        }
        catch (OperationCanceledException) when (leaseLost)
        {
            logger.LogWarning("Ingest job {JobId} lost its lease and was abandoned", claim.JobId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ingest job {JobId} failed on attempt {Attempt}", claim.JobId, claim.Attempts);
            try
            {
                var status = await queue.FailJobAsync(claim.JobId, ex.Message, CancellationToken.None);
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
        }
    }

    private async Task<IngestFileOutcome> IngestFileAsync(
        IIngestQueue queue,
        IContentIngester ingester,
        IngestOptions options,
        string jobDirectory,
        PendingIngestFile file,
        CancellationToken cancellationToken)
    {
        // The temp file keeps the original extension (modality and reader selection are extension based).
        Directory.CreateDirectory(jobDirectory);
        var tempPath = Path.Combine(jobDirectory, $"file-{file.Ordinal}{Path.GetExtension(file.LogicalPath)}");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                // Errors reading the queue propagate: they fail the job (retried), not the file.
                await queue.ReadFileContentAsync(file.FileId, stream, cancellationToken);
            }

            try
            {
                var result = await ingester.IngestAsync(new IngestSource(tempPath, file.LogicalPath), options, cancellationToken);
                return new IngestFileOutcome(
                    result.ChunkCount == 0 ? IngestFileStatus.Skipped : IngestFileStatus.Succeeded,
                    result.DocumentId, result.Modality, result.ChunkCount);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ingesting {LogicalPath} failed", file.LogicalPath);
                return new IngestFileOutcome(IngestFileStatus.Failed, Error: ex.Message);
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
