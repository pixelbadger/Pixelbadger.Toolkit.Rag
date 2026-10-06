using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>
/// Puts jobs left Processing by a previous process back to Queued (<see cref="IIngestQueue.ResetInFlightJobsAsync"/>),
/// exactly once per process, and before the worker claims anything: the startup step runs it, and the worker calls
/// <see cref="EnsureAsync"/> before every claim so that it also happens if the startup attempt failed (SQL was down).
/// </summary>
/// <remarks>
/// Single-process assumption: see <see cref="IIngestQueue.ResetInFlightJobsAsync"/>.
/// </remarks>
public sealed class InFlightJobRecovery(IServiceScopeFactory scopes, ILogger<InFlightJobRecovery> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _done;

    /// <summary>Runs the reset if it has not succeeded yet. Throws when SQL fails; the caller decides what to do.</summary>
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (_done)
            return;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_done)
                return;

            await using var scope = scopes.CreateAsyncScope();
            var reset = await scope.ServiceProvider.GetRequiredService<IIngestQueue>().ResetInFlightJobsAsync(cancellationToken);
            _done = true;
            logger.LogInformation("Reset {Count} in-flight ingest job(s) from Processing to Queued", reset);
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Runs the in-flight job reset at startup, after <see cref="DatabaseMigrationHostedService"/> and before
/// <see cref="IngestWorker"/> (hosted services start in registration order). Runs whether or not migrations are
/// enabled. When SQL is unreachable it only logs: the worker retries with backoff and repeats the reset first.
/// </summary>
public sealed class InFlightJobRecoveryHostedService(InFlightJobRecovery recovery, ILogger<InFlightJobRecoveryHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await recovery.EnsureAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not reset in-flight ingest jobs at startup; the ingest worker will retry before it claims anything");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
