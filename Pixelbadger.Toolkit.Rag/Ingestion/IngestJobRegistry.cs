namespace Pixelbadger.Toolkit.Rag.Ingestion;

/// <summary>
/// Tracks the ingest job the worker is processing right now, so a document delete can cancel it and wait for the
/// worker to let go of it. In-process only.
/// </summary>
/// <remarks>
/// Single-process assumption: there is one worker in this process, so at most one job is active. If the app is ever
/// scaled out, cancellation has to travel through shared state (e.g. the database) instead.
/// </remarks>
public sealed class IngestJobRegistry
{
    private readonly object _gate = new();
    private ActiveIngestJob? _current;

    /// <summary>
    /// Registers the job the worker is about to process. The returned handle's <see cref="ActiveIngestJob.Token"/> is
    /// cancelled on shutdown (<paramref name="stoppingToken"/>) or by <see cref="CancelDocument"/>. The worker must
    /// dispose the handle when it has finished with the job, whatever the outcome.
    /// </summary>
    public ActiveIngestJob Begin(Guid jobId, Guid documentId, CancellationToken stoppingToken)
    {
        var job = new ActiveIngestJob(this, jobId, documentId, stoppingToken);
        lock (_gate)
        {
            _current = job;
        }

        return job;
    }

    /// <summary>
    /// Cancels the active job if it belongs to <paramref name="documentId"/>. Returns a task that completes when the
    /// worker has finished with the job (success, failure or cancellation), or null when none is active for it.
    /// </summary>
    public Task? CancelDocument(Guid documentId)
    {
        ActiveIngestJob? job;
        lock (_gate)
        {
            job = _current is { } current && current.DocumentId == documentId ? current : null;
        }

        return job?.Cancel();
    }

    internal void Release(ActiveIngestJob job)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, job))
                _current = null;
        }
    }
}

/// <summary>The job a worker is processing. Dispose it when the worker has finished with the job.</summary>
public sealed class ActiveIngestJob : IDisposable
{
    private readonly IngestJobRegistry _registry;
    private readonly CancellationTokenSource _cts;
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _cancelRequested;

    internal ActiveIngestJob(IngestJobRegistry registry, Guid jobId, Guid documentId, CancellationToken stoppingToken)
    {
        _registry = registry;
        JobId = jobId;
        DocumentId = documentId;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    }

    public Guid JobId { get; }

    public Guid DocumentId { get; }

    /// <summary>Cancelled on host shutdown or when the document is deleted.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>True when the cancellation came from <see cref="IngestJobRegistry.CancelDocument"/> (the document is being deleted).</summary>
    public bool CancelRequested => Volatile.Read(ref _cancelRequested) != 0;

    /// <summary>Completes when the worker has finished with the job.</summary>
    public Task Finished => _finished.Task;

    internal Task Cancel()
    {
        Interlocked.Exchange(ref _cancelRequested, 1);
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }

        return _finished.Task;
    }

    public void Dispose()
    {
        _registry.Release(this);
        _finished.TrySetResult();
        _cts.Dispose();
    }
}
