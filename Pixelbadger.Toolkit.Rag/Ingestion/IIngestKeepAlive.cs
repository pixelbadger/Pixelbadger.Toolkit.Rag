namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>
/// Tells the hosting platform whether ingest has work, so a host that scales to zero keeps running until
/// the queue is drained. On Azure Container Apps this is a marker message in a storage queue that a queue-length
/// scale rule watches (<see cref="QueueIngestKeepAlive"/>); elsewhere it does nothing (<see cref="NoIngestKeepAlive"/>).
/// Implementations log and swallow their own failures: keeping the host alive must never fail a job.
/// </summary>
public interface IIngestKeepAlive
{
    /// <summary>A job is starting or still running: keep the host running.</summary>
    Task MarkBusyAsync(CancellationToken cancellationToken);

    /// <summary>The queue is empty: the host may scale down.</summary>
    Task MarkIdleAsync(CancellationToken cancellationToken);
}

/// <summary>The default when no keep-alive queue is configured.</summary>
public sealed class NoIngestKeepAlive : IIngestKeepAlive
{
    public Task MarkBusyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task MarkIdleAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
