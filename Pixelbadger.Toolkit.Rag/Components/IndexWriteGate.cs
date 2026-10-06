namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>
/// Serialises "SQL write + Lucene write" for one document (replace on ingest, delete on DELETE) so a delete that
/// lands between an ingest's SQL and Lucene steps cannot leave Lucene entries behind for a deleted document, and
/// Lucene's single writer is never contended inside this process.
/// </summary>
/// <remarks>
/// Single-process assumption: this is an in-process lock, which only works because one instance owns the Lucene
/// index directory. If the app is ever scaled out (shared index / multiple workers), replace it with a
/// cross-process mechanism.
/// </remarks>
public sealed class IndexWriteGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Waits for the gate; dispose the result to release it.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
